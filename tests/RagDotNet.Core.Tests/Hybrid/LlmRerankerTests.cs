using System.Text.RegularExpressions;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests.Hybrid;

public class LlmRerankerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A chat client answering through a function of the request.</summary>
    private sealed class ScriptChat(Func<IList<ChatMessage>, CancellationToken, Task<string>> reply) : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public List<IList<ChatMessage>> Requests { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (Requests) Requests.Add(list);
            Interlocked.Increment(ref _calls);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, await reply(list, cancellationToken)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static int Passages(IList<ChatMessage> request) => Regex.Matches(request.Last().Text, @"^\[\d+\]", RegexOptions.Multiline).Count;

    private static string Fives(IList<ChatMessage> request) => "[" + string.Join(", ", Enumerable.Repeat(5, Passages(request))) + "]";

    private static List<RetrievedChunk> Candidates(int n) =>
        Enumerable.Range(0, n).Select(i => new RetrievedChunk($"d{i}:0", $"d{i}", 0, $"passage number {i}", 1.0 / (i + 1), ScoreKind.Fused, null, null, [], "", new Dictionary<string, object?>())).ToList();

    [Fact]
    public async Task Sends_candidates_in_groups_of_BatchSize()
    {
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));
        var reranker = new LlmReranker(chat);

        var results = await reranker.RerankAsync("question", Candidates(25), 25, Ct);

        Assert.Equal(3, chat.Calls);
        Assert.Equal([10, 10, 5], chat.Requests.Select(Passages).Order().Reverse());
        Assert.Equal(25, results.Count);
        Assert.Equal(Enumerable.Range(0, 25), results.Select(r => r.Index)); // all ties: input order
    }

    [Fact]
    public async Task At_most_MaxConcurrency_calls_at_once()
    {
        var current = 0;
        var max = 0;
        var chat = new ScriptChat(async (r, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref max, now);
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref current);
            return Fives(r);
        });
        var reranker = new LlmReranker(chat, new LlmRerankerOptions { BatchSize = 1, MaxConcurrency = 3 });

        await reranker.RerankAsync("question", Candidates(10), 10, Ct);

        Assert.Equal(10, chat.Calls);
        Assert.InRange(max, 2, 3);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Fact]
    public async Task Parses_scores_and_divides_by_10_with_ties_in_input_order()
    {
        var chat = new ScriptChat((_, _) => Task.FromResult("Here you go: [3, 10, 3, 0] hope it helps"));
        var reranker = new LlmReranker(chat);

        var all = await reranker.RerankAsync("question", Candidates(4), 4, Ct);
        var top = await new LlmReranker(chat).RerankAsync("question", Candidates(4), 2, Ct);

        Assert.Equal([new RerankResult(1, 1.0), new RerankResult(0, 0.3), new RerankResult(2, 0.3), new RerankResult(3, 0.0)], all);
        Assert.Equal([1, 0], top.Select(r => r.Index));
    }

    [Fact]
    public async Task Prompt_numbers_passages_and_includes_the_question()
    {
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));

        await new LlmReranker(chat).RerankAsync("What is zorblax?", Candidates(2), 2, Ct);

        var user = chat.Requests[0].Last().Text;
        Assert.Contains("What is zorblax?", user);
        Assert.Contains("[1] passage number 0", user);
        Assert.Contains("[2] passage number 1", user);
        Assert.Contains("JSON array", chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task Truncates_candidates_to_MaxCandidateTokens()
    {
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));
        var long_ = Candidates(1)[0] with { Text = string.Join(' ', Enumerable.Range(0, 200).Select(i => "word" + i)) };

        await new LlmReranker(chat, new LlmRerankerOptions { MaxCandidateTokens = 6 }).RerankAsync("q", [long_], 1, Ct);

        var user = chat.Requests[0].Last().Text;
        Assert.Contains("word0", user);
        Assert.DoesNotContain("word10", user);
    }

    [Fact]
    public async Task Unparseable_reply_is_retried_once_then_falls_back()
    {
        var replies = new Queue<string>(["I cannot score these.", "[4]"]);
        var chat = new ScriptChat((_, _) => Task.FromResult(replies.Dequeue()));
        var ok = await new LlmReranker(chat).RerankAsync("q", Candidates(1), 1, Ct);
        Assert.Equal(2, chat.Calls);
        Assert.Equal(0.4, Assert.Single(ok).Score);

        var never = new ScriptChat((_, _) => Task.FromResult("no idea"));
        await Assert.ThrowsAsync<RerankFallbackException>(() => new LlmReranker(never).RerankAsync("q", Candidates(3), 3, Ct));
        Assert.Equal(2, never.Calls);
    }

    [Fact]
    public async Task Pipeline_falls_back_to_fused_results_when_the_model_cannot_score()
    {
        var never = new ScriptChat((_, _) => Task.FromResult("no idea"));
        var (rag, _, _, _, _) = Create(builder: b => b.UseReranker(new LlmReranker(never)));
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "beta gamma"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("zorblax", cancellationToken: Ct);

        Assert.Equal("a", results[0].DocumentId);
        Assert.All(results, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("[1, 2, 3, 4]")]
    [InlineData("[11, 2, 3]")]
    [InlineData("[-1, 2, 3]")]
    [InlineData("[\"a\", 2, 3]")]
    [InlineData("[1, 2, 3")]
    public async Task Wrong_number_or_range_of_scores_counts_as_unparseable(string reply)
    {
        var chat = new ScriptChat((_, _) => Task.FromResult(reply));

        await Assert.ThrowsAsync<RerankFallbackException>(() => new LlmReranker(chat).RerankAsync("q", Candidates(3), 3, Ct));
        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task Custom_Instructions_replace_the_scoring_instruction()
    {
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));
        var plain = new ScriptChat((r, _) => Task.FromResult(Fives(r)));

        await new LlmReranker(chat, new LlmRerankerOptions { Instructions = "Prefer passages citing clause numbers." }).RerankAsync("q", Candidates(1), 1, Ct);
        await new LlmReranker(plain).RerankAsync("q", Candidates(1), 1, Ct);

        var system = chat.Requests[0][0].Text;
        Assert.Contains("Prefer passages citing clause numbers.", system);
        Assert.Contains("JSON array", system);
        var defaultInstruction = plain.Requests[0][0].Text.Split('\n')[0];
        Assert.DoesNotContain(defaultInstruction, system);
    }

    [Fact]
    public async Task Chat_errors_and_cancellation_propagate()
    {
        var failing = new ScriptChat((_, _) => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new LlmReranker(failing).RerankAsync("q", Candidates(3), 3, Ct));

        using var cts = new CancellationTokenSource();
        var slow = new ScriptChat(async (r, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Fives(r);
        });
        var task = new LlmReranker(slow).RerankAsync("q", Candidates(3), 3, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData(0, 4, 512)]
    [InlineData(10, 0, 512)]
    [InlineData(10, 4, 0)]
    public void Options_below_1_are_rejected(int batch, int concurrency, int tokens)
    {
        var options = new LlmRerankerOptions { BatchSize = batch, MaxConcurrency = concurrency, MaxCandidateTokens = tokens };

        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmReranker(new FakeChatClient(), options));
    }

    [Fact]
    public void Builder_without_chat_client_throws()
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .AddLlmReranker();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Equal("AddLlmReranker needs a chat client. Call UseChatClient() first.", ex.Message);

        var (rag, _, _, _, _) = Create(builder: b => b.AddLlmReranker(o => o.BatchSize = 5));
        Assert.IsType<LlmReranker>(((RagPipeline)rag).Engine.Reranker);
    }

    [Fact]
    public async Task Di_without_chat_client_fails_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        builder.Services.AddRag(o => o.EmbeddingDimensions = 64).AddLlmReranker();
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));
        Assert.Contains("IChatClient", ex.Message);
    }

    [Fact]
    public void Di_with_chat_client_registers_the_LlmReranker()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        services.AddSingleton<IChatClient>(new FakeChatClient());
        services.AddRag(o => o.EmbeddingDimensions = 64).AddLlmReranker(o => o.MaxConcurrency = 2);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<LlmReranker>(provider.GetRequiredService<RagEngine>().Reranker);
        Assert.Equal(2, provider.GetRequiredService<IOptions<LlmRerankerOptions>>().Value.MaxConcurrency);
    }
}
