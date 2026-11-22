using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

using Rec = Dictionary<string, object?>;

public sealed class SearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ReadOnlyMemory<float> Query = new float[] { 1f, 0f, 0f, 0f };

    private static async Task<VectorStoreCollection<object, Rec>> AnglesAsync(SqliteVectorStore store, string distance)
    {
        var c = store.GetDynamicCollection("v", CollectionTests.Definition(typeof(string), distance));
        await c.EnsureCollectionExistsAsync(Ct);
        await c.UpsertAsync(
        [
            new Rec(CollectionTests.Record("deg90", [0f, 1f, 0f, 0f])) { ["I"] = 90 },
            new Rec(CollectionTests.Record("deg0", [2f, 0f, 0f, 0f])) { ["I"] = 0 },
            new Rec(CollectionTests.Record("deg45", [1f, 1f, 0f, 0f])) { ["I"] = 45 },
        ], Ct);
        return c;
    }

    private static Task<List<VectorSearchResult<Rec>>> SearchAsync(VectorStoreCollection<object, Rec> c, int top = 10, VectorSearchOptions<Rec>? options = null) =>
        c.SearchAsync(Query, top, options, Ct).ToListAsync(Ct);

    [Fact]
    public async Task Nearest_first_with_cosine_similarity()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await AnglesAsync(store, DistanceFunction.CosineSimilarity);

        var hits = await SearchAsync(c);

        Assert.Equal(["deg0", "deg45", "deg90"], hits.Select(h => (string)h.Record["Key"]!));
        Assert.Equal(1, hits[0].Score!.Value, 1e-5);
        Assert.Equal(Math.Sqrt(0.5), hits[1].Score!.Value, 1e-5);
        Assert.Equal(0, hits[2].Score!.Value, 1e-5);
    }

    [Fact]
    public async Task Cosine_distance_scores_are_distances()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await AnglesAsync(store, DistanceFunction.CosineDistance);

        var hits = await SearchAsync(c);

        Assert.Equal(["deg0", "deg45", "deg90"], hits.Select(h => (string)h.Record["Key"]!));
        Assert.Equal(0, hits[0].Score!.Value, 1e-5);
        Assert.Equal(1 - Math.Sqrt(0.5), hits[1].Score!.Value, 1e-5);
        Assert.Equal(1, hits[2].Score!.Value, 1e-5);
    }

    [Fact]
    public async Task Search_applies_filter_skip_and_threshold()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await AnglesAsync(store, DistanceFunction.CosineSimilarity);

        var filtered = await SearchAsync(c, options: new() { Filter = r => (int?)r["I"] != 0 });
        var skipped = await SearchAsync(c, options: new() { Skip = 1 });
        var threshold = await SearchAsync(c, options: new() { ScoreThreshold = 0.5 });
        var vectors = await SearchAsync(c, 1, new() { IncludeVectors = true });

        Assert.Equal(["deg45", "deg90"], filtered.Select(h => (string)h.Record["Key"]!));
        Assert.Equal(["deg45", "deg90"], skipped.Select(h => (string)h.Record["Key"]!));
        Assert.Equal(["deg0", "deg45"], threshold.Select(h => (string)h.Record["Key"]!));
        Assert.Equal([2f, 0f, 0f, 0f], ((ReadOnlyMemory<float>)Assert.Single(vectors).Record["Embedding"]!).ToArray());
        Assert.True(!filtered[0].Record.TryGetValue("Embedding", out var v) || v is null);
    }

    [Fact]
    public async Task Filter_never_cuts_results_short()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("v", CollectionTests.Definition(typeof(string)));
        await c.EnsureCollectionExistsAsync(Ct);
        // 293 records right next to the query and 7 far from it; only the far ones match the filter.
        await c.UpsertAsync(Enumerable.Range(0, 300).Select(i => new Rec(CollectionTests.Record($"k{i}", i < 7 ? [0f, 0f, 1f, i] : [1f, i / 1000f, 0f, 0f]))
        {
            ["S"] = i < 7 ? "far" : "near",
        }), Ct);

        var hits = await SearchAsync(c, 10, new() { Filter = r => (string?)r["S"] == "far" });

        Assert.Equal(7, hits.Count);
    }

    [Fact]
    public async Task Wrong_query_dimension_is_rejected()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await AnglesAsync(store, DistanceFunction.CosineSimilarity);

        await Assert.ThrowsAsync<ArgumentException>(() => c.SearchAsync(new ReadOnlyMemory<float>([1f, 0f, 0f]), 3, cancellationToken: Ct).ToListAsync(Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => c.SearchAsync("text", 3, cancellationToken: Ct).ToListAsync(Ct));
    }
}
