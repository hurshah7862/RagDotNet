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
        Assert.Equal(4864613475551056814L, ListOrder.Of("tenant=a|doc1"));
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
