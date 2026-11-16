using RagDotNet.Access;
using RagDotNet.Filtering;

namespace RagDotNet.Core.Tests.Hybrid;

public class KeywordFilterBuildTests
{
    private static readonly MetadataSchema Schema = CreateSchema();

    private static MetadataSchema CreateSchema()
    {
        var s = new MetadataSchema();
        s.Add<string>("tenant");
        s.Add<string>("category");
        s.Add<int>("year");
        s.Add<string[]>("tags");
        return s;
    }

    private static ResolvedScope Tenant(string tenant, RagFilter? userFilter = null) =>
        new(new Dictionary<string, string> { ["tenant"] = tenant }, "tenant=" + tenant, userFilter, RagPermissions.All, false);

    [Fact]
    public void Scoped_tenant_with_common_allows_own_partition_and_common()
    {
        var f = KeywordFilters.Build(Tenant("a"), includeCommon: true, partitioned: true, null, Schema, out var nothing)!;

        Assert.False(nothing);
        Assert.Equal(["tenant=a", ""], f.Partitions);
        Assert.False(f.ExcludeCommon);
        Assert.Null(f.Metadata);
    }

    [Fact]
    public void Scoped_tenant_without_common_allows_own_partition_only()
    {
        var f = KeywordFilters.Build(Tenant("a"), includeCommon: false, partitioned: true, null, Schema, out _)!;

        Assert.Equal(["tenant=a"], f.Partitions);
    }

    [Fact]
    public void Single_tenant_scoped_app_allows_the_common_area()
    {
        var scope = new ResolvedScope(new Dictionary<string, string>(), "", RagFilter.Eq("category", "x"), RagPermissions.Read, false);

        var f = KeywordFilters.Build(scope, includeCommon: true, partitioned: false, null, Schema, out _)!;

        Assert.Equal([""], f.Partitions);
        Assert.Equal(RagFilter.Eq("category", "x"), f.Metadata);
    }

    [Fact]
    public void Full_access_has_no_partition_constraint()
    {
        var f = KeywordFilters.Build(ResolvedScope.FullAccess, includeCommon: true, partitioned: true, null, Schema, out _)!;

        Assert.Null(f.Partitions);
        Assert.False(f.ExcludeCommon);
    }

    [Fact]
    public void Full_access_without_common_excludes_the_common_area()
    {
        var f = KeywordFilters.Build(ResolvedScope.FullAccess, includeCommon: false, partitioned: true, null, Schema, out _)!;

        Assert.Null(f.Partitions);
        Assert.True(f.ExcludeCommon);
    }

    [Fact]
    public void Not_partitioned_ignores_includeCommon()
    {
        var f = KeywordFilters.Build(ResolvedScope.FullAccess, includeCommon: false, partitioned: false, null, Schema, out _)!;

        Assert.False(f.ExcludeCommon);
    }

    [Fact]
    public void User_and_caller_filters_are_combined()
    {
        var f = KeywordFilters.Build(Tenant("a", RagFilter.Eq("category", "x")), true, true, RagFilter.Contains("tags", "red"), Schema, out _)!;

        var and = Assert.IsType<RagFilter.AndFilter>(f.Metadata);
        Assert.Equal(2, and.Filters.Count);
    }

    [Fact]
    public void A_filter_that_never_matches_sets_matchesNothing()
    {
        var f = KeywordFilters.Build(ResolvedScope.FullAccess, true, false, RagFilter.In("category", []), Schema, out var nothing);

        Assert.True(nothing);
        Assert.Null(f);
    }

    [Fact]
    public void Values_are_converted_to_the_declared_type()
    {
        var f = KeywordFilters.Build(ResolvedScope.FullAccess, true, false, RagFilter.Eq("year", 2024L) | RagFilter.In("year", [2020.0, (short)2021]), Schema, out _)!;

        var or = Assert.IsType<RagFilter.OrFilter>(f.Metadata);
        var eq = Assert.IsType<RagFilter.EqFilter>(or.Filters[0]);
        Assert.Equal(2024, Assert.IsType<int>(eq.Value));
        var @in = Assert.IsType<RagFilter.InFilter>(or.Filters[1]);
        Assert.All(@in.Values, v => Assert.IsType<int>(v));
    }

    [Fact]
    public void Inexact_values_throw_as_the_translator_does()
    {
        Assert.Throws<ArgumentException>(() => KeywordFilters.Build(ResolvedScope.FullAccess, true, false, RagFilter.Eq("year", 2024.5), Schema, out _));
        Assert.Throws<ArgumentException>(() => KeywordFilters.Build(ResolvedScope.FullAccess, true, false, RagFilter.Eq("unknown", "x"), Schema, out _));
    }
}
