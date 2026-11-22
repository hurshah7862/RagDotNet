using RagDotNet.Chunking;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;

namespace RagDotNet.StoreTests.Suite;

public abstract partial class StoreSuite<TFixture>
{
    /// <summary>Small fixed-size chunks, so the number of words decides the number of chunks.</summary>
    private static readonly IngestOptions SmallChunks =
        new() { Chunking = new ChunkingOptions { Chunker = Chunkers.FixedSize, MaxTokens = 16, OverlapRatio = 0 } };

    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => "w" + i));

    [Fact]
    public async Task Unchanged_reingest_writes_nothing()
    {
        var embedder = new FakeEmbeddingGenerator(Dimensions);
        var rag = Builder(embedder: embedder).Build();
        await rag.IngestAsync(Md("d1", Words(60)), SmallChunks, cancellationToken: Ct);
        var calls = embedder.CallCount;

        var again = await rag.IngestAsync(Md("d1", Words(60)), SmallChunks, cancellationToken: Ct);

        Assert.Equal(IngestStatus.Unchanged, again.Status);
        Assert.Equal(calls, embedder.CallCount);
    }

    [Fact]
    public async Task Update_with_fewer_chunks_leaves_no_orphans()
    {
        var rag = Builder().Build();
        var first = await rag.IngestAsync(Md("d1", Words(200)), SmallChunks, cancellationToken: Ct);
        var second = await rag.IngestAsync(Md("d1", Words(40)), SmallChunks, cancellationToken: Ct);

        Assert.Equal(IngestStatus.Updated, second.Status);
        Assert.True(second.ChunkCount < first.ChunkCount, $"{second.ChunkCount} chunks after the update, {first.ChunkCount} before");
        Assert.Equal(second.ChunkCount, await CountChunksAsync(rag, "d1"));
    }

    [Fact]
    public async Task Update_with_more_chunks_stores_all_of_them()
    {
        var rag = Builder().Build();
        var first = await rag.IngestAsync(Md("d1", Words(40)), SmallChunks, cancellationToken: Ct);
        var second = await rag.IngestAsync(Md("d1", Words(200)), SmallChunks, cancellationToken: Ct);

        Assert.Equal(IngestStatus.Updated, second.Status);
        Assert.True(second.ChunkCount > first.ChunkCount);
        Assert.Equal(second.ChunkCount, await CountChunksAsync(rag, "d1"));
    }

    [Fact]
    public async Task Delete_removes_document_and_chunks()
    {
        var builder = Builder();
        var rag = builder.Build();
        var admin = builder.BuildAdmin();
        await rag.IngestAsync(Md("d1", Words(100)), SmallChunks, cancellationToken: Ct);
        await rag.IngestAsync(Md("d2", "kept document"), cancellationToken: Ct);

        Assert.True(await rag.DeleteDocumentAsync("d1", Ct));

        Assert.Equal(0, await CountChunksAsync(rag, "d1"));
        Assert.Equal(1, await CountChunksAsync(rag, "d2"));
        Assert.False(await rag.DeleteDocumentAsync("d1", Ct));
        Assert.Equal(1, Assert.Single((await admin.GetStatsAsync(Ct)).Partitions).DocumentCount);
    }

    [Fact]
    public async Task Stats_count_documents_and_chunks()
    {
        var builder = Builder();
        var rag = builder.Build();
        var admin = builder.BuildAdmin();
        var total = 0;
        foreach (var (id, words) in new[] { ("d1", 20), ("d2", 60), ("d3", 120) })
            total += (await rag.IngestAsync(Md(id, Words(words)), SmallChunks, cancellationToken: Ct)).ChunkCount;

        var stats = await admin.GetStatsAsync(Ct);

        var partition = Assert.Single(stats.Partitions);
        Assert.Equal(3, partition.DocumentCount);
        Assert.Equal(total, partition.ChunkCount);
        Assert.Equal(["fake-embed"], stats.EmbeddingModelIds);
    }

    [Fact]
    public async Task Listing_sees_every_document_across_pages()
    {
        // Core lists documents 100 at a time; 201 needs three pages.
        var builder = Builder();
        var rag = builder.Build();
        var admin = builder.BuildAdmin();
        var sources = Enumerable.Range(0, 201).Select(i => Md($"doc{i:000}", $"document number u{i:000}")).ToList();
        await rag.IngestManyAsync(sources, cancellationToken: Ct);

        Assert.Equal(201, Assert.Single((await admin.GetStatsAsync(Ct)).Partitions).DocumentCount);

        // Distinct ids, not a count: unstable paging could repeat some documents and miss others and still total 201.
        var progress = new DistinctIds();
        var reindex = await admin.ReindexAsync(progress, Ct);
        Assert.Equal(201, reindex.DocumentsReindexed + reindex.DocumentsSkipped);
        Assert.Equal(sources.Select(s => s.DocumentId).Order(), progress.Ids.Order());

        // The keyword index is rebuilt from the listing too; with native hybrid search there is no index to rebuild.
        if (!Fixture.NativeHybrid)
        {
            await admin.RebuildKeywordIndexAsync(Ct);
            foreach (var i in new[] { 0, 100, 200 })
            {
                var hits = await rag.RetrieveAsync($"u{i:000}", new RetrievalOptions { Hybrid = HybridMode.On }, Ct);
                Assert.Equal($"doc{i:000}", hits[0].DocumentId);
            }
        }
    }

    /// <summary>Collects reindex progress synchronously (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class DistinctIds : IProgress<ReindexProgress>
    {
        private readonly HashSet<string> _ids = [];

        public IReadOnlyCollection<string> Ids
        {
            get { lock (_ids) return _ids.ToList(); }
        }

        public void Report(ReindexProgress value)
        {
            if (value.CurrentDocumentId is { } id)
                lock (_ids) _ids.Add(id);
        }
    }
}
