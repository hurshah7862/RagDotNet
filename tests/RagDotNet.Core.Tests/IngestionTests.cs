using System.Text;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Chunking;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Filtering;
using RagDotNet.Ingestion;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class IngestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagOptions StoreOptions() => new() { CollectionName = "test", EmbeddingDimensions = 64, Metadata = new MetadataSchema().Add<string>("team").Add<long>("rev") };

    private static string Sections(int n, string prefix = "Body") =>
        string.Concat(Enumerable.Range(1, n).Select(i => $"# Section {i}\n\n{prefix} text number {i}\n\n"));

    private static async Task<List<string>> ExistingKeys(VectorStore store, string docId, int upTo)
    {
        var chunks = store.GetDynamicCollection("test", RecordDefinitions.Chunks(StoreOptions()));
        var keys = Enumerable.Range(0, upTo).Select(i => (object)ChunkKeys.Chunk("", docId, i)).ToList();
        var found = new List<string>();
        await foreach (var r in chunks.GetAsync(keys, new RecordRetrievalOptions { IncludeVectors = false }, Ct)) found.Add((string)r["Key"]!);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static async Task<Dictionary<string, object?>?> Manifest(VectorStore store, string docId)
    {
        var m = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(StoreOptions()));
        return await m.GetAsync(ChunkKeys.Document("", docId), new RecordRetrievalOptions { IncludeVectors = false }, Ct);
    }

    [Fact]
    public async Task First_ingest_is_Created_with_chunks_and_manifest()
    {
        var (rag, _, _, _, store) = Create();
        var r = await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, r.Status);
        Assert.Equal("doc", r.DocumentId);
        Assert.Equal(3, r.ChunkCount);
        Assert.Equal(["doc:0", "doc:1", "doc:2"], await ExistingKeys(store, "doc", 5));
        var m = await Manifest(store, "doc");
        Assert.NotNull(m);
        Assert.Equal(3, m["ChunkCount"]);
        Assert.Equal("doc.md", m["SourceName"]);
        Assert.Equal("fake-embed", m["EmbeddingModelId"]);
    }

    [Fact]
    public async Task Reingesting_identical_bytes_is_Unchanged_and_embeds_nothing()
    {
        var (rag, _, embedder, _, _) = Create();
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        var calls = embedder.CallCount;
        var r = await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Unchanged, r.Status);
        Assert.Equal(2, r.ChunkCount);
        Assert.Equal(calls, embedder.CallCount);
    }

    [Fact]
    public async Task Changed_content_is_Updated_and_stale_chunks_are_deleted()
    {
        var (rag, _, _, _, store) = Create();
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        var r = await rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Updated, r.Status);
        Assert.Equal(["doc:0"], await ExistingKeys(store, "doc", 5));
        Assert.Equal(1, (await Manifest(store, "doc"))!["ChunkCount"]);
    }

    [Fact]
    public async Task Changed_chunking_options_cause_Updated()
    {
        var (rag, _, embedder, _, _) = Create();
        await rag.IngestAsync(Md("doc", Sections(2)), cancellationToken: Ct);
        var calls = embedder.CallCount;
        var r = await rag.IngestAsync(Md("doc", Sections(2)), new IngestOptions { Chunking = new ChunkingOptions { MaxTokens = 128 } }, cancellationToken: Ct);
        Assert.Equal(IngestStatus.Updated, r.Status);
        Assert.True(embedder.CallCount > calls);
    }

    [Fact]
    public async Task Embeds_in_batches_of_EmbeddingBatchSize()
    {
        var (rag, _, embedder, _, _) = Create(o => o.EmbeddingBatchSize = 32);
        var r = await rag.IngestAsync(Md("doc", Sections(70)), cancellationToken: Ct);
        Assert.Equal(70, r.ChunkCount);
        Assert.Equal(3, embedder.CallCount);
        Assert.Equal(70, embedder.Inputs.Count);
    }

    [Fact]
    public async Task Embedding_input_includes_heading_path()
    {
        var (rag, _, embedder, _, _) = Create();
        await rag.IngestAsync(Md("doc", "# Install\n\n## Linux\n\napt install foo\n"), cancellationToken: Ct);
        Assert.Contains("Install > Linux\n\napt install foo", embedder.Inputs);
    }

    [Fact]
    public async Task Undeclared_or_mistyped_metadata_throws()
    {
        var (rag, _, _, _, _) = Create(o => o.Metadata.Add<int>("year"));
        var undeclared = await Assert.ThrowsAsync<ArgumentException>(() =>
            rag.IngestAsync(Md("a", "# A\n\ntext", new Dictionary<string, object?> { ["nope"] = "x" }), cancellationToken: Ct));
        Assert.Contains("nope", undeclared.Message);
        var mistyped = await Assert.ThrowsAsync<ArgumentException>(() =>
            rag.IngestAsync(Md("b", "# A\n\ntext", new Dictionary<string, object?> { ["year"] = "2024" }), cancellationToken: Ct));
        Assert.Contains("year", mistyped.Message);
    }

    [Fact]
    public async Task Declared_metadata_is_stored_on_every_chunk()
    {
        var (rag, _, _, _, store) = Create(o => { o.Metadata.Add<string>("team"); o.Metadata.Add<long>("rev"); o.Metadata.Add<bool>("draft"); });
        await rag.IngestAsync(Md("doc", Sections(2), new Dictionary<string, object?> { ["team"] = "core", ["rev"] = 7 }), cancellationToken: Ct);
        var opts = StoreOptions();
        opts.Metadata.Add<bool>("draft");
        var chunks = store.GetDynamicCollection("test", RecordDefinitions.Chunks(opts));
        var records = new List<Dictionary<string, object?>>();
        await foreach (var r in chunks.GetAsync(new object[] { "doc:0", "doc:1" }, new RecordRetrievalOptions { IncludeVectors = false }, Ct)) records.Add(r);
        Assert.Equal(2, records.Count);
        foreach (var r in records)
        {
            Assert.Equal("core", r["meta_team"]);
            Assert.Equal(7L, r["meta_rev"]);
            Assert.Null(r["meta_draft"]);
        }
    }

    [Fact]
    public async Task Empty_document_succeeds_with_zero_chunks_and_warning()
    {
        var (rag, _, embedder, _, store) = Create();
        var r = await rag.IngestAsync(Md("empty", "  \n\n \n"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, r.Status);
        Assert.Equal(0, r.ChunkCount);
        Assert.Equal("EmptyDocument", r.Warnings[0].Code);
        Assert.Equal(0, embedder.CallCount);
        Assert.Equal(0, (await Manifest(store, "empty"))!["ChunkCount"]);
    }

    [Fact]
    public async Task Delete_removes_chunks_and_manifest_and_returns_false_when_missing()
    {
        var (rag, _, _, _, store) = Create();
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);
        Assert.True(await rag.DeleteDocumentAsync("doc", Ct));
        Assert.Empty(await ExistingKeys(store, "doc", 5));
        Assert.Null(await Manifest(store, "doc"));
        Assert.False(await rag.DeleteDocumentAsync("doc", Ct));
    }

    [Fact]
    public async Task IngestMany_returns_results_in_input_order()
    {
        var (rag, _, _, _, _) = Create();
        var sources = Enumerable.Range(1, 8).Select(i => Md($"d{i}", Sections(i))).ToList();
        var results = await rag.IngestManyAsync(sources, cancellationToken: Ct);
        Assert.Equal(sources.Select(s => s.DocumentId), results.Select(r => r.DocumentId));
        Assert.Equal(Enumerable.Range(1, 8), results.Select(r => r.ChunkCount));
    }

    [Fact]
    public async Task IngestMany_rejects_duplicate_document_ids()
    {
        var (rag, _, embedder, _, store) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rag.IngestManyAsync([Md("x", "# A\n\none"), Md("y", "# A\n\ntwo"), Md("x", "# A\n\nthree")], cancellationToken: Ct));
        Assert.Equal(0, embedder.CallCount);
        Assert.False(await store.CollectionExistsAsync("test_documents", Ct));
    }

    private sealed class SyncProgress : IProgress<IngestProgress>
    {
        public List<IngestProgress> Reports { get; } = [];

        public void Report(IngestProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Reports_progress_through_each_stage()
    {
        var (rag, _, _, _, _) = Create();
        var progress = new SyncProgress();
        await rag.IngestAsync(Md("doc", Sections(2)), progress: progress, cancellationToken: Ct);
        Assert.Equal(
            [IngestStage.Reading, IngestStage.Chunking, IngestStage.Embedding, IngestStage.Storing, IngestStage.Completed],
            progress.Reports.Select(r => r.Stage).Distinct());
        Assert.All(progress.Reports, r => Assert.Equal("doc", r.DocumentId));
        var last = progress.Reports[^1];
        Assert.Equal((IngestStage.Completed, 2, 2), (last.Stage, last.ChunksEmbedded, last.TotalChunks));
    }

    [Fact]
    public async Task Crash_after_chunks_before_manifest_converges_on_retry()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, _, _, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);

        failing.FailManifestUpsertIn = 2; // (a) succeeds, chunks are written, (c) fails
        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct));
        // Chunk 0 was overwritten, the manifest still describes v1, and the stale chunks are still there.
        Assert.Equal(3, (await Manifest(failing, "doc"))!["ChunkCount"]);
        Assert.Equal(3, (await ExistingKeys(failing, "doc", 5)).Count);

        var retry = await rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Updated, retry.Status);

        var (fresh, _, _, _, freshStore) = Create();
        await fresh.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct);
        Assert.Equal(await ExistingKeys(freshStore, "doc", 5), await ExistingKeys(failing, "doc", 5));
        Assert.Equal(1, (await Manifest(failing, "doc"))!["ChunkCount"]);
    }

    [Fact]
    public async Task Crash_before_stale_delete_converges_and_clears_high_water()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, _, embedder, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);

        failing.FailNextChunkDelete = true;
        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct));
        Assert.Equal(3, (await ExistingKeys(failing, "doc", 5)).Count);

        var calls = embedder.CallCount;
        var retry = await rag.IngestAsync(Md("doc", Sections(1, "Changed")), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Unchanged, retry.Status);
        Assert.Equal(calls, embedder.CallCount);
        Assert.Equal(["doc:0"], await ExistingKeys(failing, "doc", 5));
        var m = (await Manifest(failing, "doc"))!;
        Assert.Equal(1, m["ChunkCount"]);
        Assert.Equal(1, m["ChunkHighWater"]);
    }

    [Fact]
    public async Task Delete_after_failed_growing_update_removes_all_chunks()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, _, _, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));
        await rag.IngestAsync(Md("doc", Sections(3)), cancellationToken: Ct);

        failing.FailManifestUpsertIn = 2;
        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("doc", Sections(10)), cancellationToken: Ct));
        Assert.Equal(10, (await ExistingKeys(failing, "doc", 12)).Count);

        Assert.True(await rag.DeleteDocumentAsync("doc", Ct));
        Assert.Empty(await ExistingKeys(failing, "doc", 12));
        Assert.Null(await Manifest(failing, "doc"));
    }

    [Fact]
    public async Task Delete_after_failed_first_ingest_removes_chunks_and_returns_true()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, _, _, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));

        failing.FailManifestUpsertIn = 2;
        await Assert.ThrowsAsync<IOException>(() => rag.IngestAsync(Md("doc", Sections(4)), cancellationToken: Ct));
        Assert.Equal(4, (await ExistingKeys(failing, "doc", 6)).Count);

        Assert.True(await rag.DeleteDocumentAsync("doc", Ct));
        Assert.Empty(await ExistingKeys(failing, "doc", 6));
    }

    [Fact]
    public async Task Changed_metadata_with_same_bytes_is_Updated_and_restamps_chunks()
    {
        var (rag, _, _, _, store) = Create(o => { o.Metadata.Add<string>("team"); o.Metadata.Add<long>("rev"); });
        await rag.IngestAsync(Md("doc", Sections(2), new Dictionary<string, object?> { ["team"] = "a" }), cancellationToken: Ct);
        var r = await rag.IngestAsync(Md("doc", Sections(2), new Dictionary<string, object?> { ["team"] = "b" }), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Updated, r.Status);
        var chunks = store.GetDynamicCollection("test", RecordDefinitions.Chunks(StoreOptions()));
        await foreach (var rec in chunks.GetAsync(new object[] { "doc:0", "doc:1" }, new RecordRetrievalOptions { IncludeVectors = false }, Ct))
            Assert.Equal("b", rec["meta_team"]);
    }

    [Fact]
    public async Task Same_metadata_in_different_key_order_is_Unchanged()
    {
        var (rag, _, _, _, _) = Create(o => { o.Metadata.Add<string>("team"); o.Metadata.Add<long>("rev"); });
        await rag.IngestAsync(Md("doc", Sections(2), new Dictionary<string, object?> { ["team"] = "a", ["rev"] = 1 }), cancellationToken: Ct);
        var r = await rag.IngestAsync(Md("doc", Sections(2), new Dictionary<string, object?> { ["rev"] = 1L, ["team"] = "a" }), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Unchanged, r.Status);
    }

    [Fact]
    public async Task Per_content_type_chunking_override_applies()
    {
        var text = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"word{i}"));
        var (plain, _, _, _, _) = Create();
        var baseline = await plain.IngestAsync(Md("doc", text), cancellationToken: Ct);
        var (rag, _, _, _, _) = Create(o => o.ChunkingByContentType["text/markdown"] = new ChunkingOptions { MaxTokens = 32 });
        var overridden = await rag.IngestAsync(Md("doc", text), cancellationToken: Ct);
        Assert.True(overridden.ChunkCount > baseline.ChunkCount);
    }

    [Fact]
    public async Task ManifestStore_lists_in_key_order_across_pages()
    {
        var (rag, _, _, _, store) = Create();
        var sources = Enumerable.Range(0, 120).Select(i => Md($"d{i:000}", "# A\n\ntext " + i)).ToList();
        await rag.IngestManyAsync(sources, cancellationToken: Ct);
        var manifests = new ManifestStore(store, StoreOptions());
        var listed = new List<string>();
        await foreach (var m in manifests.ListAsync(r => (string)r["DocumentId"]! != "none", Ct)) listed.Add(m.DocumentId);
        Assert.Equal(120, listed.Count);
        Assert.Equal(listed.OrderBy(x => x, StringComparer.Ordinal), listed);
        Assert.Equal("d005", (await manifests.FindFirstAsync(r => (string)r["DocumentId"]! == "d005", Ct))!.DocumentId);
    }
}
