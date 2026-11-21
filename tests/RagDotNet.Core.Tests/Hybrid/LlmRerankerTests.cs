using System.Text.RegularExpressions;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
    private sealed class ScriptChat(Func<IList<ChatMessage>, CancellationToken, Task<string>> reply, Func<IList<ChatMessage>, long?>? inputTokens = null, ChatFinishReason? finish = null) : IChatClient
    {
        private int _calls;

        public int Calls => _calls;

        public List<IList<ChatMessage>> Requests { get; } = [];

        public List<ChatOptions?> Options { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (Requests)
            {
                Requests.Add(list);
                Options.Add(options);
            }
            Interlocked.Increment(ref _calls);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, await reply(list, cancellationToken)));
            if (inputTokens?.Invoke(list) is { } tokens) response.Usage = new UsageDetails { InputTokenCount = tokens };
            response.FinishReason = finish;
            return response;
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
        var reranker = new LlmReranker(chat, new LlmRerankerOptions { BatchSize = 10 });

        var results = await reranker.RerankAsync("question", Candidates(25), 25, Ct);

        Assert.Equal(3, chat.Calls);
        Assert.Equal([10, 10, 5], chat.Requests.Select(Passages).Order().Reverse());
        Assert.Equal(25, results.Count);
        Assert.Equal(Enumerable.Range(0, 25), results.Select(r => r.Index)); // all ties: input order
    }

    [Fact]
    public async Task Default_BatchSize_is_5_so_a_batch_fits_a_4096_token_window()
    {
        Assert.Equal(5, new LlmRerankerOptions().BatchSize);
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));

        await new LlmReranker(chat).RerankAsync("question", Candidates(20), 20, Ct);

        Assert.Equal(4, chat.Calls);
        Assert.All(chat.Requests, r => Assert.Equal(5, Passages(r)));
    }

    private static List<RetrievedChunk> LongCandidates(int n) =>
        Candidates(n).Select(c => c with { Text = string.Join(' ', Enumerable.Range(0, 400).Select(i => "word" + i)) }).ToList();

    [Fact]
    public async Task Warns_when_the_model_read_far_fewer_tokens_than_were_sent()
    {
        var logger = new FakeLogger<LlmReranker>();
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)), _ => 2050);

        var results = await new LlmReranker(chat, new LlmRerankerOptions { BatchSize = 10 }, logger: logger).RerankAsync("question", LongCandidates(10), 10, Ct);

        Assert.Equal(10, results.Count);
        var warning = Assert.Single(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("2050", warning.Message);
        Assert.Contains("context window", warning.Message);
        Assert.Contains("BatchSize", warning.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_warning_when_the_model_read_the_whole_prompt_or_reports_no_usage(bool reportUsage)
    {
        var logger = new FakeLogger<LlmReranker>();
        // A model tokenizer may count somewhat fewer tokens than cl100k_base; 90% is not a cut prompt.
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)), r => reportUsage ? (long)(Estimate(r) * 0.9) : null);

        await new LlmReranker(chat, logger: logger).RerankAsync("question", LongCandidates(10), 10, Ct);

        Assert.DoesNotContain(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
    }

    private static long Estimate(IList<ChatMessage> request) =>
        request.Sum(m => RagDotNet.Chunking.TokenCounter.CreateDefault().CountTokens(m.Text));

    [Fact]
    public async Task Builder_passes_its_logger_factory_to_the_LlmReranker()
    {
        using var provider = new FakeLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(provider));
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)), _ => 5);
        var rag = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .UseChatClient(chat)
            .UseLoggerFactory(loggerFactory)
            .AddLlmReranker()
            .Build();
        await rag.IngestAsync(Md("a", string.Join(' ', Enumerable.Range(0, 300).Select(i => "zorblax" + i))), cancellationToken: Ct);

        await rag.RetrieveAsync("zorblax1", cancellationToken: Ct);

        Assert.Contains(provider.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains("context window"));
    }

    [Fact]
    public async Task Each_call_limits_the_reply_to_MaxOutputTokens()
    {
        Assert.Equal(512, new LlmRerankerOptions().MaxOutputTokens);
        var chat = new ScriptChat((r, _) => Task.FromResult(Fives(r)));

        await new LlmReranker(chat).RerankAsync("q", Candidates(7), 7, Ct);
        await new LlmReranker(chat, new LlmRerankerOptions { MaxOutputTokens = 4096 }).RerankAsync("q", Candidates(1), 1, Ct);

        Assert.Equal([512, 512, 4096], chat.Options.Select(o => o?.MaxOutputTokens));
    }

    [Fact]
    public async Task A_reply_cut_off_at_MaxOutputTokens_falls_back_naming_the_option()
    {
        var chat = new ScriptChat((_, _) => Task.FromResult("<think>Let me look at passage one. It says"), finish: ChatFinishReason.Length);

        var ex = await Assert.ThrowsAsync<RerankFallbackException>(() => new LlmReranker(chat).RerankAsync("q", Candidates(3), 3, Ct));

        Assert.Equal(2, chat.Calls);
        Assert.Contains("MaxOutputTokens", ex.Message);
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
    [InlineData("<think>Passage [1] mentions it, [2] does not.</think>\n[7, 0]")]
    [InlineData("<think>\n[1] is good.\n</think>The scores: [7, 0]")]
    public async Task Reasoning_models_think_block_is_skipped(string reply)
    {
        var chat = new ScriptChat((_, _) => Task.FromResult(reply));

        var results = await new LlmReranker(chat).RerankAsync("q", Candidates(2), 2, Ct);

        Assert.Equal(1, chat.Calls);
        Assert.Equal([new RerankResult(0, 0.7), new RerankResult(1, 0.0)], results);
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
    [InlineData(0, 4, 512, 512)]
    [InlineData(10, 0, 512, 512)]
    [InlineData(10, 4, 0, 512)]
    [InlineData(10, 4, 512, 0)]
    public void Options_below_1_are_rejected(int batch, int concurrency, int tokens, int outputTokens)
    {
        var options = new LlmRerankerOptions { BatchSize = batch, MaxConcurrency = concurrency, MaxCandidateTokens = tokens, MaxOutputTokens = outputTokens };

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
    public async Task Di_rejects_MaxOutputTokens_below_1_at_start()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        builder.Services.AddSingleton<IChatClient>(new FakeChatClient());
        builder.Services.AddRag(o => o.EmbeddingDimensions = 64).AddLlmReranker(o => o.MaxOutputTokens = 0);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));
        Assert.Contains("MaxOutputTokens", ex.Message);
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
