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

    private static AccessScope Tenant(string t, RagFilter? filter = null) => TestScopes.Tenant(t, filter);

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

    private static IDictionary<string, object?> Groups(params string[] g) => new Dictionary<string, object?> { ["groups"] = g };

    [Fact]
    public async Task Delete_with_a_scope_filter_that_excludes_the_document_returns_false()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha beta", Groups("g1")), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["g2"]));
        Assert.False(await rag.DeleteDocumentAsync("d1", Ct));

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["g1"]));
        Assert.Single(await rag.RetrieveAsync("alpha beta", cancellationToken: Ct));
    }

    [Fact]
    public async Task Delete_with_a_no_groups_scope_touches_nothing()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha beta", Groups("g1")), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", []));
        Assert.False(await rag.DeleteDocumentAsync("d1", Ct));

        scope.Current = Tenant("a");
        Assert.Single(await rag.RetrieveAsync("alpha beta", cancellationToken: Ct));
    }

    [Fact]
    public async Task Delete_with_a_matching_scope_filter_deletes()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha beta", Groups("g1")), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["g1"]));
        Assert.True(await rag.DeleteDocumentAsync("d1", Ct));

        scope.Current = Tenant("a");
        Assert.Empty(await rag.RetrieveAsync("alpha beta", cancellationToken: Ct));
    }

    [Fact]
    public async Task Caller_filter_cannot_widen_the_partition()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d1", "alpha beta"), cancellationToken: Ct);
        scope.Current = Tenant("b");
        await rag.IngestAsync(Md("d1", "alpha beta"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        Assert.Empty(await rag.RetrieveAsync("alpha beta", new Retrieval.RetrievalOptions { Filter = RagFilter.Eq("tenant", "b") }, Ct));
        var widened = await rag.RetrieveAsync(
            "alpha beta", new Retrieval.RetrievalOptions { Filter = RagFilter.Or(RagFilter.Eq("tenant", "b"), RagFilter.Eq("tenant", "a")) }, Ct);
        Assert.Equal("a", Assert.Single(widened).Metadata["tenant"]);
    }

    [Fact]
    public async Task Pipelines_sharing_an_engine_keep_their_own_scopes()
    {
        var scopeA = new MutableScope { Current = Tenant("a") };
        var scopeB = new MutableScope { Current = Tenant("b") };
        var (pa, _, _, _, _) = Create(
            o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); },
            b => b.UseAccessScope(scopeA));
        // The builder is frozen after Build(), so the second pipeline shares the engine directly (as DI does).
        var pb = new RagDotNet.Pipeline.RagPipeline(((RagDotNet.Pipeline.RagPipeline)pa).Engine, scopeB);

        await pa.IngestAsync(Md("d1", "alpha beta"), cancellationToken: Ct);
        await pb.IngestAsync(Md("d1", "zulu yankee"), cancellationToken: Ct);

        Assert.Contains("alpha", Assert.Single(await pa.RetrieveAsync("alpha beta", cancellationToken: Ct)).Text);
        Assert.Contains("zulu", Assert.Single(await pb.RetrieveAsync("alpha beta", cancellationToken: Ct)).Text);
        Assert.Equal(2, scopeA.Calls);
        Assert.Equal(2, scopeB.Calls);
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
        scope.Current = Tenant("");
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));

        scope.Current = Tenant("\uD800");
        await Assert.ThrowsAsync<ArgumentException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));

        scope.Current = new AccessScope(new Dictionary<string, string> { ["tenant"] = null! });
        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
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
    public async Task Partition_keys_without_a_scope_build_and_write_to_the_common_area()
    {
        var (rag, _, _, _, _) = Create(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); });
        await rag.IngestAsync(Md("c1", "alpha beta"), cancellationToken: Ct);
        var hits = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
        Assert.Equal("c1:0", Assert.Single(hits).ChunkKey);
    }

    [Fact]
    public async Task Null_scope_means_full_access_and_sees_every_partition()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("a1", "alpha beta"), cancellationToken: Ct);
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha gamma"), cancellationToken: Ct);
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["a1", "c1"], hits.Select(h => h.DocumentId).Order());
    }

    [Fact]
    public async Task Full_access_ingest_naming_a_tenant_goes_to_that_tenant()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;
        await rag.IngestAsync(Md("t1", "alpha beta", new Dictionary<string, object?> { ["tenant"] = "a" }), cancellationToken: Ct);
        var hits = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
        Assert.Equal("tenant=a|t1:0", Assert.Single(hits).ChunkKey);
    }

    [Fact]
    public async Task Full_access_ingest_with_partial_or_empty_partition_values_throws()
    {
        var scope = new MutableScope { Current = null };
        var (rag, _, _, _, _) = Create(
            o => { o.Metadata.Add<string>("tenant"); o.Metadata.Add<string>("region"); o.PartitionKeys.Add("tenant"); o.PartitionKeys.Add("region"); },
            b => b.UseAccessScope(scope));

        var partial = await Assert.ThrowsAsync<ArgumentException>(
            () => rag.IngestAsync(Md("p1", "alpha", new Dictionary<string, object?> { ["tenant"] = "a" }), cancellationToken: Ct));
        Assert.Contains("region", partial.Message);
        await Assert.ThrowsAsync<ArgumentException>(
            () => rag.IngestAsync(Md("p2", "alpha", new Dictionary<string, object?> { ["tenant"] = "", ["region"] = "eu" }), cancellationToken: Ct));
        Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public void Access_scope_defaults_to_all_permissions() =>
        Assert.Equal(RagPermissions.All, new AccessScope(new Dictionary<string, string> { ["tenant"] = "a" }).Permissions);

    [Fact]
    public async Task Single_tenant_scope_resolves_without_a_partition()
    {
        var scope = new MutableScope { Current = AccessScope.Unpartitioned() };
        var (rag, _, _, _, _) = Create(builder: b => b.UseAccessScope(scope));
        await rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct);
        Assert.Equal("d1:0", Assert.Single(await rag.RetrieveAsync("alpha", cancellationToken: Ct)).ChunkKey);
    }

    [Fact]
    public async Task Single_tenant_scope_with_partition_values_throws()
    {
        var scope = new MutableScope { Current = Tenant("a") };
        var (rag, _, _, _, _) = Create(o => o.Metadata.Add<string>("tenant"), b => b.UseAccessScope(scope));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Contains("Unpartitioned", ex.Message);
    }

    [Fact]
    public async Task Tenant_sees_own_and_common_but_not_other_tenants()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;                     await rag.IngestAsync(Md("c1", "alpha common"), cancellationToken: Ct);
        scope.Current = TestScopes.Tenant("a");   await rag.IngestAsync(Md("a1", "alpha tenant a"), cancellationToken: Ct);
        scope.Current = TestScopes.Tenant("b");   await rag.IngestAsync(Md("b1", "alpha tenant b"), cancellationToken: Ct);

        scope.Current = TestScopes.Tenant("a");
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["a1", "c1"], hits.Select(h => h.DocumentId).Order());

        var own = await rag.RetrieveAsync("alpha", new() { TopK = 10, IncludeCommon = false }, Ct);
        Assert.Equal(["a1"], own.Select(h => h.DocumentId));
    }

    [Fact]
    public async Task Scope_filter_applies_to_the_common_area_too()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha hr", Groups("hr")), cancellationToken: Ct);
        await rag.IngestAsync(Md("c2", "alpha eng", Groups("eng")), cancellationToken: Ct);

        scope.Current = TestScopes.Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["c2"], hits.Select(h => h.DocumentId));
    }

    [Fact]
    public async Task Same_document_id_in_tenant_and_common_both_appear()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;
        await rag.IngestAsync(Md("d", "alpha common"), cancellationToken: Ct);
        scope.Current = TestScopes.Tenant("a");
        await rag.IngestAsync(Md("d", "alpha tenant"), cancellationToken: Ct);

        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("d", h.DocumentId));
        Assert.Equal(["d:0", "tenant=a|d:0"], hits.Select(h => h.ChunkKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Existing_unscoped_data_becomes_the_common_area_when_a_scope_is_turned_on()
    {
        var embedder = new FakeEmbeddingGenerator(64);
        var store = new InMemoryVectorStore();
        var plain = new RagPipelineBuilder().UseEmbeddingGenerator(embedder).UseChatClient(new FakeChatClient()).UseVectorStore(store, "test", 64)
            .Configure(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); }).Build();
        await plain.IngestAsync(Md("old", "alpha old"), cancellationToken: Ct);

        var scope = new MutableScope { Current = Tenant("a") };
        var scoped = new RagPipelineBuilder().UseEmbeddingGenerator(embedder).UseChatClient(new FakeChatClient()).UseVectorStore(store, "test", 64)
            .Configure(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); }).UseAccessScope(scope).Build();
        Assert.Equal(["old"], (await scoped.RetrieveAsync("alpha", cancellationToken: Ct)).Select(h => h.DocumentId));
        Assert.False(await scoped.DeleteDocumentAsync("old", Ct));
    }

    [Fact]
    public async Task Full_access_without_common_sees_only_tenant_partitions()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha common"), cancellationToken: Ct);
        await rag.IngestAsync(Md("a1", "alpha tenant", new Dictionary<string, object?> { ["tenant"] = "a" }), cancellationToken: Ct);

        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10, IncludeCommon = false }, Ct);
        Assert.Equal(["a1"], hits.Select(h => h.DocumentId));

        scope.Current = TestScopes.Tenant("a");
        var tenantHits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Contains("a1", tenantHits.Select(h => h.DocumentId));
    }

    [Fact]
    public async Task Reads_require_the_read_permission()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = TestScopes.Tenant("a", permissions: RagPermissions.Write);
        var ex = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Equal(RagPermissions.Read, ex.MissingPermission);
        await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.AskAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Read_only_user_cannot_ingest_or_delete()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = TestScopes.Tenant("a", permissions: RagPermissions.Read);
        var e1 = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
        Assert.Equal(RagPermissions.Write, e1.MissingPermission);
        Assert.Equal("ingest", e1.Operation);
        var e2 = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.DeleteDocumentAsync("d", Ct));
        Assert.Equal(RagPermissions.Delete, e2.MissingPermission);
        Assert.Equal("delete", e2.Operation);
    }

    [Fact]
    public async Task Single_tenant_read_only_user_cannot_ingest()
    {
        var scope = new MutableScope { Current = AccessScope.Unpartitioned(permissions: RagPermissions.Read) };
        var (rag, _, _, _, _) = Create(builder: b => b.UseAccessScope(scope));
        var ex = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct));
        Assert.Equal(RagPermissions.Write, ex.MissingPermission);

        scope.Current = AccessScope.Unpartitioned();
        await rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct);
        Assert.Equal("d1:0", Assert.Single(await rag.RetrieveAsync("alpha", cancellationToken: Ct)).ChunkKey);
    }

    [Fact]
    public async Task Ingest_over_a_hidden_document_is_refused_and_leaves_it_unchanged()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d", "alpha hr", Groups("hr")), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        var ex = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d", "alpha eng", Groups("eng")), cancellationToken: Ct));
        Assert.Null(ex.MissingPermission);

        scope.Current = Tenant("a");
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal("alpha hr", Assert.Single(hits).Text.Trim());
    }

    [Fact]
    public async Task Ingest_whose_metadata_falls_outside_the_read_filter_is_refused()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        var ex = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("n", "alpha hr", Groups("hr")), cancellationToken: Ct));
        Assert.Null(ex.MissingPermission);

        scope.Current = null;
        Assert.Empty(await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct));
        scope.Current = Tenant("a");
        Assert.False(await rag.DeleteDocumentAsync("n", Ct));
    }

    [Fact]
    public async Task Empty_document_is_visible_through_its_manifest_metadata()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        var empty = await rag.IngestAsync(Md("e", "", Groups("eng")), cancellationToken: Ct);
        Assert.Equal(0, empty.ChunkCount);

        var updated = await rag.IngestAsync(Md("e", "alpha eng", Groups("eng")), cancellationToken: Ct);
        Assert.Equal(Ingestion.IngestStatus.Updated, updated.Status);
        Assert.True(await rag.DeleteDocumentAsync("e", Ct));
    }

    [Fact]
    public async Task Stored_document_with_null_array_field_is_hidden_not_a_crash()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = Tenant("a");
        await rag.IngestAsync(Md("d", "alpha"), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d", "alpha eng", Groups("eng")), cancellationToken: Ct));
        Assert.False(await rag.DeleteDocumentAsync("d", Ct));
    }

    [Fact]
    public async Task Scoped_ingest_of_a_common_document_id_creates_a_separate_tenant_document()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;
        await rag.IngestAsync(Md("d", "alpha common"), cancellationToken: Ct);
        scope.Current = Tenant("a");
        var r = await rag.IngestAsync(Md("d", "alpha tenant"), cancellationToken: Ct);
        Assert.Equal(Ingestion.IngestStatus.Created, r.Status);

        scope.Current = null;
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d:0", "tenant=a|d:0"], hits.Select(h => h.ChunkKey).Order(StringComparer.Ordinal));
        Assert.Equal("alpha common", hits.Single(h => h.ChunkKey == "d:0").Text.Trim());
    }

    [Fact]
    public async Task Single_tenant_user_can_change_what_they_can_read_and_not_what_they_cannot()
    {
        var scope = new MutableScope { Current = null };
        var (rag, _, _, _, _) = Create(o => o.Metadata.Add<string[]>("groups"), b => b.UseAccessScope(scope));
        await rag.IngestAsync(Md("hr", "alpha hr", Groups("hr")), cancellationToken: Ct);

        scope.Current = AccessScope.Unpartitioned(RagFilter.ContainsAny("groups", ["eng"]));
        var created = await rag.IngestAsync(Md("e", "alpha eng", Groups("eng")), cancellationToken: Ct);
        Assert.Equal(Ingestion.IngestStatus.Created, created.Status);
        Assert.True(await rag.DeleteDocumentAsync("e", Ct));
        await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("hr", "alpha eng", Groups("eng")), cancellationToken: Ct));
        Assert.False(await rag.DeleteDocumentAsync("hr", Ct));

        scope.Current = null;
        Assert.Equal("alpha hr", Assert.Single(await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct)).Text.Trim());
    }

    [Fact]
    public async Task Tenant_cannot_delete_a_common_document()
    {
        var (rag, scope) = CreateScoped();
        scope.Current = null;
        await rag.IngestAsync(Md("c", "alpha common"), cancellationToken: Ct);

        scope.Current = Tenant("a");
        Assert.False(await rag.DeleteDocumentAsync("c", Ct));

        scope.Current = null;
        Assert.Equal("c", Assert.Single(await rag.RetrieveAsync("alpha", cancellationToken: Ct)).DocumentId);
    }

    [Fact]
    public async Task IncludeCommon_false_has_no_effect_in_a_single_tenant_app()
    {
        var scope = new MutableScope { Current = null };
        var (rag, _, _, _, _) = Create(builder: b => b.UseAccessScope(scope));
        await rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct);

        Assert.Equal(["d1"], (await rag.RetrieveAsync("alpha", new() { IncludeCommon = false }, Ct)).Select(h => h.DocumentId));
        scope.Current = AccessScope.Unpartitioned();
        Assert.Equal(["d1"], (await rag.RetrieveAsync("alpha", new() { IncludeCommon = false }, Ct)).Select(h => h.DocumentId));
    }

    [Fact]
    public async Task Scope_filter_on_groups_hides_documents_without_groups_instead_of_crashing()
    {
        var (rag, scope) = CreateScoped(o => o.Metadata.Add<string[]>("groups"));
        scope.Current = null;
        await rag.IngestAsync(Md("c1", "alpha beta"), cancellationToken: Ct);
        await rag.IngestAsync(Md("c2", "alpha beta", Groups("eng")), cancellationToken: Ct);

        scope.Current = Tenant("a", RagFilter.ContainsAny("groups", ["eng"]));
        var r = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
        Assert.NotEmpty(r);
        Assert.All(r, c => Assert.Equal("c2", c.DocumentId));
    }
}
