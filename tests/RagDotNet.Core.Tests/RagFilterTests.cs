using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Filtering;

namespace RagDotNet.Core.Tests;

public class RagFilterTests
{
    private static VectorStoreCollectionDefinition Definition() => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty("Key", typeof(string)),
            new VectorStoreDataProperty("meta_tenant", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("meta_groups", typeof(string[])) { IsIndexed = true },
            new VectorStoreDataProperty("ChunkCount", typeof(int)),
            new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 4) { DistanceFunction = DistanceFunction.CosineSimilarity },
        ],
    };

    private static Dictionary<string, object?> Rec(string key, string tenant, string[] groups) => new()
    {
        ["Key"] = key,
        ["meta_tenant"] = tenant,
        ["meta_groups"] = groups,
        ["ChunkCount"] = 1,
        ["Vector"] = new ReadOnlyMemory<float>([1f, 0f, 0f, 0f]),
    };

    private static MetadataSchema Schema() => new MetadataSchema().Add<string>("tenant").Add<string[]>("groups").Add<int>("year");

    [Fact]
    public void Unknown_field_throws_with_guidance()
    {
        var ex = Assert.Throws<ArgumentException>(() => RagFilterTranslator.Translate(RagFilter.Eq("nope", "x"), Schema(), out _));
        Assert.Contains("Unknown filter field 'nope'. Declare it with RagOptions.Metadata.Add<T>().", ex.Message);
    }

    [Fact]
    public void Eq_converts_value_to_declared_type()
    {
        var expr = RagFilterTranslator.Translate(RagFilter.Eq("year", 2024L), Schema(), out var none);
        Assert.False(none);
        Assert.True(expr!.Compile()(new() { ["meta_year"] = 2024 }));
        Assert.Throws<ArgumentException>(() => RagFilterTranslator.Translate(RagFilter.Eq("year", "x"), Schema(), out _));
    }

    [Fact]
    public void Empty_ContainsAny_matches_nothing()
    {
        var expr = RagFilterTranslator.Translate(RagFilter.ContainsAny("groups", []), Schema(), out var none);
        Assert.True(none);
        Assert.Null(expr);
    }

    [Fact]
    public void Empty_In_matches_nothing()
    {
        var expr = RagFilterTranslator.Translate(RagFilter.In("tenant", []), Schema(), out var none);
        Assert.True(none);
        Assert.Null(expr);
    }

    [Fact]
    public void And_with_empty_In_matches_nothing()
    {
        var f = RagFilter.And(RagFilter.Eq("tenant", "a"), RagFilter.In("tenant", []));
        var expr = RagFilterTranslator.Translate(f, Schema(), out var none);
        Assert.True(none);
        Assert.Null(expr);
    }

    [Fact]
    public void Or_with_only_empty_branches_matches_nothing()
    {
        var f = RagFilter.Or(RagFilter.ContainsAny("groups", []), RagFilter.In("tenant", []));
        var expr = RagFilterTranslator.Translate(f, Schema(), out var none);
        Assert.True(none);
        Assert.Null(expr);
    }

    [Fact]
    public void Null_filter_returns_null_without_matchesNothing()
    {
        Assert.Null(RagFilterTranslator.Translate(null, Schema(), out var none));
        Assert.False(none);
    }

    [Fact]
    public void Or_drops_empty_branch()
    {
        var f = RagFilter.Or(RagFilter.ContainsAny("groups", []), RagFilter.Eq("tenant", "a"));
        var expr = RagFilterTranslator.Translate(f, Schema(), out var none);
        Assert.False(none);
        var fn = expr!.Compile();
        Assert.True(fn(new() { ["meta_tenant"] = "a" }));
        Assert.False(fn(new() { ["meta_tenant"] = "b" }));
    }

    [Fact]
    public void Contains_on_non_array_field_throws()
    {
        Assert.Throws<ArgumentException>(() => RagFilterTranslator.Translate(RagFilter.Contains("tenant", "a"), Schema(), out _));
    }

    [Fact]
    public void StorageName_maps_fields()
    {
        Assert.Equal("DocumentId", RagFilterTranslator.StorageName("DocumentId", new MetadataSchema()));
        Assert.Equal("meta_x", RagFilterTranslator.StorageName("x", new MetadataSchema().Add<string>("x")));
    }

    [Fact]
    public void Schema_validates_types_names_and_duplicates()
    {
        Assert.Throws<ArgumentException>(() => new MetadataSchema().Add<double>("x"));
        Assert.Throws<ArgumentException>(() => new MetadataSchema().Add<string>("1x"));
        Assert.Throws<ArgumentException>(() => new MetadataSchema().Add<string>("a").Add<int>("a"));
        var s = new MetadataSchema().Add<string>("a").Add<DateTimeOffset>("b").Add<bool>("c").Add<long>("d");
        Assert.Equal(4, s.Fields.Count);
    }

    [Fact]
    public async Task Translated_filters_select_expected_records_in_InMemory_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var col = new InMemoryVectorStore().GetDynamicCollection("t2", Definition());
        await col.EnsureCollectionExistsAsync(ct);
        await col.UpsertAsync(Rec("1", "a", ["g1"]), ct);
        await col.UpsertAsync(Rec("2", "b", ["g2"]), ct);
        await col.UpsertAsync(Rec("3", "a", ["g1", "g2"]), ct);
        await col.UpsertAsync(Rec("4", "b", []), ct);
        var schema = new MetadataSchema().Add<string>("tenant").Add<string[]>("groups");

        async Task<string[]> Run(RagFilter f)
        {
            var expr = RagFilterTranslator.Translate(f, schema, out var none);
            Assert.False(none);
            var keys = new List<string>();
            await foreach (var r in col.GetAsync(expr!, 10, new FilteredRecordRetrievalOptions<Dictionary<string, object?>>(), ct))
                keys.Add((string)r["Key"]!);
            return keys.Order().ToArray();
        }

        Assert.Equal(["1", "3"], await Run(RagFilter.Eq("tenant", "a")));
        Assert.Equal(["1", "2", "3", "4"], await Run(RagFilter.In("tenant", ["a", "b"])));
        Assert.Equal(["2", "3"], await Run(RagFilter.Contains("groups", "g2")));
        Assert.Equal(["2", "3"], await Run(RagFilter.ContainsAny("groups", ["g2", "g3"])));
        Assert.Equal(["3"], await Run(RagFilter.And(RagFilter.Eq("tenant", "a"), RagFilter.Contains("groups", "g2"))));
        Assert.Equal(["1", "2", "3"], await Run(RagFilter.Eq("tenant", "a") | RagFilter.Contains("groups", "g2")));
        Assert.Equal(["3"], await Run(RagFilter.Eq("tenant", "a") & RagFilter.Contains("groups", "g2")));
    }

    [Fact]
    public async Task Dynamic_InMemory_collection_supports_indexer_filters_and_filtered_get()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryVectorStore();
        var col = store.GetDynamicCollection("t", Definition());
        await col.EnsureCollectionExistsAsync(ct);
        await col.UpsertAsync(Rec("1", "a", ["g1"]), ct);
        await col.UpsertAsync(Rec("2", "b", ["g2"]), ct);
        await col.UpsertAsync(Rec("3", "a", ["g1", "g2"]), ct);

        ReadOnlyMemory<float> v = new float[] { 1, 0, 0, 0 };
        var r1 = new List<string>();
        await foreach (var x in col.SearchAsync(v, 10, new VectorSearchOptions<Dictionary<string, object?>> { Filter = r => (string)r["meta_tenant"]! == "a" }, ct))
            r1.Add((string)x.Record["Key"]!);
        Assert.Equal(["1", "3"], r1.Order());

        var r2 = new List<string>();
        await foreach (var x in col.SearchAsync(v, 10, new VectorSearchOptions<Dictionary<string, object?>> { Filter = r => ((string[])r["meta_groups"]!).Contains("g1") }, ct))
            r2.Add((string)x.Record["Key"]!);
        Assert.Equal(["1", "3"], r2.Order());

        var page = new List<string>();
        await foreach (var x in col.GetAsync(r => (int)r["ChunkCount"]! >= 0, 10,
            new FilteredRecordRetrievalOptions<Dictionary<string, object?>> { OrderBy = o => o.Ascending(r => r["Key"]!), Skip = 1 }, ct))
            page.Add((string)x["Key"]!);
        Assert.Equal(["2", "3"], page);
    }
}
