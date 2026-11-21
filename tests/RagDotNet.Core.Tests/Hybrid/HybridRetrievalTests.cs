using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Keyword;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests.Hybrid;

public class HybridRetrievalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagPipelineBuilder Builder(VectorStore store, CapturingLoggerFactory? log = null, Action<RagOptions>? configure = null) =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(store, "test", 64)
            .UseLoggerFactory(log ?? new CapturingLoggerFactory())
            .Configure(o => configure?.Invoke(o));

    /// <summary>Three short documents that embed exactly like "ERR-404", and one longer document that contains it.</summary>
    private static async Task SeedExactTermAsync(IRagPipeline rag)
    {
        foreach (var (word, i) in FakeEmbeddingGenerator.Colliding("err-404", 64).Take(3).Select((w, i) => (w, i)))
            await rag.IngestAsync(Md($"filler{i}", word), cancellationToken: Ct);
        await rag.IngestAsync(Md("target", "ERR-404 means the page is missing from the server today"), cancellationToken: Ct);
    }

    private static ServiceProvider NoKeywordIndex(Action<RagOptions> configure, CapturingLoggerFactory? log = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        if (log is not null) services.AddSingleton<ILoggerFactory>(log);
        services.AddRag(o => { o.EmbeddingDimensions = 64; configure(o); });
        services.RemoveAll<IKeywordIndex>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Default_pipeline_is_hybrid_and_finds_an_exact_term_vector_search_misses()
    {
        var (rag, _, _, _, _) = Create(o => o.HybridCandidates = 2);
        await SeedExactTermAsync(rag);

        var hybrid = await rag.RetrieveAsync("ERR-404", new RetrievalOptions { TopK = 2 }, Ct);
        var vector = await rag.RetrieveAsync("ERR-404", new RetrievalOptions { TopK = 2, Hybrid = HybridMode.Off }, Ct);

        Assert.Contains(hybrid, c => c.DocumentId == "target");
        Assert.All(hybrid, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
        Assert.DoesNotContain(vector, c => c.DocumentId == "target");
        Assert.All(vector, c => Assert.Equal(ScoreKind.Similarity, c.ScoreKind));
    }

    [Fact]
    public async Task Off_in_options_and_per_call_gives_stage6_output()
    {
        static async Task<List<string>> Run(IRagPipeline rag, RetrievalOptions options)
        {
            await rag.IngestAsync(Md("a", "# Alpha\n\nalpha beta gamma delta"), cancellationToken: Ct);
            await rag.IngestAsync(Md("b", "# Beta\n\nbeta gamma epsilon"), cancellationToken: Ct);
            await rag.IngestAsync(Md("c", "# Gamma\n\nzeta eta theta beta"), cancellationToken: Ct);
            var lines = new List<string>();
            foreach (var q in new[] { "beta gamma", "theta", "unrelated words" })
                foreach (var c in await rag.RetrieveAsync(q, options with { TopK = 3 }, Ct))
                    lines.Add($"{q}|{c.ChunkKey}|{c.Score:R}|{c.ScoreKind}|{c.Text}");
            return lines;
        }

        using var provider = NoKeywordIndex(_ => { });
        var baseline = await Run(provider.GetRequiredService<IRagPipeline>(), new RetrievalOptions());
        var optionsOff = await Run(Create(o => o.Hybrid = HybridMode.Off).Pipeline, new RetrievalOptions());
        var callOff = await Run(Create().Pipeline, new RetrievalOptions { Hybrid = HybridMode.Off });
        var minScore = await Run(Create(o => o.Hybrid = HybridMode.Off).Pipeline, new RetrievalOptions { MinScore = 0.3 });

        Assert.NotEmpty(baseline);
        Assert.All(baseline, l => Assert.EndsWith("|Similarity|" + l.Split('|')[4], l));
        Assert.Equal(baseline, optionsOff);
        Assert.Equal(baseline, callOff);
        Assert.Equal(baseline.Where(l => double.Parse(l.Split('|')[2], System.Globalization.CultureInfo.InvariantCulture) >= 0.3), minScore);
    }

    [Fact]
    public async Task On_without_a_keyword_path_throws_HybridSearchUnavailable()
    {
        using var provider = NoKeywordIndex(o => o.Hybrid = HybridMode.On);
        var rag = provider.GetRequiredService<IRagPipeline>();

        await Assert.ThrowsAsync<HybridSearchUnavailableException>(() => rag.RetrieveAsync("anything", cancellationToken: Ct));

        var offInOptions = Create(o => o.Hybrid = HybridMode.Off).Pipeline;
        var ex = await Assert.ThrowsAsync<HybridSearchUnavailableException>(() => offInOptions.RetrieveAsync("anything", new RetrievalOptions { Hybrid = HybridMode.On }, Ct));
        Assert.Contains("RagOptions.Hybrid is Off", ex.Message);
    }

    [Fact]
    public async Task Auto_without_a_keyword_path_warns_once()
    {
        var log = new CapturingLoggerFactory();
        using var provider = NoKeywordIndex(_ => { }, log);
        var rag = provider.GetRequiredService<IRagPipeline>();
        await rag.IngestAsync(Md("a", "alpha beta"), cancellationToken: Ct);

        var first = await rag.RetrieveAsync("alpha", cancellationToken: Ct);
        await rag.RetrieveAsync("beta", cancellationToken: Ct);

        Assert.Equal(ScoreKind.Similarity, Assert.Single(first).ScoreKind);
        Assert.Single(log.Messages(LogLevel.Warning), m => m ==
            "Hybrid search is unavailable: the vector store has no native hybrid search and no keyword index is registered. Retrieval is vector-only. Register one with AddInMemoryKeywordIndex() or AddLuceneKeywordIndex().");
    }

    [Fact]
    public async Task Scope_applies_inside_keyword_search()
    {
        var scope = new MutableScope { Current = TestScopes.Tenant("b") };
        var (rag, _, _, _, _) = Create(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); }, b => b.UseAccessScope(scope));
        await rag.IngestAsync(Md("secret", "zorblax belongs to tenant b"), cancellationToken: Ct);
        scope.Current = null; // full access: a document without a tenant goes to the common area
        await rag.IngestAsync(Md("shared", "zorblax in the common area"), cancellationToken: Ct);
        scope.Current = TestScopes.Tenant("a");
        await rag.IngestAsync(Md("mine", "unrelated text for tenant a"), cancellationToken: Ct);

        var withCommon = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 10 }, Ct);
        var withoutCommon = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 10, IncludeCommon = false }, Ct);

        Assert.Equal(["mine", "shared"], withCommon.Select(c => c.DocumentId).Order(StringComparer.Ordinal));
        Assert.Equal(["mine"], withoutCommon.Select(c => c.DocumentId));
    }

    [Fact]
    public async Task Caller_filter_and_missing_values_apply_inside_keyword_search()
    {
        var (rag, _, _, _, _) = Create(o => o.Metadata.Add<string>("category"));
        await rag.IngestAsync(Md("x", "zorblax x", new Dictionary<string, object?> { ["category"] = "x" }), cancellationToken: Ct);
        await rag.IngestAsync(Md("y", "zorblax y", new Dictionary<string, object?> { ["category"] = "y" }), cancellationToken: Ct);
        await rag.IngestAsync(Md("none", "zorblax none"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 10, Filter = Filtering.RagFilter.Eq("category", "x") }, Ct);

        Assert.Equal(["x"], results.Select(c => c.DocumentId));
    }

    [Fact]
    public async Task Reindex_in_progress_drops_keyword_hits_not_yet_reembedded()
    {
        var (seed, _, _, _, store) = Create(o => o.EmbeddingModelId = "m1");
        await seed.IngestAsync(Md("old", "zorblax on the old model"), cancellationToken: Ct);
        var rag = Builder(store, configure: o => o.EmbeddingModelId = "m2").Build();
        await ((RagPipeline)rag).Engine.Manifests.UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), Ct);
        await rag.IngestAsync(Md("new", "something else entirely"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 10 }, Ct);

        Assert.Equal(["new"], results.Select(c => c.DocumentId));
    }

    [Fact]
    public async Task Drifted_key_is_dropped_logged_once_and_counted()
    {
        var log = new CapturingLoggerFactory();
        var index = new RecordingKeywordIndex();
        var rag = Builder(new InMemoryVectorStore(), log).UseKeywordIndex(index).Build();
        await rag.IngestAsync(Md("real", "zorblax is real"), cancellationToken: Ct);
        await index.UpsertAsync([new KeywordChunk("ghost:0", "ghost", "", "zorblax ghost", new Dictionary<string, object?>())], Ct);
        using var capture = new TelemetryCapture();

        var first = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 10 }, Ct);
        await rag.RetrieveAsync("zorblax ghost", new RetrievalOptions { TopK = 10 }, Ct);

        Assert.Equal(["real"], first.Select(c => c.DocumentId));
        Assert.Single(log.Messages(LogLevel.Warning), m => m ==
            "The keyword index returned chunk ghost:0, which the vector store does not have; the index is out of step. Run IRagAdmin.RebuildKeywordIndexAsync to fix it.");
        Assert.Equal(2, capture.Sum("rag.keyword.drift"));
    }

    [Fact]
    public async Task MinScore_is_ignored_for_fused_results_with_one_warning()
    {
        var log = new CapturingLoggerFactory();
        var rag = Builder(new InMemoryVectorStore(), log).Build();
        await rag.IngestAsync(Md("a", "alpha beta"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("alpha", new RetrievalOptions { MinScore = 0.99 }, Ct);
        await rag.RetrieveAsync("beta", new RetrievalOptions { MinScore = 0.99 }, Ct);

        Assert.Equal(ScoreKind.Fused, Assert.Single(results).ScoreKind);
        Assert.Single(log.Messages(LogLevel.Warning), m => m == "MinScore is ignored for hybrid results without a reranker, because fused scores are not similarities.");
    }

    [Fact]
    public async Task Native_path_makes_one_hybrid_call_and_no_index_call()
    {
        var store = new HybridVectorStore();
        var index = new RecordingKeywordIndex();
        var rag = Builder(store).UseKeywordIndex(index).Build();
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "beta gamma"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("The Zorblax!", new RetrievalOptions { TopK = 2, Filter = Filtering.RagFilter.Eq("DocumentId", "a") | Filtering.RagFilter.Eq("DocumentId", "b") }, Ct);

        Assert.Equal(1, store.HybridCalls);
        Assert.Equal(0, store.VectorCalls);
        Assert.Equal(0, index.SearchCalls);
        Assert.Equal(["zorblax"], store.LastKeywords);
        Assert.True(store.LastHadFilter);
        Assert.Contains("Text", store.LastAdditionalProperty);
        Assert.Equal("a", results[0].DocumentId);
        Assert.All(results, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
    }

    [Fact]
    public async Task Native_NotSupported_switches_to_index_once()
    {
        var log = new CapturingLoggerFactory();
        var store = new HybridVectorStore { ThrowNotSupported = true };
        var index = new RecordingKeywordIndex();
        var rag = Builder(store, log).UseKeywordIndex(index).Build();
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "beta gamma"), cancellationToken: Ct);

        var first = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 1 }, Ct);
        var second = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 1 }, Ct);

        Assert.Equal(1, store.HybridCalls);
        Assert.Equal(KeywordPath.Index, ((RagPipeline)rag).Engine.KeywordPath);
        Assert.Equal("a", Assert.Single(first).DocumentId);
        Assert.Equal("a", Assert.Single(second).DocumentId);
        Assert.Equal(2, index.SearchCalls);
        Assert.Single(log.Messages(LogLevel.Warning), m => m.Contains("NotSupportedException"));
    }

    [Fact]
    public async Task Multi_query_fuses_each_query_hybrid_then_across_queries()
    {
        var index = new RecordingKeywordIndex();
        var (rag, _, _, _, _) = Create(builder: b => b.UseKeywordIndex(index));
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "quuxly beta"), cancellationToken: Ct);
        await rag.IngestAsync(Md("c", "gamma delta"), cancellationToken: Ct);

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2, QueryTransformers = [new FixedQueries("zorblax", "quuxly")] }, Ct);

        Assert.Equal(2, index.SearchCalls);
        Assert.Equal(["a", "b"], results.Select(c => c.DocumentId).Order(StringComparer.Ordinal));
        Assert.All(results, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
    }

    [Fact]
    public async Task External_content_hybrid_fetches_text_once()
    {
        var content = new CountingContentStore();
        var rag = Builder(new InMemoryVectorStore()).UseContentStore(content).Build();
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "beta gamma"), cancellationToken: Ct);
        content.Gets = 0;

        var results = await rag.RetrieveAsync("zorblax", new RetrievalOptions { TopK = 2 }, Ct);

        Assert.Equal(1, content.Gets);
        Assert.Equal("a", results[0].DocumentId);
        Assert.Equal("zorblax alpha", results[0].Text);
    }

    [Fact]
    public async Task Stop_word_query_returns_the_vector_results()
    {
        var (rag, _, _, _, _) = Create();
        await rag.IngestAsync(Md("a", "the cat sat"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "of mice and men"), cancellationToken: Ct);

        var hybrid = await rag.RetrieveAsync("the of and", new RetrievalOptions { TopK = 2 }, Ct);
        var vector = await rag.RetrieveAsync("the of and", new RetrievalOptions { TopK = 2, Hybrid = HybridMode.Off }, Ct);
        var punctuation = await rag.RetrieveAsync("?", new RetrievalOptions { TopK = 2 }, Ct);

        Assert.Equal(vector.Select(c => c.ChunkKey), hybrid.Select(c => c.ChunkKey));
        Assert.All(hybrid, c => Assert.Equal(ScoreKind.Fused, c.ScoreKind));
        Assert.Equal(2, punctuation.Count);
    }

    [Fact]
    public async Task Telemetry_tags_hybrid_path_and_keyword_count()
    {
        var (rag, _, _, _, _) = Create();
        await rag.IngestAsync(Md("a", "zorblax alpha"), cancellationToken: Ct);
        await rag.IngestAsync(Md("b", "zorblax beta"), cancellationToken: Ct);
        using var capture = new TelemetryCapture();

        await rag.RetrieveAsync("zorblax", cancellationToken: Ct);
        await rag.RetrieveAsync("zorblax", new RetrievalOptions { Hybrid = HybridMode.Off }, Ct);

        var spans = capture.Mine.Where(a => a.OperationName == "rag.retrieve").ToList();
        Assert.Equal("index", spans[0].GetTagItem("rag.hybrid.path"));
        Assert.Equal(2, spans[0].GetTagItem("rag.keyword.count"));
        Assert.Equal("none", spans[1].GetTagItem("rag.hybrid.path"));
        Assert.Null(spans[1].GetTagItem("rag.keyword.count"));
    }

    private sealed class FixedQueries(params string[] queries) : IQueryTransformer
    {
        public Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(queries);
    }

    private sealed class CountingContentStore : IChunkContentStore
    {
        private readonly InMemoryChunkContentStore _inner = new();

        public int Gets;

        public Task UpsertAsync(IReadOnlyList<ChunkContent> chunks, CancellationToken ct) => _inner.UpsertAsync(chunks, ct);

        public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
        {
            Interlocked.Increment(ref Gets);
            return _inner.GetAsync(chunkKeys, ct);
        }

        public Task DeleteAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct) => _inner.DeleteAsync(chunkKeys, ct);
    }
}
