using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Embedding;
using RagDotNet.Pipeline;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class AdminAndModelSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedScope(string tenant) : IAccessScope
    {
        public ValueTask<AccessScope> GetScopeAsync(CancellationToken cancellationToken) =>
            new(new AccessScope(new Dictionary<string, string> { ["tenant"] = tenant }));
    }

    private static RagPipelineBuilder NewBuilder(VectorStore store, FakeEmbeddingGenerator embedder, string modelId, bool partitioned)
    {
        var b = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder)
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64)
            .Configure(o =>
            {
                o.EmbeddingModelId = modelId;
                o.EmbeddingBatchSize = 2;
                if (partitioned)
                {
                    o.Metadata.Add<string>("tenant");
                    o.PartitionKeys.Add("tenant");
                }
            });
        return b;
    }

    private static async Task SeedAsync(VectorStore store, bool partitioned = false)
    {
        var b = NewBuilder(store, new FakeEmbeddingGenerator(64), "fake-embed", partitioned);
        if (!partitioned)
        {
            var rag = b.Build();
            await rag.IngestAsync(Md("d1", "# A\n\nalpha beta gamma"), cancellationToken: Ct);
            await rag.IngestAsync(Md("d2", "# B\n\nzulu yankee xray"), cancellationToken: Ct);
            return;
        }

        foreach (var t in new[] { "a", "b" })
        {
            var rag = NewBuilder(store, new FakeEmbeddingGenerator(64), "fake-embed", true).UseAccessScope(new FixedScope(t)).Build();
            await rag.IngestAsync(Md("d1", "alpha beta gamma"), cancellationToken: Ct);
            if (t == "a") await rag.IngestAsync(Md("d2", "zulu yankee xray"), cancellationToken: Ct);
        }
    }

    [Fact]
    public async Task Switching_model_id_throws_mismatch_on_ingest_retrieve_and_delete()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);

        var rag = NewBuilder(store, new FakeEmbeddingGenerator(64), "other", false).Build();

        var ex = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.IngestAsync(Md("d3", "new text"), cancellationToken: Ct));
        Assert.Equal("fake-embed", ex.StoredModelId);
        Assert.Equal("other", ex.ConfiguredModelId);
        Assert.Contains("IRagAdmin.ReindexAsync()", ex.Message);
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.DeleteDocumentAsync("d1", Ct));
    }

    [Fact]
    public async Task Reindex_reembeds_all_partitions_and_clears_mismatch()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store, partitioned: true);

        var embedder = new FakeEmbeddingGenerator(64);
        var builder = NewBuilder(store, embedder, "other", true).UseAccessScope(new FixedScope("a"));
        var rag = builder.Build();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));

        var admin = builder.BuildAdmin();
        var progress = new List<ReindexProgress>();
        var result = await admin.ReindexAsync(new Progress<ReindexProgress>(progress.Add), Ct);

        Assert.Equal(3, result.DocumentsReindexed);
        Assert.Equal(0, result.DocumentsSkipped);
        Assert.Equal(3, result.ChunksReindexed);
        Assert.Equal(["other"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);

        var hits = await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);
        Assert.NotEmpty(hits);
        Assert.Equal("d1", hits[0].DocumentId);
    }

    [Fact]
    public async Task Reindex_skips_documents_already_on_current_model()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var embedder = new FakeEmbeddingGenerator(64);
        var admin = NewBuilder(store, embedder, "other", false).BuildAdmin();

        var first = await admin.ReindexAsync(cancellationToken: Ct);
        Assert.Equal(2, first.DocumentsReindexed);
        var calls = embedder.CallCount;

        var second = await admin.ReindexAsync(cancellationToken: Ct);
        Assert.Equal(0, second.DocumentsReindexed);
        Assert.Equal(2, second.DocumentsSkipped);
        Assert.Equal(calls, embedder.CallCount);
    }

    [Fact]
    public async Task Reindex_with_different_dimensions_throws_before_writing()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var admin = NewBuilder(store, new FakeEmbeddingGenerator(32), "other", false).BuildAdmin();

        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ReindexAsync(cancellationToken: Ct));

        Assert.Equal(["fake-embed"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);
    }

    private sealed class FlakyEmbedder(FakeEmbeddingGenerator inner) : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        public int FailOnCall { get; set; }

        private int _calls;

        public async Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (FailOnCall > 0 && ++_calls == FailOnCall) throw new IOException("Simulated embedder failure.");
            return await inner.GenerateAsync(values, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Interrupted_reindex_resumes()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var embedder = new FlakyEmbedder(new FakeEmbeddingGenerator(64)) { FailOnCall = 3 }; // probe, d1, then d2 fails
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder)
            .UseVectorStore(store, "test", 64)
            .Configure(o => o.EmbeddingModelId = "other");
        var admin = builder.BuildAdmin();
        var rag = builder.Build();

        await Assert.ThrowsAsync<IOException>(() => admin.ReindexAsync(cancellationToken: Ct));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Equal(["fake-embed", "other"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);

        var result = await admin.ReindexAsync(cancellationToken: Ct);
        Assert.Equal(1, result.DocumentsReindexed);
        Assert.Equal(1, result.DocumentsSkipped);
        Assert.Equal(["other"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);
        Assert.NotEmpty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Stats_report_documents_and_chunks_per_partition()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store, partitioned: true);
        var admin = NewBuilder(store, new FakeEmbeddingGenerator(64), "fake-embed", true).BuildAdmin();

        var stats = await admin.GetStatsAsync(Ct);

        Assert.Equal(["fake-embed"], stats.EmbeddingModelIds);
        Assert.Equal(2, stats.Partitions.Count);
        var a = Assert.Single(stats.Partitions, p => p.Partition["tenant"] == "a");
        var b = Assert.Single(stats.Partitions, p => p.Partition["tenant"] == "b");
        Assert.Equal((2, 2), (a.DocumentCount, a.ChunkCount));
        Assert.Equal((1, 1), (b.DocumentCount, b.ChunkCount));
    }

    [Fact]
    public async Task Admin_delete_targets_named_partition()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store, partitioned: true);
        var admin = NewBuilder(store, new FakeEmbeddingGenerator(64), "fake-embed", true).BuildAdmin();

        Assert.True(await admin.DeleteDocumentAsync("d1", new Dictionary<string, string> { ["tenant"] = "a" }, Ct));
        Assert.False(await admin.DeleteDocumentAsync("d1", new Dictionary<string, string> { ["tenant"] = "a" }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.DeleteDocumentAsync("d1", new Dictionary<string, string> { ["other"] = "a" }, Ct));

        var stats = await admin.GetStatsAsync(Ct);
        Assert.Equal(1, stats.Partitions.Single(p => p.Partition["tenant"] == "a").DocumentCount);
        Assert.Equal(1, stats.Partitions.Single(p => p.Partition["tenant"] == "b").DocumentCount);
    }

    private sealed class ReversingEmbedder(FakeEmbeddingGenerator inner) : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        public List<string> Inputs { get; } = [];

        public async Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            Inputs.AddRange(list);
            var r = await inner.GenerateAsync(list, options, cancellationToken);
            var o = new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>();
            foreach (var e in r) o.Add(new Microsoft.Extensions.AI.Embedding<float>(e.Vector.ToArray().Reverse().ToArray()));
            return o;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
        }
    }

    private static RagOptions RawOptions() => new() { CollectionName = "test", EmbeddingDimensions = 64 };

    [Fact]
    public async Task Reindex_handles_dirty_manifest_and_pending_cleanup_chunks()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var opts = RawOptions();
        var manifests = new ManifestStore(store, opts);
        var chunks = new ChunkStore(store, opts);

        // Simulate a crashed ingest: dirty marker, plus a leftover chunk 1 beyond ChunkCount.
        var m = (await manifests.GetAsync("", "d1", Ct))!;
        Assert.Equal(1, m.ChunkCount);
        var extra = (await chunks.GetAsync([ChunkKeys.Chunk("", "d1", 0)], true, Ct)).Single();
        extra["Key"] = ChunkKeys.Chunk("", "d1", 1);
        extra["ChunkIndex"] = 1;
        await chunks.UpsertAsync([extra], Ct);
        var dirty = m with { ContentHash = "", ChunkHighWater = 2 };
        await manifests.UpsertAsync(dirty, Ct);

        var admin = NewBuilder(store, new FakeEmbeddingGenerator(64), "other", false).BuildAdmin();
        var result = await admin.ReindexAsync(cancellationToken: Ct);

        Assert.Equal(2, result.DocumentsReindexed);
        Assert.Equal(3, result.ChunksReindexed); // d1 chunk 0, leftover chunk 1, d2 chunk 0
        var after = (await manifests.GetAsync("", "d1", Ct))!;
        Assert.Equal(dirty with { EmbeddingModelId = "other", PartitionValues = after.PartitionValues }, after);
        var rec = await chunks.GetAsync([ChunkKeys.Chunk("", "d1", 0), ChunkKeys.Chunk("", "d1", 1)], false, Ct);
        Assert.Equal(2, rec.Count);
        Assert.All(rec, r => Assert.Equal("other", r["EmbeddingModelId"]));
    }

    [Fact]
    public async Task Reindex_replaces_chunk_model_and_vectors_and_preserves_heading_path()
    {
        var store = new InMemoryVectorStore();
        var seedEmbedder = new FakeEmbeddingGenerator(64);
        var seed = NewBuilder(store, seedEmbedder, "fake-embed", false).Build();
        await seed.IngestAsync(Md("d1", string.Join("\n\n", "# Install", "## Linux", "### Debian", "apt install foo")), cancellationToken: Ct);
        var opts = RawOptions();
        var chunks = new ChunkStore(store, opts);
        var key = ChunkKeys.Chunk("", "d1", 0);
        var before = (await chunks.GetAsync([key], true, Ct)).Single();
        Assert.Equal("Install > Linux > Debian", before["HeadingPath"]);
        var beforeVector = ((ReadOnlyMemory<float>)before["Embedding"]!).ToArray();
        var ingestInput = seedEmbedder.Inputs.Single();

        var reverse = new ReversingEmbedder(new FakeEmbeddingGenerator(64));
        var admin = new RagPipelineBuilder().UseEmbeddingGenerator(reverse).UseVectorStore(store, "test", 64)
            .Configure(o => o.EmbeddingModelId = "other").BuildAdmin();
        await admin.ReindexAsync(cancellationToken: Ct);

        var after = (await chunks.GetAsync([key], true, Ct)).Single();
        Assert.Equal("other", after["EmbeddingModelId"]);
        Assert.Equal("Install > Linux > Debian", after["HeadingPath"]);
        Assert.Equal(before["Text"], after["Text"]);
        Assert.Equal(beforeVector.Reverse().ToArray(), ((ReadOnlyMemory<float>)after["Embedding"]!).ToArray());
        Assert.NotEqual(beforeVector, ((ReadOnlyMemory<float>)after["Embedding"]!).ToArray());
        Assert.Equal(ingestInput, reverse.Inputs.Last()); // same embedding input as a fresh ingest
        Assert.Contains("Install > Linux > Debian", ingestInput);
    }

    [Fact]
    public async Task Null_stored_model_id_fails_closed()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var opts = RawOptions();
        var manifests = new ManifestStore(store, opts);
        var raw = (await manifests.GetAsync("", "d1", Ct))!.ToDynamic(RagKeyType.String);
        raw["EmbeddingModelId"] = null;
        await store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(opts)).UpsertAsync(raw, Ct);

        var rag = NewBuilder(store, new FakeEmbeddingGenerator(64), "fake-embed", false).Build();

        var ex = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Equal("", ex.StoredModelId);
    }

    [Fact]
    public async Task Reindex_throws_naming_chunk_when_text_is_missing()
    {
        var store = new InMemoryVectorStore();
        await SeedAsync(store);
        var chunks = new ChunkStore(store, RawOptions());
        var rec = (await chunks.GetAsync([ChunkKeys.Chunk("", "d2", 0)], true, Ct)).Single();
        rec["Text"] = null;
        await chunks.UpsertAsync([rec], Ct);

        var admin = NewBuilder(store, new FakeEmbeddingGenerator(64), "other", false).BuildAdmin();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ReindexAsync(cancellationToken: Ct));
        Assert.Contains("d2", ex.Message);
        Assert.Contains("0", ex.Message);
    }
}
