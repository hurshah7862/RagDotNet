using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class AccessScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MutableScope : IAccessScope
    {
        public AccessScope? Current { get; set; }

        public int Calls { get; private set; }

        public ValueTask<AccessScope> GetScopeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<AccessScope>(Current!);
        }
    }

    private static AccessScope Tenant(string t, RagFilter? filter = null) =>
        new(new Dictionary<string, string> { ["tenant"] = t }, filter);

    private static (IRagPipeline Rag, MutableScope Scope) CreateScoped(Action<RagOptions>? configure = null)
    {
        var scope = new MutableScope { Current = Tenant("a") };
        var (rag, _, _, _, _) = Create(
            o =>
            {
                o.Metadata.Add<string>("tenant");
                o.PartitionKeys.Add("tenant");
                configure?.Invoke(o);
            },
            b => b.UseAccessScope(scope));
        return (rag, scope);
    }

    [Fact]
    public async Task Same_document_id_in_two_partitions_is_isolated()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("doc1", "alpha beta gamma"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("doc1", "zulu yankee xray"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        var a = await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);
        scope.Current = Tenant("b");
        var b = await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct);

        Assert.Single(a);
        Assert.Contains("alpha", a[0].Text);
        Assert.Equal("a", a[0].Metadata["tenant"]);
        Assert.Single(b);
        Assert.Contains("zulu", b[0].Text);
        Assert.NotEqual(a[0].ChunkKey, b[0].ChunkKey);

        scope.Current = Tenant("a");
        Assert.True(await rag.DeleteDocumentAsync("doc1", Ct));
        Assert.Empty(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct));
        Assert.False(await rag.DeleteDocumentAsync("doc1", Ct));

        scope.Current = Tenant("b");
        var after = await rag.RetrieveAsync("zulu yankee xray", cancellationToken: Ct);
        Assert.Single(after);
        Assert.Contains("zulu", after[0].Text);
    }

    [Fact]
    public async Task Partition_values_override_caller_metadata()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("doc1", "alpha beta", new Dictionary<string, object?> { ["tenant"] = "b" }), cancellationToken: Ct);

        var inA = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
        Assert.Single(inA);
        Assert.Equal("a", inA[0].Metadata["tenant"]);

        scope.Current = Tenant("b");
        Assert.Empty(await rag.RetrieveAsync("alpha beta", cancellationToken: Ct));
    }

    [Fact]
    public async Task Scope_filter_is_ANDed_with_caller_filter()
    {
        var (rag, scope) = CreateScoped(o => { o.Metadata.Add<string[]>("groups"); o.Metadata.Add<string>("category"); });
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha one", new Dictionary<string, object?> { ["groups"] = new[] { "g1" }, ["category"] = "x" }), cancellationToken: Ct);
        await rag.IngestAsync(Md("d2", "alpha two", new Dictionary<string, object?> { ["groups"] = new[] { "g1" }, ["category"] = "y" }), cancellationToken: Ct);
        await rag.IngestAsync(Md("d3", "alpha three", new Dictionary<string, object?> { ["groups"] = new[] { "g2" }, ["category"] = "x" }), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["g1"]));
        var results = await rag.RetrieveAsync("alpha", new Retrieval.RetrievalOptions { Filter = RagFilter.Eq("category", "x") }, Ct);

        Assert.Equal(["d1"], results.Select(r => r.DocumentId));
    }

    [Fact]
    public async Task User_with_no_groups_sees_nothing()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha", new Dictionary<string, object?> { ["groups"] = new[] { "g1" } }), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", []));

        Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Change_detection_is_partition_scoped()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = Tenant("a");
        var first = await rag.IngestAsync(Md("doc1", "same bytes"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        var second = await rag.IngestAsync(Md("doc1", "same bytes"), cancellationToken: Ct);
        var again = await rag.IngestAsync(Md("doc1", "same bytes"), cancellationToken: Ct);

        Assert.Equal(Ingestion.IngestStatus.Created, first.Status);
        Assert.Equal(Ingestion.IngestStatus.Created, second.Status);
        Assert.Equal(Ingestion.IngestStatus.Unchanged, again.Status);
    }

    [Fact]
    public async Task Empty_partition_throws()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = new AccessScope(new Dictionary<string, string>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.DeleteDocumentAsync("d", Ct));
    }

    [Fact]
    public async Task Partition_keys_not_matching_options_throw()
    {
        var (rag, scope) = CreateScoped();

        scope.Current = new AccessScope(new Dictionary<string, string> { ["org"] = "a" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Contains("org", ex.Message);
        Assert.Contains("tenant", ex.Message);

        scope.Current = new AccessScope(new Dictionary<string, string> { ["tenant"] = "a", ["org"] = "o" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Invalid_partition_values_throw()
    {
        var (rag, scope) = CreateScoped();
        foreach (var bad in new[] { "", "\uD800" })
        {
            scope.Current = Tenant(bad);
            await Assert.ThrowsAnyAsync<Exception>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        }

        scope.Current = new AccessScope(new Dictionary<string, string> { ["tenant"] = null! });
        await Assert.ThrowsAnyAsync<Exception>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Scope_is_resolved_once_per_call()
    {
        var (rag, scope) = CreateScoped();
        await rag.IngestManyAsync([Md("d1", "alpha"), Md("d2", "beta")], cancellationToken: Ct);
        Assert.Equal(1, scope.Calls);
        await rag.RetrieveAsync("alpha", cancellationToken: Ct);
        Assert.Equal(2, scope.Calls);
    }

    [Fact]
    public async Task Enabling_scope_over_unscoped_data_throws_UnscopedDataException()
    {
        var store = new InMemoryVectorStore();
        var embedder = new FakeEmbeddingGenerator(64);
        var plain = new RagPipelineBuilder().UseEmbeddingGenerator(embedder).UseVectorStore(store, "test", 64)
            .Configure(o => o.Metadata.Add<string>("tenant")).Build();
        await plain.IngestAsync(Md("old", "alpha beta"), cancellationToken: Ct);

        var scope = new MutableScope { Current = Tenant("a") };
        var scoped = new RagPipelineBuilder().UseEmbeddingGenerator(embedder).UseVectorStore(store, "test", 64)
            .Configure(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); })
            .UseAccessScope(scope).Build();

        await Assert.ThrowsAsync<UnscopedDataException>(() => scoped.RetrieveAsync("alpha", cancellationToken: Ct));
        // A failed check is not cached: it runs again.
        await Assert.ThrowsAsync<UnscopedDataException>(() => scoped.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        await Assert.ThrowsAsync<UnscopedDataException>(() => scoped.DeleteDocumentAsync("old", Ct));
    }
}
