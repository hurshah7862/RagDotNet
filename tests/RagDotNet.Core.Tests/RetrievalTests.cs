using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Pipeline;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Filtering;
using RagDotNet.Retrieval;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class RetrievalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedScope(string tenant) : IAccessScope
    {
        public ValueTask<AccessScope> GetScopeAsync(CancellationToken cancellationToken) =>
            new(new AccessScope(new Dictionary<string, string> { ["tenant"] = tenant }));
    }

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
        var (pipeline, _, _, _, _) = Create(
            o =>
            {
                o.Metadata.Add<string>("tenant");
                o.PartitionKeys.Add("tenant");
            },
            b => b.UseAccessScope(new FixedScope("a b")));
        await pipeline.IngestAsync(Md("doc one", "alpha beta", Tenant("a b")), cancellationToken: Ct);

        var results = await pipeline.RetrieveAsync("alpha beta", cancellationToken: Ct);

        var chunk = Assert.Single(results);
        Assert.Equal(RagDotNet.Storage.ChunkKeys.Chunk(
            RagDotNet.Storage.ChunkKeys.Partition(new Dictionary<string, string> { ["tenant"] = "a b" }), "doc one", 0), chunk.ChunkKey);
    }

    private static (RagPipeline Pipeline, FakeEmbeddingGenerator Embedder, FailingVectorStore Store) ScopedRag(Action<RagOptions>? configure = null)
    {
        var embedder = new FakeEmbeddingGenerator(64);
        var store = new FailingVectorStore(new InMemoryVectorStore());
        var partitioned = false;
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder)
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64)
            .Configure(o =>
            {
                o.Metadata.Add<string>("tenant").Add<string>("kind").Add<string[]>("groups");
                configure?.Invoke(o);
                partitioned = o.PartitionKeys.Count > 0;
            });
        if (partitioned) builder.UseAccessScope(new FixedScope("a"));
        return ((RagPipeline)builder.Build(), embedder, store);
    }

    private static IDictionary<string, object?> Meta(string tenant, string kind) =>
        new Dictionary<string, object?> { ["tenant"] = tenant, ["kind"] = kind, ["groups"] = new[] { "g" } };

    private static async Task Seed(RagPipeline p)
    {
        await p.IngestAsync(Md("a-doc", "alpha beta gamma", Meta("a", "doc")), cancellationToken: Ct);
        await p.IngestAsync(Md("a-faq", "alpha beta delta", Meta("a", "faq")), cancellationToken: Ct);
        await p.IngestAsync(Md("b-doc", "alpha beta gamma", Meta("b", "doc")), cancellationToken: Ct);
    }

    private static ResolvedScope Scope(RagFilter? filter) => new(new Dictionary<string, string>(), "", filter);

    private static Task<IReadOnlyList<RetrievedChunk>> Retrieve(RagPipeline p, ResolvedScope scope, RetrievalOptions? o = null) =>
        p.Engine.Retriever.RetrieveAsync("alpha beta gamma", scope, o ?? new RetrievalOptions(), Ct);

    [Fact]
    public async Task Scope_filter_alone_restricts_results()
    {
        var (p, _, _) = ScopedRag();
        await Seed(p);

        var results = await Retrieve(p, Scope(RagFilter.Eq("tenant", "a")));

        Assert.Equal(["a-doc", "a-faq"], results.Select(r => r.DocumentId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Scope_filter_and_caller_filter_are_intersected()
    {
        var (p, _, _) = ScopedRag();
        await Seed(p);

        var results = await Retrieve(p, Scope(RagFilter.Eq("tenant", "a")), new RetrievalOptions { Filter = RagFilter.Eq("kind", "doc") });
        Assert.Equal("a-doc", Assert.Single(results).DocumentId);

        // A caller filter cannot widen the scope.
        var widened = await Retrieve(p, Scope(RagFilter.Eq("tenant", "a")), new RetrievalOptions { Filter = RagFilter.Eq("tenant", "b") });
        Assert.Empty(widened);
    }

    [Fact]
    public async Task Scope_filter_that_matches_nothing_returns_empty_without_embedding()
    {
        var (p, embedder, _) = ScopedRag();
        await Seed(p);
        var calls = embedder.CallCount;

        var results = await Retrieve(p, Scope(RagFilter.ContainsAny("groups", [])));

        Assert.Empty(results);
        Assert.Equal(calls, embedder.CallCount);
    }

    [Fact]
    public async Task Scope_that_matches_nothing_wins_over_a_valid_caller_filter()
    {
        var (p, embedder, _) = ScopedRag();
        await Seed(p);
        var calls = embedder.CallCount;

        var results = await Retrieve(p, Scope(RagFilter.ContainsAny("groups", [])), new RetrievalOptions { Filter = RagFilter.Eq("tenant", "a") });

        Assert.Empty(results);
        Assert.Equal(calls, embedder.CallCount);
    }

    [Fact]
    public async Task Null_store_score_throws_instead_of_fabricating_a_score()
    {
        foreach (var distance in new[] { DistanceFunction.CosineSimilarity, DistanceFunction.CosineDistance })
        {
            var (p, _, store) = ScopedRag(o => o.DistanceFunction = distance);
            await Seed(p);
            store.NullSearchScores = true;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Retrieve(p, ResolvedScope.None));
            Assert.Contains("without a score", ex.Message);
        }
    }

    [Fact]
    public async Task Record_missing_a_partition_column_throws()
    {
        var (p, _, store) = ScopedRag(o => o.PartitionKeys.Add("tenant"));
        await Seed(p);
        store.DropColumnOnSearch = "Partition";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Retrieve(p, ResolvedScope.None));
        Assert.Contains("no Partition field", ex.Message);
    }

    [Fact]
    public async Task Equal_scores_are_ordered_by_document_id_then_chunk_index()
    {
        var (p, _, _) = ScopedRag();
        await p.IngestAsync(Md("z", "alpha beta gamma", Meta("a", "doc")), cancellationToken: Ct);
        await p.IngestAsync(Md("m", "alpha beta gamma", Meta("a", "doc")), cancellationToken: Ct);

        var results = await Retrieve(p, ResolvedScope.None);

        Assert.Equal(["m", "z"], results.Select(r => r.DocumentId));
    }

    [Fact]
    public async Task TopK_exception_names_the_TopK_parameter()
    {
        var (pipeline, _, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => pipeline.RetrieveAsync("alpha", new RetrievalOptions { TopK = 0 }, Ct));
        Assert.Equal("TopK", ex.ParamName);
    }
}
