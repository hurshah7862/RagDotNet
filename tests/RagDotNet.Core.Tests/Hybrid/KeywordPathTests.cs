using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Keyword;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests.Hybrid;

public class KeywordPathTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sections(int n, string prefix = "Body") =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"# Section {i}\n\n{prefix} text number {i}\n\n"));

    private static RagPipelineBuilder Builder(VectorStore store, CapturingLoggerFactory log, Action<RagOptions>? configure = null) =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(store, "test", 64)
            .UseLoggerFactory(log)
            .Configure(o => configure?.Invoke(o));

    private static async Task<RagEngine> Ready(IRagPipeline rag)
    {
        var engine = ((RagPipeline)rag).Engine;
        await engine.EnsureReadyAsync(Ct);
        return engine;
    }

    [Fact]
    public async Task Native_store_uses_native_and_logs_that_the_index_is_unused()
    {
        var log = new CapturingLoggerFactory();
        var index = new RecordingKeywordIndex();
        var rag = Builder(new HybridVectorStore(), log).UseKeywordIndex(index).Build();
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);

        var engine = await Ready(rag);

        Assert.Equal(KeywordPath.Native, engine.KeywordPath);
        Assert.Contains(log.Messages(LogLevel.Information), m => m.StartsWith("Keyword search: native", StringComparison.Ordinal) && m.Contains("recording keyword index is not used"));
        Assert.Empty(index.Calls);
    }

    [Fact]
    public async Task External_content_never_uses_native()
    {
        var log = new CapturingLoggerFactory();
        var rag = Builder(new HybridVectorStore(), log).UseContentStore(new InMemoryChunkContentStore()).Build();

        Assert.Equal(KeywordPath.Index, (await Ready(rag)).KeywordPath);
    }

    [Fact]
    public async Task Store_without_native_uses_the_index()
    {
        var log = new CapturingLoggerFactory();
        var rag = Builder(new InMemoryVectorStore(), log).Build();

        Assert.Equal(KeywordPath.Index, (await Ready(rag)).KeywordPath);
        Assert.Contains(log.Messages(LogLevel.Information), m => m.StartsWith("Keyword search: index (the in-memory keyword index)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Off_is_None_and_builds_nothing()
    {
        var store = new InMemoryVectorStore();
        await Builder(store, new()).Build().IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        var index = new RecordingKeywordIndex();

        var engine = await Ready(Builder(store, new(), o => o.Hybrid = HybridMode.Off).UseKeywordIndex(index).Build());

        Assert.Equal(KeywordPath.None, engine.KeywordPath);
        Assert.Equal(0, index.RebuildCalls);
        Assert.Empty(index.Calls);
    }

    [Fact]
    public async Task Startup_builds_the_in_memory_index_from_the_store()
    {
        var store = new InMemoryVectorStore();
        await Builder(store, new(), o => o.Metadata.Add<string>("team")).Build()
            .IngestAsync(Md("doc", Sections(3), new Dictionary<string, object?> { ["team"] = "core" }), cancellationToken: Ct);
        var log = new CapturingLoggerFactory();
        var index = new InMemoryKeywordIndex();

        await Ready(Builder(store, log, o => o.Metadata.Add<string>("team")).UseKeywordIndex(index).Build());

        var hits = await index.SearchAsync("number", new KeywordFilter(null, false, Filtering.RagFilter.Eq("team", "core")), 10, Ct);
        Assert.Equal(["doc:0", "doc:1", "doc:2"], hits.Select(h => h.ChunkKey).Order(StringComparer.Ordinal));
        Assert.Contains(log.Messages(LogLevel.Information), m => m.StartsWith("Built the in-memory keyword index: 3 chunks", StringComparison.Ordinal));
        Assert.Empty(log.Messages(LogLevel.Warning));
    }

    [Fact]
    public async Task Startup_rebuilds_when_counts_differ()
    {
        var store = new InMemoryVectorStore();
        await Builder(store, new()).Build().IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        var log = new CapturingLoggerFactory();
        var index = new RecordingKeywordIndex { CountOverride = 99 };

        await Ready(Builder(store, log).UseKeywordIndex(index).Build());

        Assert.Equal(1, index.RebuildCalls);
        Assert.Equal(["doc:0", "doc:1", "doc:2"], index.Chunks.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(log.Messages(LogLevel.Warning), m => m.Contains("holds 99 chunks") && m.Contains("have 3"));
    }

    [Fact]
    public async Task No_rebuild_when_counts_match()
    {
        var store = new InMemoryVectorStore();
        var index = new RecordingKeywordIndex();
        var first = Builder(store, new()).UseKeywordIndex(index).Build();
        await first.IngestAsync(Md("empty", ""), cancellationToken: Ct);
        await first.IngestAsync(Md("doc", Sections(5)), cancellationToken: Ct);
        await first.IngestAsync(Md("doc", Sections(5)), cancellationToken: Ct);
        await first.IngestAsync(Md("doc", Sections(2, "Changed")), cancellationToken: Ct);
        await first.IngestAsync(Md("other", Sections(1)), cancellationToken: Ct);

        // A second engine over the same store and the same (persistent) index.
        await Ready(Builder(store, new()).UseKeywordIndex(index).Build());

        Assert.Equal(0, index.RebuildCalls);
        Assert.Equal(3, await index.CountAsync(Ct));
    }

    [Fact]
    public async Task Admin_RebuildKeywordIndexAsync_rebuilds()
    {
        var index = new RecordingKeywordIndex();
        var builder = Builder(new InMemoryVectorStore(), new()).UseKeywordIndex(index);
        await builder.Build().IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        index.Chunks.Clear();

        await builder.BuildAdmin().RebuildKeywordIndexAsync(Ct);

        Assert.Equal(1, index.RebuildCalls);
        Assert.Equal(["doc:0", "doc:1"], index.Chunks.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Headings_are_keyword_searchable_after_ingest_and_after_a_rebuild()
    {
        var store = new InMemoryVectorStore();
        var index = new InMemoryKeywordIndex();
        var builder = Builder(store, new()).UseKeywordIndex(index);
        await builder.Build().IngestAsync(Md("doc", "# Zorblax settings\n\nBody text number 1\n"), cancellationToken: Ct);

        Assert.Equal(["doc:0"], (await index.SearchAsync("zorblax", KeywordFilter.All, 10, Ct)).Select(h => h.ChunkKey));

        await builder.BuildAdmin().RebuildKeywordIndexAsync(Ct);

        Assert.Equal(["doc:0"], (await index.SearchAsync("zorblax", KeywordFilter.All, 10, Ct)).Select(h => h.ChunkKey));
    }

    [Fact]
    public async Task Ingest_during_a_rebuild_is_not_lost()
    {
        var index = new PausingKeywordIndex();
        var builder = Builder(new InMemoryVectorStore(), new()).UseKeywordIndex(index);
        var rag = builder.Build();
        await rag.IngestAsync(Md("first", Sections(1)), cancellationToken: Ct);

        // The rebuild reads the documents, then waits before replacing the index.
        var rebuild = builder.BuildAdmin().RebuildKeywordIndexAsync(Ct);
        await index.Enumerated.Task.WaitAsync(Ct);
        var ingest = rag.IngestAsync(Md("late", "# Late\n\nQuixotic words\n"), cancellationToken: Ct);
        await Task.WhenAny(ingest, Task.Delay(500, Ct));
        index.Release.TrySetResult();
        await Task.WhenAll(rebuild, ingest);

        Assert.Equal(["late:0"], (await index.SearchAsync("quixotic", KeywordFilter.All, 10, Ct)).Select(h => h.ChunkKey));
    }

    /// <summary>An in-memory index whose rebuild pauses after reading its source, until released.</summary>
    private sealed class PausingKeywordIndex : IKeywordIndex
    {
        private readonly InMemoryKeywordIndex _inner = new();

        public TaskCompletionSource Enumerated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "pausing";

        public Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken cancellationToken) => _inner.UpsertAsync(chunks, cancellationToken);

        public Task DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken cancellationToken) => _inner.DeleteAsync(chunkKeys, cancellationToken);

        public Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken cancellationToken) =>
            _inner.SearchAsync(query, filter, top, cancellationToken);

        public Task<long> CountAsync(CancellationToken cancellationToken) => _inner.CountAsync(cancellationToken);

        public async Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken cancellationToken)
        {
            var chunks = new List<KeywordChunk>();
            await foreach (var c in source.WithCancellation(cancellationToken)) chunks.Add(c);
            if (chunks.Count > 0)
            {
                // Only the admin rebuild pauses; the startup build of an empty store does not.
                Enumerated.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            await _inner.RebuildAsync(Replay(chunks), cancellationToken);
        }

        private static async IAsyncEnumerable<KeywordChunk> Replay(List<KeywordChunk> chunks)
        {
            await Task.Yield();
            foreach (var c in chunks) yield return c;
        }
    }

    [Fact]
    public async Task Admin_rebuild_with_Hybrid_Off_throws()
    {
        var builder = Builder(new InMemoryVectorStore(), new(), o => o.Hybrid = HybridMode.Off);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAdmin().RebuildKeywordIndexAsync(Ct));
        Assert.Contains("not in use", ex.Message);
    }

    [Fact]
    public async Task Rebuild_in_external_content_mode_reads_text_from_the_content_store()
    {
        var store = new InMemoryVectorStore();
        var content = new InMemoryChunkContentStore();
        var scope = new MutableScope { Current = TestScopes.Tenant("a") };
        void Partitioned(RagOptions o)
        {
            o.Metadata.Add<string>("tenant");
            o.PartitionKeys.Add("tenant");
        }

        await Builder(store, new(), Partitioned).UseContentStore(content).UseAccessScope(scope).Build()
            .IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        var index = new RecordingKeywordIndex();

        await Ready(Builder(store, new(), Partitioned).UseContentStore(content).UseKeywordIndex(index).Build());

        Assert.Equal(["tenant=a|doc:0", "tenant=a|doc:1"], index.Chunks.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("Body text number 1", index.Chunks["tenant=a|doc:0"].Text);
        Assert.Equal("tenant=a", index.Chunks["tenant=a|doc:0"].Partition);
        Assert.Equal("a", index.Chunks["tenant=a|doc:0"].Metadata["tenant"]);
    }
}
