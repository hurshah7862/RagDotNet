using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class ConcurrencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagOptions StoreOptions() => new() { CollectionName = "test", EmbeddingDimensions = 64 };

    private static string Sections(int n, string prefix) =>
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

    private static async Task<int?> ManifestChunkCount(VectorStore store, string docId)
    {
        var m = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(StoreOptions()));
        var r = await m.GetAsync(ChunkKeys.Document("", docId), new RecordRetrievalOptions { IncludeVectors = false }, Ct);
        return r is null ? null : (int)r["ChunkCount"]!;
    }

    /// <summary>Either the document is fully present (chunks 0..ChunkCount-1, nothing beyond) or fully gone.</summary>
    private static async Task AssertConsistentAsync(VectorStore store, string docId)
    {
        var count = await ManifestChunkCount(store, docId);
        var expected = Enumerable.Range(0, count ?? 0).Select(i => ChunkKeys.Chunk("", docId, i)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, await ExistingKeys(store, docId, 20));
    }

    [Fact]
    public async Task Concurrent_ingests_of_same_document_leave_no_chunks_beyond_manifest()
    {
        var store = new InMemoryVectorStore();
        var gated = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), batch => batch.Any(t => t.Contains("Small", StringComparison.Ordinal)));
        var (rag, _, _, _, _) = Create(builder: b => b.UseEmbeddingGenerator(gated).UseVectorStore(store, "test", 64));

        // The small ingest reads the (absent) manifest, then pauses while embedding.
        var small = rag.IngestAsync(Md("doc", Sections(1, "Small")), cancellationToken: Ct);
        await gated.Reached.WaitAsync(Ct);

        // The large ingest of the same id runs while the small one is paused.
        var large = rag.IngestAsync(Md("doc", Sections(4, "Large")), cancellationToken: Ct);
        await Task.WhenAny(large, Task.Delay(1000, Ct));
        gated.Gate.SetResult();
        await Task.WhenAll(small, large);

        await AssertConsistentAsync(store, "doc");
        Assert.Equal(4, await ManifestChunkCount(store, "doc")); // the large ingest ran last
    }

    [Fact]
    public async Task Concurrent_ingest_and_delete_of_same_document_stay_consistent()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, _, _, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));
        await rag.IngestAsync(Md("doc", Sections(2, "Old")), cancellationToken: Ct);

        // The delete reads the manifest (2 chunks), then pauses before deleting chunks.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failing.BeforeNextChunkDelete = async () => { reached.TrySetResult(); await gate.Task; };
        var delete = rag.DeleteDocumentAsync("doc", Ct);
        await reached.Task.WaitAsync(Ct);

        // A larger re-ingest runs while the delete is paused.
        var ingest = rag.IngestAsync(Md("doc", Sections(5, "New")), cancellationToken: Ct);
        await Task.WhenAny(ingest, Task.Delay(1000, Ct));
        gate.SetResult();
        await Task.WhenAll(delete, ingest);

        Assert.True(await delete);
        await AssertConsistentAsync(failing, "doc");
        Assert.Equal(5, await ManifestChunkCount(failing, "doc")); // the ingest ran after the delete
    }

    [Fact]
    public async Task Concurrent_admin_delete_and_ingest_stay_consistent()
    {
        var failing = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, builder, _, _, _) = Create(builder: b => b.UseVectorStore(failing, "test", 64));
        var admin = builder.BuildAdmin();
        await rag.IngestAsync(Md("doc", Sections(2, "Old")), cancellationToken: Ct);

        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failing.BeforeNextChunkDelete = async () => { reached.TrySetResult(); await gate.Task; };
        var delete = admin.DeleteDocumentAsync("doc", null, Ct);
        await reached.Task.WaitAsync(Ct);

        var ingest = rag.IngestAsync(Md("doc", Sections(5, "New")), cancellationToken: Ct);
        await Task.WhenAny(ingest, Task.Delay(1000, Ct));
        gate.SetResult();
        await Task.WhenAll(delete, ingest);

        await AssertConsistentAsync(failing, "doc");
        Assert.Equal(5, await ManifestChunkCount(failing, "doc"));
    }

    [Fact]
    public async Task Document_lock_serializes_same_key_and_removes_unused_entries()
    {
        var locks = new DocumentLocks();
        var first = await locks.AcquireAsync("p", "d", Ct);
        var second = locks.AcquireAsync("p", "d", Ct).AsTask();
        var other = await locks.AcquireAsync("p", "e", Ct); // a different document is not blocked
        Assert.False(second.IsCompleted);
        Assert.Equal(2, locks.Count);

        first.Dispose();
        (await second.WaitAsync(Ct)).Dispose();
        other.Dispose();
        Assert.Equal(0, locks.Count);

        // A cancelled wait releases its reference too.
        var held = await locks.AcquireAsync("p", "d", Ct);
        using var cts = new CancellationTokenSource();
        var waiting = locks.AcquireAsync("p", "d", cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        held.Dispose();
        Assert.Equal(0, locks.Count);
    }
}
