using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

using Rec = Dictionary<string, object?>;

public sealed class HybridTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ReadOnlyMemory<float> Query = new float[] { 1f, 0f, 0f, 0f };

    // Reciprocal rank fusion with k = 60: rank 1 in one list scores 1/61; rank 1 in both scores 2/61.
    private const double OneList = 1.0 / 61;
    private const double BothLists = 2.0 / 61;

    private static VectorStoreCollectionDefinition TextDefinition() =>
        CollectionTests.Definition(typeof(string), extra: new VectorStoreDataProperty("Text", typeof(string)) { IsFullTextIndexed = true });

    private static Rec Doc(string key, string? text) => new(CollectionTests.Record(key)) { ["Text"] = text, ["S"] = key };

    private static async Task<VectorStoreCollection<object, Rec>> CollectionAsync(SqliteVectorStore store, params Rec[] records)
    {
        var c = store.GetDynamicCollection("h", TextDefinition());
        await c.EnsureCollectionExistsAsync(Ct);
        if (records.Length > 0) await c.UpsertAsync(records, Ct);
        return c;
    }

    private static Task<List<VectorSearchResult<Rec>>> HybridAsync(VectorStoreCollection<object, Rec> c, string[] keywords, int top = 10, HybridSearchOptions<Rec>? options = null) =>
        ((IKeywordHybridSearchable<Rec>)c).HybridSearchAsync(Query, keywords, top, options, Ct).ToListAsync(Ct);

    private static string Key(VectorSearchResult<Rec> r) => (string)r.Record["Key"]!;

    [Fact]
    public async Task Exact_term_wins_over_an_identical_vector()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        // Same vector; "other" is inserted first, so it ranks first by vector alone.
        var c = await CollectionAsync(store, Doc("other", "other filler"), Doc("target", "zx81 filler"));

        var hits = await HybridAsync(c, ["zx81"], options: new() { AdditionalProperty = r => r["Text"] });

        Assert.Equal(["target", "other"], hits.Select(Key));
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public async Task Hybrid_applies_the_filter_to_both_rankings()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CollectionAsync(store, Doc("other", "other filler"), Doc("target", "zx81 filler"));

        var hits = await HybridAsync(c, ["zx81"], options: new() { Filter = r => (string?)r["S"] != "target" });

        Assert.Equal(["other"], hits.Select(Key));
    }

    [Fact]
    public async Task Hybrid_keywords_with_fts_syntax_are_plain_words()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CollectionAsync(store, Doc("other", "nothing here"), Doc("target", "x a-b y"));

        var hits = await HybridAsync(c, ["\"quoted\"", "a-b", "x*", "col:val", "(", "NEAR", "AND", "OR", "  "]);

        Assert.Equal("target", Key(hits[0]));
    }

    [Fact]
    public async Task Updated_and_deleted_text_leaves_keyword_search()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CollectionAsync(store, Doc("d1", "alpha"));
        Assert.Equal(BothLists, Assert.Single(await HybridAsync(c, ["alpha"])).Score!.Value, 1e-9);

        await c.UpsertAsync(Doc("d1", "beta"), Ct);
        Assert.Equal(OneList, Assert.Single(await HybridAsync(c, ["alpha"])).Score!.Value, 1e-9);
        Assert.Equal(BothLists, Assert.Single(await HybridAsync(c, ["beta"])).Score!.Value, 1e-9);

        await c.UpsertAsync(Doc("d1", null), Ct);
        Assert.Equal(OneList, Assert.Single(await HybridAsync(c, ["beta"])).Score!.Value, 1e-9);

        // A deleted record's text must not reattach to a new record that reuses its rowid.
        await c.UpsertAsync(Doc("d1", "beta"), Ct);
        await c.DeleteAsync("d1", Ct);
        await c.UpsertAsync(Doc("d2", "gamma"), Ct);
        var hit = Assert.Single(await HybridAsync(c, ["beta"]));
        Assert.Equal("d2", Key(hit));
        Assert.Equal(OneList, hit.Score!.Value, 1e-9);
    }

    [Fact]
    public async Task Hybrid_works_after_reopen()
    {
        using var db = new TempDatabase();
        using (var first = new SqliteVectorStore(db.ConnectionString))
            await CollectionAsync(first, Doc("other", "other filler"), Doc("target", "zx81 filler"));

        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CollectionAsync(store);

        Assert.Equal("target", Key((await HybridAsync(c, ["zx81"]))[0]));
    }

    [Fact]
    public async Task Skip_top_and_empty_keywords()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CollectionAsync(store, Doc("a", "one"), Doc("b", "two"), Doc("c", "zx81"));

        Assert.Equal(["a"], (await HybridAsync(c, ["zx81"], 1, new() { Skip = 1 })).Select(Key));
        Assert.Equal(["a", "b", "c"], (await HybridAsync(c, [])).Select(Key));
        await Assert.ThrowsAsync<NotSupportedException>(() => HybridAsync(c, ["zx81"], options: new() { ScoreThreshold = 0.5 }));
    }

    [Fact]
    public async Task No_full_text_property_is_not_supported()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("plain", CollectionTests.Definition(typeof(string)));
        await c.EnsureCollectionExistsAsync(Ct);

        var e = await Assert.ThrowsAsync<NotSupportedException>(() => HybridAsync(c, ["x"]));
        Assert.Contains("full-text", e.Message);
        var twice = Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("two", CollectionTests.Definition(typeof(string), extra:
            [new VectorStoreDataProperty("T1", typeof(string)) { IsFullTextIndexed = true }, new VectorStoreDataProperty("T2", typeof(string)) { IsFullTextIndexed = true }])));
        Assert.Contains("full-text", twice.Message);
    }
}
