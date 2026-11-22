using System.Linq.Expressions;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Filtering;
using RagDotNet.Storage;

namespace RagDotNet.Core.Tests;

/// <summary>
/// What Core hands a vector store must be in the shapes every supported connector translates. The PgVector connector
/// reads record fields only through the indexer method (<c>r["x"]</c> as a C# lambda writes it), not an IndexExpression,
/// and creates NOT NULL columns for non-nullable value types.
/// </summary>
public sealed class StoreShapeTests
{
    private static MetadataSchema Schema() =>
        new MetadataSchema().Add<string>("tenant").Add<int>("i").Add<long>("l").Add<bool>("b").Add<DateTimeOffset>("d").Add<string[]>("tags");

    private static readonly RagFilter Every = RagFilter.And(
        RagFilter.Eq("tenant", "a"), RagFilter.Eq("i", 1), RagFilter.Eq("l", 2L), RagFilter.Eq("b", true),
        RagFilter.Eq("d", DateTimeOffset.UnixEpoch), RagFilter.Contains("tags", "red"), RagFilter.Eq("DocumentId", "doc"));

    private sealed class FieldReads : ExpressionVisitor
    {
        public int Calls { get; private set; }

        protected override Expression VisitIndex(IndexExpression node) =>
            throw new Xunit.Sdk.XunitException($"Filter reads a field through an IndexExpression: {node}");

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "get_Item" && node.Object?.Type == typeof(Dictionary<string, object?>)) Calls++;
            return base.VisitMethodCall(node);
        }
    }

    [Fact]
    public void Filters_read_fields_through_the_indexer_method()
    {
        var translated = RagFilterTranslator.Translate(Every, Schema(), out _)!;
        var reads = new FieldReads();
        reads.Visit(translated);
        Assert.Equal(7, reads.Calls);
    }

    [Fact]
    public void Read_filters_read_partition_and_model_through_the_indexer_method()
    {
        foreach (var includeCommon in new[] { true, false })
        {
            var full = ReadFilters.Build(ResolvedScope.FullAccess, includeCommon, partitioned: true, Every, Schema(), out _, requireModelId: "m")!;
            var reads = new FieldReads();
            reads.Visit(full);
            Assert.Equal(includeCommon ? 8 : 9, reads.Calls); // the filter's 7, the model, and Partition != "" without common
        }

        var tenant = new ResolvedScope(new Dictionary<string, string> { ["tenant"] = "a" }, "tenant=a", null, RagPermissions.All, false);
        var scoped = ReadFilters.Build(tenant, includeCommon: true, partitioned: true, null, Schema(), out _)!;
        var scopedReads = new FieldReads();
        scopedReads.Visit(scoped);
        Assert.Equal(2, scopedReads.Calls); // Partition == own || Partition == ""
    }

    [Fact]
    public void Value_type_metadata_is_nullable_in_store_definitions()
    {
        var o = new RagOptions { CollectionName = "c", EmbeddingDimensions = 4 };
        o.Metadata.Add<string>("s").Add<int>("i").Add<long>("l").Add<bool>("b").Add<DateTimeOffset>("d").Add<string[]>("tags");

        foreach (var definition in new[] { RecordDefinitions.Chunks(o), RecordDefinitions.Manifests(o) })
        {
            Type TypeOf(string field) => definition.Properties.Single(p => p.Name == RecordDefinitions.MetaName(field)).Type!;
            Assert.Equal(typeof(string), TypeOf("s"));
            Assert.Equal(typeof(int?), TypeOf("i"));
            Assert.Equal(typeof(long?), TypeOf("l"));
            Assert.Equal(typeof(bool?), TypeOf("b"));
            Assert.Equal(typeof(DateTimeOffset?), TypeOf("d"));
            Assert.Equal(typeof(string[]), TypeOf("tags"));
        }
    }
}
