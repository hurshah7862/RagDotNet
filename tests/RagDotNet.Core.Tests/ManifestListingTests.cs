using System.Linq.Expressions;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Storage;

namespace RagDotNet.Core.Tests;

/// <summary>
/// The document list pages by a stable numeric <c>ListOrder</c>, not the key: some stores (Qdrant) cannot order by the
/// record key, and every supported store can order by an indexed long.
/// </summary>
public sealed class ManifestListingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagOptions Options(RagKeyType keyType) =>
        new() { CollectionName = "test", EmbeddingDimensions = 64, KeyType = keyType };

    [Fact]
    public void List_order_is_stable_and_spreads_keys()
    {
        Assert.Equal(ListOrder.Of("a"), ListOrder.Of("a"));
        Assert.Equal(10_000, Enumerable.Range(0, 10_000).Select(i => ListOrder.Of("|doc" + i)).Distinct().Count());
        // Exact in a double: some stores (Qdrant) compare numeric payloads as doubles in range filters.
        Assert.All(Enumerable.Range(0, 10_000).Select(i => ListOrder.Of("|doc" + i)), v => Assert.InRange(v, 0L, (1L << 53) - 1));
        Assert.Equal(2375299548608914L, ListOrder.Of("tenant=a|doc1"));
    }

    [Theory]
    [InlineData(RagKeyType.String)]
    [InlineData(RagKeyType.Guid)]
    public async Task Manifest_records_carry_their_list_order(RagKeyType keyType)
    {
        var (rag, _, _, _, store) = TestRag.Create(o => o.KeyType = keyType);
        await rag.IngestAsync(TestRag.Md("doc1", "alpha text"), cancellationToken: Ct);

        var manifests = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(Options(keyType)));
        var record = Assert.Single(await manifests.GetAsync(r => true, 10, cancellationToken: Ct).ToListAsync(Ct));

        Assert.Equal(ListOrder.Of(ChunkKeys.Document("", "doc1")), record["ListOrder"]);
    }

    [Fact]
    public async Task Listing_pages_by_list_order()
    {
        var store = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, builder, _, _, _) = TestRag.Create(builder: b => b.UseVectorStore(store, "test", 64));
        await rag.IngestAsync(TestRag.Md("doc1", "alpha text"), cancellationToken: Ct);
        store.LastManifestQuery = null;

        await builder.BuildAdmin().GetStatsAsync(Ct);

        var query = Assert.IsType<FilteredRecordRetrievalOptions<Dictionary<string, object?>>>(store.LastManifestQuery);
        var definition = new FilteredRecordRetrievalOptions<Dictionary<string, object?>>.OrderByDefinition();
        query.OrderBy!(definition);
        var sort = Assert.Single(definition.Values);
        Assert.True(sort.Ascending);
        var reads = new FieldNames();
        reads.Visit(sort.PropertySelector);
        Assert.Equal(["ListOrder"], reads.Names);
    }

    [Fact]
    public async Task Listing_pages_from_the_last_list_order_without_skipping()
    {
        // Some stores (Qdrant) emulate Skip by fetching Skip + top records, so a Skip-paged listing costs O(N^2).
        var store = new FailingVectorStore(new InMemoryVectorStore());
        var (rag, builder, _, _, _) = TestRag.Create(builder: b => b.UseVectorStore(store, "test", 64));
        await rag.IngestManyAsync(Enumerable.Range(0, 250).Select(i => TestRag.Md($"d{i:000}", "text " + i)).ToList(), cancellationToken: Ct);
        store.ManifestQueries.Clear();

        var stats = await builder.BuildAdmin().GetStatsAsync(Ct);

        Assert.Equal(250, Assert.Single(stats.Partitions).DocumentCount);
        var pages = store.ManifestQueries.Where(q => q.Options?.OrderBy is not null).ToList();
        Assert.Equal(3, pages.Count);
        // Skip counts only the records already listed at the previous page's last ListOrder (one, without ties), never the pages before.
        Assert.Equal([0, 1, 1], pages.Select(q => q.Options!.Skip));
        var later = new FieldNames();
        later.Visit(pages[1].Filter);
        Assert.Contains("ListOrder", later.Names);
    }

    [Fact]
    public async Task Equal_list_orders_across_a_page_boundary_are_listed_once()
    {
        var (rag, builder, _, _, store) = TestRag.Create();
        await rag.IngestAsync(TestRag.Md("seed", "alpha text"), cancellationToken: Ct);
        var manifests = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(Options(RagKeyType.String)));
        var seed = Assert.Single(await manifests.GetAsync(r => true, 10, cancellationToken: Ct).ToListAsync(Ct));
        for (var i = 0; i < 150; i++)
        {
            var copy = new Dictionary<string, object?>(seed)
            {
                ["Key"] = ChunkKeys.Document("", $"tie{i:000}"),
                ["DocumentId"] = $"tie{i:000}",
                ["ListOrder"] = 42L, // 150 documents sharing one value: more than a page
            };
            await manifests.UpsertAsync(copy, Ct);
        }

        var stats = await builder.BuildAdmin().GetStatsAsync(Ct);

        Assert.Equal(151, Assert.Single(stats.Partitions).DocumentCount);
    }

    [Fact]
    public async Task Records_without_list_order_fail_naming_the_fix()
    {
        var (rag, builder, _, _, store) = TestRag.Create();
        await rag.IngestAsync(TestRag.Md("doc1", "alpha text"), cancellationToken: Ct);
        var manifests = store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(Options(RagKeyType.String)));
        var old = Assert.Single(await manifests.GetAsync(r => true, 10, cancellationToken: Ct).ToListAsync(Ct));
        old["ListOrder"] = null; // as an earlier build left it on a store that added the column (SQLite)
        await manifests.UpsertAsync(old, Ct);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAdmin().GetStatsAsync(Ct));

        Assert.Contains("ListOrder", e.Message);
        Assert.Contains("ingest", e.Message);
        Assert.Contains("docs/vector-stores.md", e.Message);
    }

    private sealed class FieldNames : ExpressionVisitor
    {
        public List<string> Names { get; } = [];

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "get_Item" && node.Arguments is [ConstantExpression { Value: string name }]) Names.Add(name);
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitIndex(IndexExpression node)
        {
            if (node.Arguments is [ConstantExpression { Value: string name }]) Names.Add(name);
            return base.VisitIndex(node);
        }
    }
}
