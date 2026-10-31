using Microsoft.Extensions.VectorData;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Filtering;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class RetrievalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IDictionary<string, object?> Tenant(string t) => new Dictionary<string, object?> { ["tenant"] = t };

    [Fact]
    public async Task Returns_most_similar_chunks_first_with_metadata_and_heading_path()
    {
        var (pipeline, _, _, _, _) = Create(o => o.Metadata.Add<string>("tenant"));
        await pipeline.IngestAsync(Md("near", "# Guide\n\n## Setup\n\nalpha beta gamma", Tenant("a")), cancellationToken: Ct);
        await pipeline.IngestAsync(Md("far", "zulu yankee xray", Tenant("b")), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);

        Assert.Equal(2, results.Count);
        var top = results[0];
        Assert.Equal("near", top.DocumentId);
        Assert.Equal("near:0", top.ChunkKey);
        Assert.Equal(0, top.ChunkIndex);
        Assert.Equal(["Guide", "Setup"], top.HeadingPath);
        Assert.Contains("alpha beta gamma", top.Text);
        Assert.Equal("near.md", top.SourceName);
        Assert.Equal("a", top.Metadata["tenant"]);
        Assert.Single(top.Metadata);
        Assert.Equal(ScoreKind.Similarity, top.ScoreKind);
        Assert.True(top.Score > results[1].Score);
        Assert.Empty(results[1].HeadingPath);
    }

    [Fact]
    public async Task Respects_TopK()
    {
        var (pipeline, _, _, _, _) = Create();
        for (var i = 0; i < 6; i++)
            await pipeline.IngestAsync(Md($"d{i}", $"alpha beta number{i}"), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync("alpha beta", new RetrievalOptions { TopK = 3 }, Ct);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task MinScore_drops_low_similarity_results()
    {
        var (pipeline, _, _, _, _) = Create();
        await pipeline.IngestAsync(Md("near", "alpha beta gamma"), cancellationToken: Ct);
        await pipeline.IngestAsync(Md("far", "zulu yankee xray"), cancellationToken: Ct);

        var all = await pipeline.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);
        var filtered = await pipeline.RetrieveAsync("alpha beta gamma", new RetrievalOptions { MinScore = 0.5 }, Ct);

        Assert.Equal(2, all.Count);
        Assert.All(all, c => Assert.Equal(ScoreKind.Similarity, c.ScoreKind));
        var only = Assert.Single(filtered);
        Assert.Equal("near", only.DocumentId);
        Assert.Equal(ScoreKind.Similarity, only.ScoreKind);
    }

    [Fact]
    public async Task Caller_filter_is_applied_inside_the_store_query()
    {
        var (pipeline, _, _, _, _) = Create(o => o.Metadata.Add<string>("tenant"));
        for (var i = 0; i < 10; i++)
            await pipeline.IngestAsync(Md($"b{i}", "alpha beta gamma", Tenant("b")), cancellationToken: Ct);
        for (var i = 0; i < 2; i++)
            await pipeline.IngestAsync(Md($"a{i}", "alpha delta epsilon", Tenant("a")), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync(
            "alpha beta gamma", new RetrievalOptions { TopK = 5, Filter = RagFilter.Eq("tenant", "a") }, Ct);

        Assert.Equal(2, results.Count);
        Assert.All(results, c => Assert.Equal("a", c.Metadata["tenant"]));
    }

    [Fact]
    public async Task Filter_that_matches_nothing_returns_empty_without_querying()
    {
        var (pipeline, _, embedder, _, store) = Create(o => o.Metadata.Add<string>("tenant"));
        await pipeline.IngestAsync(Md("d", "alpha beta", Tenant("a")), cancellationToken: Ct);
        var calls = embedder.CallCount;

        var results = await pipeline.RetrieveAsync(
            "alpha", new RetrievalOptions { Filter = RagFilter.In("tenant", []) }, Ct);

        Assert.Empty(results);
        Assert.Equal(calls, embedder.CallCount);

        // On a pipeline that has never touched its store, nothing is created either.
        var (fresh, _, freshEmbedder, _, freshStore) = Create(o => o.Metadata.Add<string>("tenant"));
        Assert.Empty(await fresh.RetrieveAsync("alpha", new RetrievalOptions { Filter = RagFilter.In("tenant", []) }, Ct));
        Assert.Equal(0, freshEmbedder.CallCount);
        Assert.False(await freshStore.CollectionExistsAsync("test", Ct));
        Assert.NotNull(store);
    }

    [Fact]
    public async Task CosineDistance_scores_are_converted_to_similarity()
    {
        var (pipeline, _, _, _, _) = Create(o => o.DistanceFunction = DistanceFunction.CosineDistance);
        await pipeline.IngestAsync(Md("near", "alpha beta gamma"), cancellationToken: Ct);
        await pipeline.IngestAsync(Md("far", "zulu yankee xray"), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);

        Assert.Equal("near", results[0].DocumentId);
        Assert.InRange(results[0].Score, 0.99, 1.0001);
        Assert.InRange(results[1].Score, -0.0001, 0.2);
        Assert.True(results[0].Score > results[1].Score);

        var filtered = await pipeline.RetrieveAsync("alpha beta gamma", new RetrievalOptions { MinScore = 0.5 }, Ct);
        Assert.Equal("near", Assert.Single(filtered).DocumentId);
    }

    [Fact]
    public async Task Empty_or_whitespace_query_throws_ArgumentException()
    {
        var (pipeline, _, embedder, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RetrieveAsync("", cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => pipeline.RetrieveAsync("   ", cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.RetrieveAsync(null!, cancellationToken: Ct));
        Assert.Equal(0, embedder.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task TopK_out_of_range_throws_before_querying(int topK)
    {
        var (pipeline, _, embedder, _, _) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => pipeline.RetrieveAsync("alpha", new RetrievalOptions { TopK = topK }, Ct));
        Assert.Equal(0, embedder.CallCount);
    }

    [Fact]
    public async Task Chunk_key_includes_partition_values_read_from_metadata()
    {
        var (pipeline, _, _, _, _) = Create(o =>
        {
            o.Metadata.Add<string>("tenant");
            o.PartitionKeys.Add("tenant");
        });
        await pipeline.IngestAsync(Md("doc one", "alpha beta", Tenant("a b")), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync("alpha beta", cancellationToken: Ct);

        var chunk = Assert.Single(results);
        Assert.Equal(RagDotNet.Storage.ChunkKeys.Chunk(
            RagDotNet.Storage.ChunkKeys.Partition(new Dictionary<string, string> { ["tenant"] = "a b" }), "doc one", 0), chunk.ChunkKey);
    }
}
