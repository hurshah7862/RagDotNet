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

    private static MetadataSchema TypedSchema() => new MetadataSchema()
        .Add<int>("year").Add<long>("rev").Add<bool>("flag").Add<string>("tenant").Add<DateTimeOffset>("at");

    private static void Accepts(string field, object value, object stored)
    {
        var expr = RagFilterTranslator.Translate(RagFilter.Eq(field, value), TypedSchema(), out _);
        Assert.True(expr!.Compile()(new() { ["meta_" + field] = stored }));
    }

    private static void Rejects(string field, object value)
    {
        var ex = Assert.Throws<ArgumentException>(() => RagFilterTranslator.Translate(RagFilter.Eq(field, value), TypedSchema(), out _));
        Assert.Contains($"'{field}'", ex.Message);
        Assert.Contains(value.GetType().Name, ex.Message);
    }

    public static TheoryData<string, object, object> ExactConversions() => new()
    {
        { "year", 2024, 2024 },
        { "year", 2024L, 2024 },
        { "year", (short)7, 7 },
        { "year", 2024.0, 2024 },
        { "year", 2024.0f, 2024 },
        { "year", 2024m, 2024 },
        { "rev", 5, 5L },
        { "rev", 5L, 5L },
        { "rev", 5.0, 5L },
        { "rev", 9_007_199_254_740_992m, 9_007_199_254_740_992L },
        { "flag", true, true },
        { "tenant", "a", "a" },
        { "at", new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)) },
        { "at", new DateTime(2024, 1, 2, 1, 4, 5, DateTimeKind.Utc), new DateTimeOffset(2024, 1, 2, 1, 4, 5, TimeSpan.Zero) },
        { "at", "2024-01-02T03:04:05+02:00", new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)) },
        { "at", "2024-01-02T01:04:05Z", new DateTimeOffset(2024, 1, 2, 1, 4, 5, TimeSpan.Zero) },
    };

    [Theory]
    [MemberData(nameof(ExactConversions))]
    public void Filter_values_convert_exactly(string field, object value, object stored) => Accepts(field, value, stored);

    [Fact]
    public void Local_DateTime_filter_value_is_accepted()
    {
        var local = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Local);
        Accepts("at", local, new DateTimeOffset(local));
    }

    public static TheoryData<string, object> LossyConversions() => new()
    {
        { "year", 2024.7 },
        { "year", 2024.5m },
        { "year", 1.5f },
        { "year", 3_000_000_000L },
        { "year", 1e20 },
        { "year", double.NaN },
        { "year", "2024" },
        { "year", true },
        { "rev", 2.5 },
        { "rev", 1e30 },
        { "rev", "5" },
        { "flag", 1 },
        { "flag", "true" },
        { "tenant", 5 },
        { "tenant", true },
        { "at", new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Unspecified) },
        { "at", "2024-01-02T03:04:05" },
        { "at", "2024-01-02" },
        { "at", "not a date" },
        { "at", 1_700_000_000L },
    };

    [Theory]
    [MemberData(nameof(LossyConversions))]
    public void Lossy_or_ambiguous_filter_values_are_rejected(string field, object value) => Rejects(field, value);

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
    public void Schema_rejects_reserved_DocumentId_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => new MetadataSchema().Add<string>("DocumentId"));
        Assert.Contains("DocumentId", ex.Message);
        Assert.Contains("reserved", ex.Message);
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

    [Fact]
    public void Evaluator_null_array_field_never_matches()
    {
        var schema = new MetadataSchema().Add<string[]>("groups");
        var values = new Dictionary<string, object?> { ["groups"] = null };
        Assert.False(RagFilterEvaluator.Matches(RagFilter.ContainsAny("groups", ["hr"]), schema, "d", values));
    }

    [Fact]
    public void Evaluator_matches_like_the_translator()
    {
        var schema = new MetadataSchema().Add<long>("n").Add<string[]>("groups").Add<string>("kind");
        var v = new Dictionary<string, object?> { ["n"] = 5L, ["groups"] = new[] { "hr" }, ["kind"] = "memo" };
        Assert.True(RagFilterEvaluator.Matches(RagFilter.Eq("n", 5) & RagFilter.Contains("groups", "hr"), schema, "d", v));
        Assert.True(RagFilterEvaluator.Matches(RagFilter.Eq("DocumentId", "d"), schema, "d", v));
        Assert.False(RagFilterEvaluator.Matches(RagFilter.In("kind", []), schema, "d", v));
        Assert.Throws<ArgumentException>(() => RagFilterEvaluator.Matches(RagFilter.Eq("n", 5.5), schema, "d", v));
    }

    [Fact]
    public void Evaluator_handles_or_null_values_missing_fields_and_ordinal_contains()
    {
        var schema = new MetadataSchema().Add<string>("kind").Add<string[]>("groups").Add<int>("year");
        var v = new Dictionary<string, object?> { ["kind"] = null, ["groups"] = new[] { "HR" } };
        Assert.True(RagFilterEvaluator.Matches(null, schema, "d", v));
        Assert.False(RagFilterEvaluator.Matches(RagFilter.Eq("kind", "memo"), schema, "d", v));
        Assert.False(RagFilterEvaluator.Matches(RagFilter.In("year", [2024]), schema, "d", v));
        Assert.False(RagFilterEvaluator.Matches(RagFilter.Contains("groups", "hr"), schema, "d", v));
        Assert.True(RagFilterEvaluator.Matches(RagFilter.Or(RagFilter.Eq("kind", "memo"), RagFilter.ContainsAny("groups", ["x", "HR"])), schema, "d", v));
        Assert.False(RagFilterEvaluator.Matches(RagFilter.Eq("DocumentId", "other"), schema, "d", v));
        Assert.Throws<ArgumentException>(() => RagFilterEvaluator.Matches(RagFilter.Eq("unknown", "x"), schema, "d", v));
    }
}
