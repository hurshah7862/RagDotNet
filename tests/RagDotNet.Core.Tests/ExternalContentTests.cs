using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Retrieval;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class ExternalContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingContentStore : IChunkContentStore
    {
        private readonly InMemoryChunkContentStore _inner = new();
        public Func<IReadOnlyList<string>, Task>? OnUpsert { get; set; }
        public List<IReadOnlyList<string>> GetCalls { get; } = [];
        public List<string> Deleted { get; } = [];
        public int Count => _inner.Count;

        public List<ChunkContent> Upserted { get; } = [];

        public async Task UpsertAsync(IReadOnlyList<ChunkContent> chunks, CancellationToken ct)
        {
            Upserted.AddRange(chunks);
            if (OnUpsert is not null) await OnUpsert(chunks.Select(c => c.ChunkKey).ToList());
            await _inner.UpsertAsync(chunks, ct);
        }

        public Task<IReadOnlyDictionary<string, string>> GetAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
        {
            GetCalls.Add(chunkKeys.ToList());
            return _inner.GetAsync(chunkKeys, ct);
        }

        public Task DeleteAsync(IReadOnlyList<string> chunkKeys, CancellationToken ct)
        {
            Deleted.AddRange(chunkKeys);
            return _inner.DeleteAsync(chunkKeys, ct);
        }
    }

    private static RagOptions StoreOptions(RagKeyType keyType = RagKeyType.String) =>
        new() { CollectionName = "test", EmbeddingDimensions = 64, ContentStorage = ContentStorageMode.External, KeyType = keyType };

    private static RagPipelineBuilder NewBuilder(VectorStore store, IChunkContentStore content, FakeEmbeddingGenerator? embedder = null, string model = "fake-embed") =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder ?? new FakeEmbeddingGenerator(64))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64)
            .UseContentStore(content)
            .Configure(o => o.EmbeddingModelId = model);

    private static async Task<List<Dictionary<string, object?>>> RawChunks(VectorStore store, string docId, int n, RagKeyType keyType = RagKeyType.String)
    {
        var chunks = store.GetDynamicCollection("test", RecordDefinitions.Chunks(StoreOptions(keyType)));
        var keys = Enumerable.Range(0, n).Select(i => keyType == RagKeyType.Guid
            ? (object)ChunkKeys.ToGuid(ChunkKeys.Chunk("", docId, i))
            : ChunkKeys.Chunk("", docId, i)).ToList();
        var found = new List<Dictionary<string, object?>>();
        await foreach (var r in chunks.GetAsync(keys, new RecordRetrievalOptions { IncludeVectors = false }, Ct)) found.Add(r);
        return found;
    }

    private static string Sections(int n) =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"# Section {i}\n\nBody text number {i}\n\n"));

    [Fact]
    public async Task External_mode_stores_no_text_in_vector_records()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var rag = NewBuilder(store, content).Build();
        await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);

        var raw = await RawChunks(store, "d", 2);
        Assert.Equal(2, raw.Count);
        Assert.All(raw, r => Assert.False(r.ContainsKey("Text")));
        Assert.Equal(2, content.Count);
    }

    [Fact]
    public async Task Text_is_written_to_content_store_before_vectors()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var vectorCountAtUpsert = -1;
        content.OnUpsert = async _ => vectorCountAtUpsert = (await RawChunks(store, "d", 2)).Count;
        var rag = NewBuilder(store, content).Build();
        await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);

        Assert.Equal(0, vectorCountAtUpsert);
        Assert.Equal(2, (await RawChunks(store, "d", 2)).Count);
    }

    [Fact]
    public async Task Retrieval_fetches_text_in_one_batch_call()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var rag = NewBuilder(store, content).Build();
        await rag.IngestAsync(Md("d", Sections(4)), cancellationToken: Ct);
        content.GetCalls.Clear();

        var hits = await rag.RetrieveAsync("Body text number 2", new RetrievalOptions { TopK = 3 }, Ct);

        Assert.Equal(3, hits.Count);
        var call = Assert.Single(content.GetCalls);
        Assert.Equal(3, call.Count);
        Assert.All(hits, h => { Assert.Contains(h.ChunkKey, call); Assert.False(string.IsNullOrEmpty(h.Text)); });
    }

    [Fact]
    public async Task Missing_text_drops_chunk_and_logs_warning()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var logger = new FakeLogger();
        var rag = NewBuilder(store, content).UseLoggerFactory(new FakeLoggerFactory(logger)).Build();
        await rag.IngestAsync(Md("d", Sections(3)), cancellationToken: Ct);
        var lost = ChunkKeys.Chunk("", "d", 1);
        await content.DeleteAsync([lost], Ct);

        var hits = await rag.RetrieveAsync("Body text number 2", new RetrievalOptions { TopK = 5 }, Ct);

        Assert.Equal(2, hits.Count);
        Assert.DoesNotContain(hits, h => h.ChunkKey == lost);
        Assert.All(hits, h => Assert.False(string.IsNullOrEmpty(h.Text)));
        Assert.Contains(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains(lost));
    }

    [Fact]
    public async Task Stale_chunks_are_deleted_from_both_stores()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var rag = NewBuilder(store, content).Build();
        await rag.IngestAsync(Md("d", Sections(3)), cancellationToken: Ct);
        Assert.Equal(3, content.Count);

        await rag.IngestAsync(Md("d", Sections(1)), cancellationToken: Ct);
        Assert.Equal(1, content.Count);
        Assert.Single(await RawChunks(store, "d", 3));
        Assert.Contains(ChunkKeys.Chunk("", "d", 2), content.Deleted);

        await rag.DeleteDocumentAsync("d", Ct);
        Assert.Equal(0, content.Count);
        Assert.Empty(await RawChunks(store, "d", 3));
    }

    [Fact]
    public async Task Reindex_reads_text_from_content_store()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        await NewBuilder(store, content).Build().IngestAsync(Md("d", "# H\n\nalpha beta gamma"), cancellationToken: Ct);

        var embedder = new FakeEmbeddingGenerator(64);
        var builder = NewBuilder(store, content, embedder, "other");
        content.GetCalls.Clear();
        var result = await builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);

        Assert.Equal(1, result.DocumentsReindexed);
        Assert.NotEmpty(content.GetCalls);
        Assert.Contains(embedder.Inputs, i => i.Contains("alpha beta gamma"));
        var hits = await builder.Build().RetrieveAsync("alpha beta gamma", cancellationToken: Ct);
        Assert.Contains("alpha beta gamma", hits[0].Text);
    }

    [Fact]
    public async Task Reindex_with_missing_text_throws_naming_document_and_chunk()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        await NewBuilder(store, content).Build().IngestAsync(Md("doc-x", "alpha beta"), cancellationToken: Ct);
        await content.DeleteAsync([ChunkKeys.Chunk("", "doc-x", 0)], Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewBuilder(store, content, model: "other").BuildAdmin().ReindexAsync(cancellationToken: Ct));
        Assert.Contains("doc-x", ex.Message);
        Assert.Contains("Chunk 0", ex.Message);
    }

    [Fact]
    public async Task Mode_mismatch_with_existing_collection_throws()
    {
        var store = new InMemoryVectorStore();
        var plain = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64)).UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64).Configure(o => o.EmbeddingModelId = "fake-embed").Build();
        await plain.IngestAsync(Md("d", "alpha"), cancellationToken: Ct);

        var builder = NewBuilder(store, new InMemoryChunkContentStore());
        var rag = builder.Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Contains("VectorStore", ex.Message);
        Assert.Contains("External", ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("e", "beta"), cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.DeleteDocumentAsync("d", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAdmin().ReindexAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Chunk_keys_match_between_stores_for_guid_key_type()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var rag = NewBuilder(store, content).Configure(o => o.KeyType = RagKeyType.Guid).Build();
        await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);

        var hits = await rag.RetrieveAsync("Body text number 1", new RetrievalOptions { TopK = 2 }, Ct);
        var fetched = content.GetCalls[^1];
        Assert.Equal(2, hits.Count);
        foreach (var h in hits)
        {
            Assert.Contains(h.ChunkKey, fetched);
            Assert.Equal(ChunkKeys.Chunk("", "d", h.ChunkIndex), h.ChunkKey);
        }

        var raw = await RawChunks(store, "d", 2, RagKeyType.Guid);
        Assert.Equal(2, raw.Count);
        Assert.All(raw, r => Assert.Contains(ChunkKeys.Chunk("", "d", Convert.ToInt32(r["ChunkIndex"])), fetched));
    }

    [Fact]
    public void External_without_store_still_throws_at_build()
    {
        var b = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64)).UseChatClient(new FakeChatClient())
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Configure(o => o.ContentStorage = ContentStorageMode.External);
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => b.Build());
    }

    [Fact]
    public async Task Reindex_with_blank_stored_text_throws_naming_document_and_chunk()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        await NewBuilder(store, content).Build().IngestAsync(Md("doc-y", "alpha beta"), cancellationToken: Ct);
        await content.UpsertAsync([new ChunkContent(ChunkKeys.Chunk("", "doc-y", 0), "doc-y", new Dictionary<string, string>(), 0, "  ", null, null, [])], Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewBuilder(store, content, model: "other").BuildAdmin().ReindexAsync(cancellationToken: Ct));
        Assert.Contains("doc-y", ex.Message);
        Assert.Contains("Chunk 0", ex.Message);
    }

    [Fact]
    public async Task Retrieval_drops_chunk_when_content_store_returns_empty_text()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var logger = new FakeLogger();
        var rag = NewBuilder(store, content).UseLoggerFactory(new FakeLoggerFactory(logger)).Build();
        await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);
        var blank = ChunkKeys.Chunk("", "d", 0);
        await content.UpsertAsync([new ChunkContent(blank, "d", new Dictionary<string, string>(), 0, "", null, null, [])], Ct);

        var hits = await rag.RetrieveAsync("Body text number 1", new RetrievalOptions { TopK = 5 }, Ct);

        Assert.DoesNotContain(hits, h => h.ChunkKey == blank);
        Assert.Single(hits);
        Assert.Contains(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains(blank));
    }

    [Fact]
    public async Task Retrieval_drops_chunk_when_content_store_returns_whitespace_text()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        var logger = new FakeLogger();
        var rag = NewBuilder(store, content).UseLoggerFactory(new FakeLoggerFactory(logger)).Build();
        await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);
        var blank = ChunkKeys.Chunk("", "d", 0);
        await content.UpsertAsync([new ChunkContent(blank, "d", new Dictionary<string, string>(), 0, "   ", null, null, [])], Ct);
        var missingBefore = ((RagDotNet.Pipeline.RagPipeline)rag).Engine.MissingContentCount;

        var hits = await rag.RetrieveAsync("Body text number 1", new RetrievalOptions { TopK = 5 }, Ct);

        Assert.DoesNotContain(hits, h => h.ChunkKey == blank);
        Assert.Single(hits);
        Assert.Equal(missingBefore + 1, ((RagDotNet.Pipeline.RagPipeline)rag).Engine.MissingContentCount);
        Assert.Contains(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning && r.Message.Contains(blank));
    }

    private sealed class FixedScope(string tenant) : RagDotNet.Access.IAccessScope
    {
        public ValueTask<RagDotNet.Access.AccessScope?> GetScopeAsync(CancellationToken cancellationToken) =>
            new(new RagDotNet.Access.AccessScope(new Dictionary<string, string> { ["tenant"] = tenant }));
    }

    [Fact]
    public async Task Scoped_external_mode_requests_only_callers_partition_keys_and_links_keys()
    {
        var store = new InMemoryVectorStore();
        var content = new RecordingContentStore();
        RagPipelineBuilder Scoped(string t) => NewBuilder(store, content)
            .Configure(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); })
            .UseAccessScope(new FixedScope(t));
        var a = Scoped("a").Build();
        var b = Scoped("b").Build();
        await a.IngestAsync(Md("d", "alpha beta gamma"), cancellationToken: Ct);
        await b.IngestAsync(Md("d", "alpha beta gamma"), cancellationToken: Ct);

        var stored = content.Upserted.Where(c => c.Partition["tenant"] == "a").ToList();
        Assert.Single(stored);
        Assert.Equal(new Dictionary<string, string> { ["tenant"] = "a" }, stored[0].Partition);

        content.GetCalls.Clear();
        var hits = await a.RetrieveAsync("alpha beta gamma", new RetrievalOptions { TopK = 5 }, Ct);

        var hit = Assert.Single(hits);
        Assert.Equal(stored[0].ChunkKey, hit.ChunkKey);
        var requested = Assert.Single(content.GetCalls);
        Assert.Equal([stored[0].ChunkKey], requested);
        Assert.DoesNotContain(content.Upserted.Where(c => c.Partition["tenant"] == "b"), c => requested.Contains(c.ChunkKey));
    }

    [Fact]
    public async Task Failed_vector_upsert_after_content_upsert_converges_on_retry()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var content = new RecordingContentStore();
        var rag = NewBuilder(failing, content).Build();
        failing.FailNextChunkUpsert = true;

        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct));
        Assert.Equal(2, content.Count);
        Assert.Empty(await RawChunks(failing, "d", 2));

        var r = await rag.IngestAsync(Md("d", Sections(2)), cancellationToken: Ct);
        Assert.Equal(2, r.ChunkCount);
        Assert.Equal(2, content.Count);
        Assert.Equal(2, (await RawChunks(failing, "d", 2)).Count);
        Assert.Equal(2, (await rag.RetrieveAsync("Body text number 1", new RetrievalOptions { TopK = 5 }, Ct)).Count);
    }

    [Fact]
    public async Task Delete_after_failed_vector_upsert_leaves_no_keys_in_either_store()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var content = new RecordingContentStore();
        var rag = NewBuilder(failing, content).Build();
        failing.FailNextChunkUpsert = true;
        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("d", Sections(3)), cancellationToken: Ct));
        Assert.Equal(3, content.Count);

        Assert.True(await rag.DeleteDocumentAsync("d", Ct));

        Assert.Equal(0, content.Count);
        Assert.Empty(await RawChunks(failing, "d", 3));
    }

    private sealed class FakeLoggerFactory(FakeLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}
