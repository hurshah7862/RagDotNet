using RagDotNet.Configuration;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using F = RagDotNet.Filtering.RagFilter;

namespace RagDotNet.StoreTests.Suite;

public abstract partial class StoreSuite<TFixture>
{
    // Non-zero offsets and milliseconds: a store that keeps the wall-clock time but drops or misreads the offset
    // shifts the instant, which a UTC value on a UTC CI runner would never show.
    private static readonly DateTimeOffset FullDate = new(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5));
    private static readonly DateTimeOffset OtherDate = new(2026, 6, 1, 8, 0, 0, 456, TimeSpan.FromHours(-7));

    /// <summary>Every case: its filter and the documents it must return. "bare" has no metadata and must never match.</summary>
    private static readonly Dictionary<string, (F Filter, string[] Expected)> FilterCases = new()
    {
        ["eq-string"] = (F.Eq("s", "x"), ["full"]),
        ["eq-int"] = (F.Eq("i", 1), ["full"]),
        ["eq-long"] = (F.Eq("l", 10_000_000_000L), ["full"]),
        ["eq-bool-true"] = (F.Eq("b", true), ["full"]),
        ["eq-bool-false"] = (F.Eq("b", false), ["other"]),
        ["eq-date"] = (F.Eq("d", FullDate), ["full"]),
        ["eq-documentid"] = (F.Eq("DocumentId", "other"), ["other"]),
        ["in-string"] = (F.In("s", ["x", "y"]), ["full", "other"]),
        ["in-int"] = (F.In("i", [1, 2]), ["full", "other"]),
        ["in-long"] = (F.In("l", [20_000_000_000L]), ["other"]),
        ["in-bool"] = (F.In("b", [false]), ["other"]),
        ["in-date"] = (F.In("d", [FullDate, OtherDate]), ["full", "other"]),
        ["contains"] = (F.Contains("tags", "red"), ["full"]),
        ["contains-any"] = (F.ContainsAny("tags", ["green", "blue"]), ["full", "other"]),
        ["contains-none"] = (F.Contains("tags", "purple"), []),
        ["and"] = (F.Eq("s", "x") & F.Eq("i", 1), ["full"]),
        ["and-disjoint"] = (F.Eq("s", "x") & F.Eq("i", 2), []),
        ["or"] = (F.Eq("s", "x") | F.Eq("i", 2), ["full", "other"]),
        ["nested"] = (F.Or(F.And(F.Eq("b", true), F.Contains("tags", "red")), F.Eq("l", 20_000_000_000L)), ["full", "other"]),
    };

    public static TheoryData<string> FilterCaseNames => new(FilterCases.Keys);

    private static void FilterSchema(RagOptions o) =>
        o.Metadata.Add<string>("s").Add<int>("i").Add<long>("l").Add<bool>("b").Add<DateTimeOffset>("d").Add<string[]>("tags");

    private async Task<IRagPipeline> FilterCorpusAsync()
    {
        var rag = Builder(FilterSchema).Build();
        await rag.IngestAsync(Md("full", "common full", new Dictionary<string, object?>
        {
            ["s"] = "x", ["i"] = 1, ["l"] = 10_000_000_000L, ["b"] = true, ["d"] = FullDate, ["tags"] = new[] { "red", "green" },
        }), cancellationToken: Ct);
        await rag.IngestAsync(Md("other", "common other", new Dictionary<string, object?>
        {
            ["s"] = "y", ["i"] = 2, ["l"] = 20_000_000_000L, ["b"] = false, ["d"] = OtherDate, ["tags"] = new[] { "blue" },
        }), cancellationToken: Ct);
        await rag.IngestAsync(Md("bare", "common bare"), cancellationToken: Ct);
        return rag;
    }

    [Theory]
    [MemberData(nameof(FilterCaseNames))]
    public async Task Filter_matches_exactly(string name)
    {
        var (filter, expected) = FilterCases[name];
        var rag = await FilterCorpusAsync();

        var hits = await rag.RetrieveAsync("common", new RetrievalOptions { TopK = 10, Hybrid = HybridMode.Off, Filter = filter }, Ct);

        Assert.Equal(expected.Order(), hits.Select(h => h.DocumentId).Distinct().Order());
    }

    [Fact]
    public async Task Metadata_round_trips_with_its_type()
    {
        var rag = await FilterCorpusAsync();

        var hit = Assert.Single(await rag.RetrieveAsync("common", new RetrievalOptions { Hybrid = HybridMode.Off, Filter = F.Eq("DocumentId", "full") }, Ct));

        Assert.Equal("x", Assert.IsType<string>(hit.Metadata["s"]));
        Assert.Equal(1, Assert.IsType<int>(hit.Metadata["i"]));
        Assert.Equal(10_000_000_000L, Assert.IsType<long>(hit.Metadata["l"]));
        Assert.True(Assert.IsType<bool>(hit.Metadata["b"]));
        Assert.Equal(FullDate, Assert.IsType<DateTimeOffset>(hit.Metadata["d"]));
        Assert.Equal(["red", "green"], Assert.IsType<string[]>(hit.Metadata["tags"]));
    }
}
