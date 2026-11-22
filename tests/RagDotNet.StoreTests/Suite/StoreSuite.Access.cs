using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using RagDotNet.StoreTests.Infrastructure;
using static RagDotNet.StoreTests.Infrastructure.MutableScope;

namespace RagDotNet.StoreTests.Suite;

public abstract partial class StoreSuite<TFixture>
{
    /// <summary>A multi-tenant app: partitioned by tenant, with a groups field for scope filters.</summary>
    private (IRagPipeline Rag, IRagAdmin Admin, MutableScope Scope) Scoped()
    {
        var scope = new MutableScope();
        var builder = Builder(o =>
        {
            o.Metadata.Add<string>("tenant").Add<string[]>("groups");
            o.PartitionKeys.Add("tenant");
        }).UseAccessScope(scope);
        return (builder.Build(), builder.BuildAdmin(), scope);
    }

    private static readonly RetrievalOptions Ten = new() { TopK = 10 };

    private static async Task<string[]> IdsAsync(IRagPipeline rag, string query, RetrievalOptions? options = null) =>
        (await rag.RetrieveAsync(query, options ?? Ten, Ct)).Select(h => h.DocumentId).Distinct().Order().ToArray();

    [Fact]
    public async Task Same_document_id_in_two_partitions_is_isolated()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("doc1", "alpha beta gamma"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("doc1", "zulu yankee xray"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        var a = Assert.Single(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct));
        scope.Current = Tenant("b");
        var b = Assert.Single(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct));
        Assert.Contains("alpha", a.Text);
        Assert.Equal("a", a.Metadata["tenant"]);
        Assert.Contains("zulu", b.Text);
        Assert.NotEqual(a.ChunkKey, b.ChunkKey);

        scope.Current = Tenant("a");
        Assert.True(await rag.DeleteDocumentAsync("doc1", Ct));
        Assert.Empty(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct));
        Assert.False(await rag.DeleteDocumentAsync("doc1", Ct));

        scope.Current = Tenant("b");
        Assert.Contains("zulu", Assert.Single(await rag.RetrieveAsync("zulu yankee xray", cancellationToken: Ct)).Text);
    }

    [Fact]
    public async Task Tenant_sees_own_and_common_but_not_other_tenants()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha common"), cancellationToken: Ct);
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("a1", "alpha tenant a"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("b1", "alpha tenant b"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        Assert.Equal(["a1", "c1"], await IdsAsync(rag, "alpha"));
        Assert.Equal(["a1"], await IdsAsync(rag, "alpha", new RetrievalOptions { TopK = 10, IncludeCommon = false }));
    }

    [Fact]
    public async Task Tenant_cannot_delete_a_common_document()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c", "alpha common"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        Assert.False(await rag.DeleteDocumentAsync("c", Ct));

        scope.Current = null;
        Assert.Equal(["c"], await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task Full_access_sees_every_partition()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("a1", "alpha beta"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("b1", "alpha delta"), cancellationToken: Ct);
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha gamma"), cancellationToken: Ct);

        Assert.Equal(["a1", "b1", "c1"], await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task Full_access_without_common_sees_only_tenant_partitions()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha common"), cancellationToken: Ct);
        await rag.IngestAsync(Md("a1", "alpha tenant", new Dictionary<string, object?> { ["tenant"] = "a" }), cancellationToken: Ct);

        Assert.Equal(["a1"], await IdsAsync(rag, "alpha", new RetrievalOptions { TopK = 10, IncludeCommon = false }));

        scope.Current = Tenant("a");
        Assert.Contains("a1", await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task Scope_filter_on_groups_hides_documents_without_groups()
    {
        var (rag, _, scope) = Scoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha beta"), cancellationToken: Ct);
        await rag.IngestAsync(Md("c2", "alpha beta", new Dictionary<string, object?> { ["groups"] = new[] { "g1" } }), cancellationToken: Ct);
        await rag.IngestAsync(Md("c3", "alpha beta", new Dictionary<string, object?> { ["groups"] = new[] { "g2" } }), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["g1"]));

        Assert.Equal(["c2"], await IdsAsync(rag, "alpha beta"));
    }

    [Fact]
    public async Task Admin_delete_names_the_partition()
    {
        var (rag, admin, scope) = Scoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("doc1", "alpha tenant a"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("doc1", "alpha tenant b"), cancellationToken: Ct);

        Assert.True(await admin.DeleteDocumentAsync("doc1", new Dictionary<string, string> { ["tenant"] = "b" }, Ct));

        scope.Current = Tenant("b");
        Assert.Empty(await IdsAsync(rag, "alpha"));
        scope.Current = Tenant("a");
        Assert.Equal(["doc1"], await IdsAsync(rag, "alpha"));
    }

    [Fact]
    public async Task Stats_are_per_partition()
    {
        var (rag, admin, scope) = Scoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("a1", "alpha one"), cancellationToken: Ct);
        await rag.IngestAsync(Md("a2", "alpha two"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("b1", "alpha three"), cancellationToken: Ct);
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha four"), cancellationToken: Ct);

        var stats = await admin.GetStatsAsync(Ct);

        Assert.Equal(3, stats.Partitions.Count);
        static string TenantOf(PartitionStats p) => p.Partition.TryGetValue("tenant", out var t) ? t : "";
        Assert.Equal(2, Assert.Single(stats.Partitions, p => TenantOf(p) == "a").DocumentCount);
        Assert.Equal(1, Assert.Single(stats.Partitions, p => TenantOf(p) == "b").DocumentCount);
        Assert.Equal(1, Assert.Single(stats.Partitions, p => TenantOf(p) == "").DocumentCount);
    }
}
