using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests.Hybrid;

public class RerankingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Scores candidates by a script; records what it was given.</summary>
    private sealed class ScriptedReranker(Func<IReadOnlyList<RetrievedChunk>, IReadOnlyList<RerankResult>>? script = null) : IReranker
    {
        public string? Query { get; private set; }

        public IReadOnlyList<RetrievedChunk>? Candidates { get; private set; }

        public int Top { get; private set; }

        public int Calls { get; private set; }

        public Exception? Throw { get; set; }

        public async Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken)
        {
            Calls++;
            Query = query;
            Candidates = candidates;
            Top = top;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null) throw Throw;
            // Default: reverse the fused order; the last candidate scores 1.
            return script?.Invoke(candidates)
                ?? candidates.Select((c, i) => new RerankResult(i, (i + 1.0) / candidates.Count)).Reverse().ToList();
        }
    }

    private static async Task<IRagPipeline> SeededAsync(IReranker? reranker, Action<RagOptions>? configure = null, Action<RagPipelineBuilder>? builder = null)
    {
        var (rag, _, _, _, _) = Create(configure, b =>
        {
            if (reranker is not null) b.UseReranker(reranker);
            builder?.Invoke(b);
        });
        for (var i = 0; i < 6; i++)
            await rag.IngestAsync(Md($"d{i}", $"zorblax document {i} " + string.Join(' ', Enumerable.Repeat("filler", i))), cancellationToken: Ct);
        return rag;
    }

    [Fact]
    public async Task Registered_reranker_reorders_candidates_with_Rerank_scores()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker);
        var fused = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 6, UseReranker = false }, Ct);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 3 }, Ct);

        Assert.Equal(fused.Select(c => c.ChunkKey).Reverse().Take(3), results.Select(c => c.ChunkKey));
        Assert.All(results, c => Assert.Equal(ScoreKind.Rerank, c.ScoreKind));
        Assert.Equal([1.0, 5.0 / 6, 4.0 / 6], results.Select(c => c.Score));
        Assert.Equal(3, reranker.Top);
    }

    [Fact]
    public async Task Reranker_sees_RerankCandidates_and_the_original_question()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker, o => o.RerankCandidates = 4, b => b.UseChatClient(new FakeChatClient()));

        await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2, QueryTransformers = [new FixedQueries("rewritten one", "rewritten two")] }, Ct);

        Assert.Equal("zorblax", reranker.Query);
        Assert.Equal(4, reranker.Candidates!.Count);
        Assert.All(reranker.Candidates, c => Assert.False(string.IsNullOrEmpty(c.Text)));
    }

    [Fact]
    public async Task Candidate_pool_is_at_least_TopK()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker, o => o.RerankCandidates = 2);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 5 }, Ct);

        Assert.Equal(5, reranker.Candidates!.Count);
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task UseReranker_false_skips_it()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { UseReranker = false }, Ct);

        Assert.Equal(0, reranker.Calls);
        Assert.All(results, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
    }

    [Fact]
    public async Task UseReranker_true_without_one_throws()
    {
        var rag = await SeededAsync(null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("zorblax", new RetrievalOptions { UseReranker = true }, Ct));

        Assert.Equal("UseReranker is true but no reranker is registered. Register one with AddLlmReranker() or AddReranker<T>().", ex.Message);
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(99, 0.5)]
    [InlineData(0, double.NaN)]
    [InlineData(0, 1.5)]
    [InlineData(0, -0.1)]
    [InlineData(-2, 0.5)] // duplicate index
    public async Task Invalid_indexes_or_scores_throw_naming_the_reranker(int index, double score)
    {
        var reranker = new ScriptedReranker(_ => index == -2 ? [new RerankResult(0, 0.9), new RerankResult(0, 0.8)] : [new RerankResult(index, score)]);
        var rag = await SeededAsync(reranker);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("zorblax", cancellationToken: Ct));

        Assert.Contains(nameof(ScriptedReranker), ex.Message);
    }

    [Fact]
    public async Task MinScore_applies_to_rerank_scores()
    {
        var log = new CapturingLoggerFactory();
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker, builder: b => b.UseLoggerFactory(log));

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 6, MinScore = 0.6 }, Ct);

        Assert.Equal([1.0, 5.0 / 6, 4.0 / 6], results.Select(c => c.Score));
        Assert.DoesNotContain(log.Messages(LogLevel.Warning), m => m.StartsWith("MinScore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reranker_with_vector_only_retrieval_still_runs()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker, o => o.Hybrid = HybridMode.Off);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2, MinScore = 0.9 }, Ct);

        Assert.Equal(1, reranker.Calls);
        Assert.Equal(6, reranker.Candidates!.Count); // MinScore did not cut similarity scores before reranking
        Assert.All(reranker.Candidates, c => Assert.Equal(ScoreKind.Similarity, c.ScoreKind));
        Assert.All(results, c => Assert.Equal(ScoreKind.Rerank, c.ScoreKind));
    }

    [Fact]
    public async Task Fallback_exception_returns_fused_order_and_warns()
    {
        var log = new CapturingLoggerFactory();
        var reranker = new ScriptedReranker { Throw = new RerankFallbackException("the model's reply could not be read") };
        var rag = await SeededAsync(reranker, builder: b => b.UseLoggerFactory(log));
        var fused = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 3, UseReranker = false }, Ct);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 3 }, Ct);

        Assert.Equal(fused.Select(c => (c.ChunkKey, c.Score, c.ScoreKind)), results.Select(c => (c.ChunkKey, c.Score, c.ScoreKind)));
        Assert.Contains(log.Messages(LogLevel.Warning), m => m.Contains(nameof(ScriptedReranker)) && m.Contains("the model's reply could not be read"));
    }

    [Fact]
    public async Task Cancellation_and_other_errors_propagate()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker);
        using var cts = new CancellationTokenSource();
        reranker.Throw = new OperationCanceledException(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rag.RetrieveAsync("zorblax", cancellationToken: Ct));

        reranker.Throw = new TimeoutException("chat timed out");
        await Assert.ThrowsAsync<TimeoutException>(() => rag.RetrieveAsync("zorblax", cancellationToken: Ct));
    }

    [Fact]
    public async Task Rerank_span_has_candidates_count_and_fallback_tag()
    {
        var reranker = new ScriptedReranker();
        var rag = await SeededAsync(reranker, o => o.RerankCandidates = 4);
        using var capture = new TelemetryCapture();

        await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2 }, Ct);
        reranker.Throw = new RerankFallbackException("gave up");
        await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2 }, Ct);

        var spans = capture.Mine.Where(a => a.OperationName == "rag.rerank").ToList();
        Assert.Equal(2, spans.Count);
        Assert.Equal(4, spans[0].GetTagItem("rag.rerank.candidates"));
        Assert.Null(spans[0].GetTagItem("rag.rerank.fallback"));
        Assert.Equal(true, spans[1].GetTagItem("rag.rerank.fallback"));
        var retrieve = capture.Mine.First(a => a.OperationName == "rag.retrieve");
        Assert.Equal(retrieve.Id, spans[0].ParentId);
    }

    private sealed class FixedQueries(params string[] queries) : IQueryTransformer
    {
        public Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(queries);
    }
}
