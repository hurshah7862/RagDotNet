using RagDotNet.Content;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Embedding;
using RagDotNet.Retrieval;
using RagDotNet.StoreTests.Infrastructure;

namespace RagDotNet.StoreTests.Suite;

public abstract partial class StoreSuite<TFixture>
{
    [Fact]
    public async Task Exact_term_query_finds_its_chunk_with_hybrid()
    {
        // The twin hashes into the same embedding bucket as the target, so vector search cannot tell the two documents
        // apart; only keyword search (native or the index) can put the exact term first.
        const string target = "zx81";
        var twin = FakeEmbeddingGenerator.Colliding(target, Dimensions).First();
        var rag = Builder().Build();
        await rag.IngestAsync(Md("b", $"{twin} filler"), cancellationToken: Ct);
        await rag.IngestAsync(Md("a", $"{target} filler"), cancellationToken: Ct);

        using var capture = new HybridPathCapture();
        var hits = await rag.RetrieveAsync(target, new RetrievalOptions { TopK = 2, Hybrid = HybridMode.On }, Ct);

        Assert.Equal("a", hits[0].DocumentId);
        Assert.NotEmpty(capture.Paths);
        Assert.All(capture.Paths, p => Assert.Equal(Fixture.NativeHybrid ? "native" : "index", p));
    }

    [Fact]
    public async Task Reindex_moves_every_chunk_to_the_new_model()
    {
        var old = Builder(o => o.EmbeddingModelId = "m1").Build();
        foreach (var (id, text) in new[] { ("d1", "alpha one"), ("d2", "alpha two"), ("d3", "alpha three") })
            await old.IngestAsync(Md(id, text), cancellationToken: Ct);

        var builder = Builder(o => o.EmbeddingModelId = "m2");
        var rag = builder.Build();
        var admin = builder.BuildAdmin();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));

        var result = await admin.ReindexAsync(cancellationToken: Ct);

        Assert.Equal(3, result.DocumentsReindexed);
        Assert.Equal(["m2"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);
        Assert.Equal(["d1", "d2", "d3"], await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task Reads_during_reindex_see_only_reembedded_documents()
    {
        var old = Builder(o => o.EmbeddingModelId = "m1").Build();
        await old.IngestAsync(Md("d1", "alpha one"), cancellationToken: Ct);
        await old.IngestAsync(Md("d2", "alpha two"), cancellationToken: Ct);

        var gate = new GatedEmbeddingGenerator(
            new FakeEmbeddingGenerator(Dimensions), batch => batch.Any(t => t.Contains("alpha two", StringComparison.Ordinal)));
        var builder = Builder(o => o.EmbeddingModelId = "m2", gate);
        var rag = builder.Build();
        var admin = builder.BuildAdmin();

        var reindex = admin.ReindexAsync(cancellationToken: Ct);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(["d1"], await IdsAsync(rag, "alpha"));

        gate.Gate.SetResult();
        Assert.Equal(2, (await reindex.WaitAsync(TimeSpan.FromSeconds(30), Ct)).DocumentsReindexed);
        Assert.Equal(["d1", "d2"], await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task External_content_round_trips_and_deletes()
    {
        var content = new InMemoryChunkContentStore();
        var rag = Builder(o => o.ContentStorage = ContentStorageMode.External).UseContentStore(content).Build();

        var r = await rag.IngestAsync(Md("d1", "alpha external text"), cancellationToken: Ct);

        Assert.Equal(r.ChunkCount, content.Count);
        Assert.Contains("alpha external text", Assert.Single(await rag.RetrieveAsync("alpha external text", cancellationToken: Ct)).Text);

        Assert.True(await rag.DeleteDocumentAsync("d1", Ct));
        Assert.Equal(0, content.Count);
        Assert.Equal(0, await CountChunksAsync(rag, "d1"));
    }
}
