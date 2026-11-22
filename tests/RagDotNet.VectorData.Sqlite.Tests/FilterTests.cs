using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

using Rec = Dictionary<string, object?>;

public sealed class FilterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset FullDate = new(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset OtherDate = new(2026, 6, 1, 8, 0, 0, 456, TimeSpan.FromHours(-7));

    private static VectorStoreCollectionDefinition Definition() => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty("Key", typeof(string)),
            new VectorStoreDataProperty("DocumentId", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("s", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("i", typeof(int)) { IsIndexed = true },
            new VectorStoreDataProperty("l", typeof(long)) { IsIndexed = true },
            new VectorStoreDataProperty("b", typeof(bool)) { IsIndexed = true },
            new VectorStoreDataProperty("d", typeof(DateTimeOffset)) { IsIndexed = true },
            new VectorStoreDataProperty("tags", typeof(string[])) { IsIndexed = true },
            new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), CollectionTests.Dims),
        ],
    };

    // The same cases and expected sets as the store suite's filter table (StoreSuite.Filters.cs), as C# lambdas.
    private static readonly Dictionary<string, (Expression<Func<Rec, bool>> Filter, string[] Expected)> Cases = new()
    {
        ["eq-string"] = (r => (string?)r["s"] == "x", ["full"]),
        ["eq-int"] = (r => (int?)r["i"] == 1, ["full"]),
        ["eq-long"] = (r => (long?)r["l"] == 10_000_000_000L, ["full"]),
        ["eq-bool-true"] = (r => (bool?)r["b"] == true, ["full"]),
        ["eq-bool-false"] = (r => (bool?)r["b"] == false, ["other"]),
        ["eq-date"] = (r => (DateTimeOffset?)r["d"] == FullDate, ["full"]),
        ["eq-documentid"] = (r => (string?)r["DocumentId"] == "other", ["other"]),
        ["in-string"] = (r => (string?)r["s"] == "x" || (string?)r["s"] == "y", ["full", "other"]),
        ["in-int"] = (r => (int?)r["i"] == 1 || (int?)r["i"] == 2, ["full", "other"]),
        ["in-long"] = (r => (long?)r["l"] == 20_000_000_000L, ["other"]),
        ["in-bool"] = (r => (bool?)r["b"] == false, ["other"]),
        ["in-date"] = (r => (DateTimeOffset?)r["d"] == FullDate || (DateTimeOffset?)r["d"] == OtherDate, ["full", "other"]),
        ["contains"] = (r => Enumerable.Contains((string[])r["tags"]!, "red"), ["full"]),
        ["contains-any"] = (r => Enumerable.Contains((string[])r["tags"]!, "green") || Enumerable.Contains((string[])r["tags"]!, "blue"), ["full", "other"]),
        ["contains-none"] = (r => Enumerable.Contains((string[])r["tags"]!, "purple"), []),
        ["and"] = (r => (string?)r["s"] == "x" && (int?)r["i"] == 1, ["full"]),
        ["and-disjoint"] = (r => (string?)r["s"] == "x" && (int?)r["i"] == 2, []),
        ["or"] = (r => (string?)r["s"] == "x" || (int?)r["i"] == 2, ["full", "other"]),
        ["nested"] = (r => ((bool?)r["b"] == true && Enumerable.Contains((string[])r["tags"]!, "red")) || (long?)r["l"] == 20_000_000_000L, ["full", "other"]),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    private static Rec Doc(string key, Rec values)
    {
        var r = CollectionTests.Record(key);
        r["DocumentId"] = key;
        foreach (var (k, v) in values) r[k] = v;
        return r;
    }

    private static async Task<VectorStoreCollection<object, Rec>> CorpusAsync(SqliteVectorStore store)
    {
        var c = store.GetDynamicCollection("f", Definition());
        await c.EnsureCollectionExistsAsync(Ct);
        await c.UpsertAsync(
        [
            Doc("full", new() { ["s"] = "x", ["i"] = 1, ["l"] = 10_000_000_000L, ["b"] = true, ["d"] = FullDate, ["tags"] = new[] { "red", "green" } }),
            Doc("other", new() { ["s"] = "y", ["i"] = 2, ["l"] = 20_000_000_000L, ["b"] = false, ["d"] = OtherDate, ["tags"] = new[] { "blue" } }),
            Doc("bare", new()),
        ], Ct);
        return c;
    }

    private static async Task<string[]> KeysAsync(VectorStoreCollection<object, Rec> c, Expression<Func<Rec, bool>> filter) =>
        (await c.GetAsync(filter, 10, cancellationToken: Ct).ToListAsync(Ct)).Select(r => (string)r["Key"]!).Order().ToArray();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Translated_filter_matches_exactly(string name)
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);
        var (filter, expected) = Cases[name];

        Assert.Equal(expected.Order(), await KeysAsync(c, filter));
    }

    [Fact]
    public async Task Core_style_index_expressions_are_understood()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);
        var p = Expression.Parameter(typeof(Rec), "r");
        var field = Expression.Convert(Expression.MakeIndex(p, typeof(Rec).GetProperty("Item")!, [Expression.Constant("i")]), typeof(int?));
        var filter = Expression.Lambda<Func<Rec, bool>>(Expression.Equal(field, Expression.Constant(1, typeof(int?))), p);

        Assert.Equal(["full"], await KeysAsync(c, filter));
    }

    [Fact]
    public async Task Not_equal_keeps_csharp_null_semantics()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);

        Assert.Equal(["bare", "other"], await KeysAsync(c, r => (string)r["s"]! != "x"));
        Assert.Equal(["bare", "other"], await KeysAsync(c, r => !((string?)r["s"] == "x")));
        Assert.Equal(["bare"], await KeysAsync(c, r => r["s"] == null));
    }

    [Fact]
    public async Task Captured_values_are_evaluated()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);
        var v = "y";

        Assert.Equal(["other"], await KeysAsync(c, r => (string)r["s"]! == v));
    }

    [Fact]
    public async Task Comparisons_on_numbers_and_dates()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);

        Assert.Equal(["other"], await KeysAsync(c, r => (int)r["i"]! >= 2));
        Assert.Equal(["full"], await KeysAsync(c, r => (DateTimeOffset)r["d"]! < OtherDate));
        Assert.Equal(["bare"], await KeysAsync(c, r => !((int?)r["i"] >= 1)));
    }

    [Fact]
    public async Task Unsupported_node_is_named()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await CorpusAsync(store);

        var e = await Assert.ThrowsAsync<NotSupportedException>(() => KeysAsync(c, r => ((string)r["s"]!).StartsWith("x")));

        Assert.Contains("StartsWith", e.Message);
    }

    private static VectorStoreCollectionDefinition Numbered() => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty("Key", typeof(string)),
            new VectorStoreDataProperty("n", typeof(int)),
            new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), CollectionTests.Dims),
        ],
    };

    [Fact]
    public async Task Paging_with_order_by_key_is_stable()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("p", Numbered());
        await c.EnsureCollectionExistsAsync(Ct);
        // Inserted out of key order, so rowid order is not key order.
        await c.UpsertAsync(Enumerable.Range(0, 250).Reverse().Select(i => CollectionTests.Record($"k{i:D3}")), Ct);

        var keys = new List<string>();
        for (var skip = 0; skip < 300; skip += 100)
        {
            var options = new FilteredRecordRetrievalOptions<Rec> { Skip = skip, OrderBy = o => o.Ascending(r => r["Key"]) };
            keys.AddRange((await c.GetAsync(r => true, 100, options, Ct).ToListAsync(Ct)).Select(r => (string)r["Key"]!));
        }

        Assert.Equal(250, keys.Distinct().Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    [Fact]
    public async Task Descending_order_and_skip()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("p", Numbered());
        await c.EnsureCollectionExistsAsync(Ct);
        await c.UpsertAsync(new[] { 5, 1, 9, 3, 7 }.Select(n => new Rec(CollectionTests.Record($"k{n}")) { ["n"] = n }), Ct);

        var options = new FilteredRecordRetrievalOptions<Rec> { Skip = 1, OrderBy = o => o.Descending(r => r["n"]) };
        var got = await c.GetAsync(r => true, 2, options, Ct).ToListAsync(Ct);

        Assert.Equal([7, 5], got.Select(r => (int)r["n"]!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.GetAsync(r => true, 0, cancellationToken: Ct).ToListAsync(Ct));
    }
}
