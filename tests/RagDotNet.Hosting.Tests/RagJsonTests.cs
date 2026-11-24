using System.Text.Json;
using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Filtering;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Hosting.Tests;

public sealed class RagJsonTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset When = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(5)).AddTicks(1234567);

    private static RagFilter Everything() => RagFilter.And(
        RagFilter.Eq("s", "x"),
        RagFilter.Or(RagFilter.Eq("i", 5), RagFilter.Eq("l", 5L)),
        RagFilter.In("mixed", new object[] { true, When, "y" }),
        RagFilter.Contains("tags", "a"),
        RagFilter.ContainsAny("tags", ["b", "c"]));

    [Fact]
    public void Every_filter_round_trips()
    {
        var json = JsonSerializer.Serialize(Everything(), RagJson.Options);

        var restored = JsonSerializer.Deserialize<RagFilter>(json, RagJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(restored, RagJson.Options));
        var and = Assert.IsType<RagFilter.AndFilter>(restored);
        Assert.Equal("x", Assert.IsType<RagFilter.EqFilter>(and.Filters[0]).Value);
        var or = Assert.IsType<RagFilter.OrFilter>(and.Filters[1]);
        Assert.IsType<int>(Assert.IsType<RagFilter.EqFilter>(or.Filters[0]).Value);
        Assert.IsType<long>(Assert.IsType<RagFilter.EqFilter>(or.Filters[1]).Value);
        var @in = Assert.IsType<RagFilter.InFilter>(and.Filters[2]);
        Assert.Equal(true, @in.Values[0]);
        var date = Assert.IsType<DateTimeOffset>(@in.Values[1]);
        Assert.Equal(When, date);
        Assert.Equal(TimeSpan.FromHours(5), date.Offset);
        Assert.Equal("a", Assert.IsType<RagFilter.ContainsFilter>(and.Filters[3]).Value);
        Assert.Equal(["b", "c"], Assert.IsType<RagFilter.ContainsAnyFilter>(and.Filters[4]).Values);
        Assert.Contains("\"op\":\"containsAny\"", json);
        Assert.Contains("{\"long\":5}", json);
    }

    [Fact]
    public void Scope_round_trips()
    {
        var scope = new AccessScope(new Dictionary<string, string> { ["tenant"] = "a" }, RagFilter.Eq("tag", "x"), RagPermissions.Read | RagPermissions.Write);

        var restored = JsonSerializer.Deserialize<AccessScope>(JsonSerializer.Serialize(scope, RagJson.Options), RagJson.Options)!;

        Assert.Equal("a", Assert.Single(restored.Partition).Value);
        Assert.Equal(scope.Filter, restored.Filter);
        Assert.Equal(RagPermissions.Read | RagPermissions.Write, restored.Permissions);

        var bare = JsonSerializer.Deserialize<AccessScope>(JsonSerializer.Serialize(AccessScope.Unpartitioned(), RagJson.Options), RagJson.Options)!;
        Assert.Empty(bare.Partition);
        Assert.Null(bare.Filter);
        Assert.Equal(RagPermissions.All, bare.Permissions);
    }

    [Fact]
    public async Task Restored_scope_still_restricts_reads()
    {
        var admin = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(new InMemoryVectorStore(), "test", 64)
            .Configure(o =>
            {
                o.Metadata.Add<string>("tenant");
                o.Metadata.Add<string>("tag");
                o.PartitionKeys.Add("tenant");
            })
            .BuildAdmin();
        var tenantA = admin.ForScope(TestScopes.Tenant("a"));
        await tenantA.IngestAsync(Md("a1", "alpha beta", new Dictionary<string, object?> { ["tag"] = "x" }), cancellationToken: Ct);
        await tenantA.IngestAsync(Md("a2", "alpha gamma", new Dictionary<string, object?> { ["tag"] = "y" }), cancellationToken: Ct);

        var json = JsonSerializer.Serialize(TestScopes.Tenant("a", RagFilter.Eq("tag", "x")), RagJson.Options);
        var restored = JsonSerializer.Deserialize<AccessScope>(json, RagJson.Options)!;

        var hits = await admin.ForScope(restored).RetrieveAsync("alpha", cancellationToken: Ct);
        Assert.Equal("a1", Assert.Single(hits).DocumentId);
    }

    [Fact]
    public void Unsupported_value_type_is_refused()
    {
        var e = Assert.Throws<JsonException>(() => JsonSerializer.Serialize(RagFilter.Eq("f", 1.5), RagJson.Options));
        Assert.Contains("Double", e.Message);
    }

    [Fact]
    public void Malformed_json_is_refused()
    {
        var e = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RagFilter>("""{"op":"near","field":"f"}""", RagJson.Options));
        Assert.Contains("near", e.Message);
    }
}
