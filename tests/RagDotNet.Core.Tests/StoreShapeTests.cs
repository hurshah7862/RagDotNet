using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
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
            Assert.Equal(includeCommon ? 8 : 9, reads.Calls); // the filter's 7, the model, and Partition != common without common
        }

        var tenant = new ResolvedScope(new Dictionary<string, string> { ["tenant"] = "a" }, "tenant=a", null, RagPermissions.All, false);
        var scoped = ReadFilters.Build(tenant, includeCommon: true, partitioned: true, null, Schema(), out _)!;
        var scopedReads = new FieldReads();
        scopedReads.Visit(scoped);
        Assert.Equal(2, scopedReads.Calls); // Partition == own || Partition == common
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
            Assert.Equal(typeof(int?), TypeOf("b")); // bools and dates are stored as numbers: StoredValues
            Assert.Equal(typeof(long?), TypeOf("d"));
            Assert.Equal(typeof(string[]), TypeOf("tags"));
        }
    }

    [Fact]
    public void Filtered_fields_are_indexed()
    {
        // A store may ignore a filter on a field that isn't indexed (Redis returns every record), so every field Core
        // filters on must be indexed.
        var o = new RagOptions { CollectionName = "c", EmbeddingDimensions = 4 };
        o.Metadata.Add<string>("s").Add<int>("i").Add<bool>("b").Add<DateTimeOffset>("d").Add<string[]>("tags");

        static void AssertIndexed(VectorStoreCollectionDefinition definition, IEnumerable<string> fields)
        {
            foreach (var field in fields)
            {
                var property = Assert.IsType<VectorStoreDataProperty>(definition.Properties.Single(p => p.Name == field));
                Assert.True(property.IsIndexed, $"{field} is filtered on but not indexed.");
            }
        }

        string[] meta = ["s", "i", "b", "d", "tags"];
        var metaNames = meta.Select(RecordDefinitions.MetaName);

        // Chunks: DocumentId (delete, document filters), Partition (ReadFilters), EmbeddingModelId (ReadFilters while
        // reindexing), meta_* (RagFilterTranslator).
        AssertIndexed(RecordDefinitions.Chunks(o), ["DocumentId", "Partition", "EmbeddingModelId", .. metaNames]);

        // Manifests: the same, plus ChunkCount (listing, "ChunkCount >= 0" skips the marker), ContentStorage
        // (ManifestStore content-mode check) and ListOrder (keyset paging).
        AssertIndexed(RecordDefinitions.Manifests(o),
            ["DocumentId", "Partition", "EmbeddingModelId", "ChunkCount", "ContentStorage", "ListOrder", .. metaNames]);
    }

    [Fact]
    public async Task Common_area_is_stored_with_a_non_empty_partition()
    {
        // Redis cannot query an empty string in an indexed field, so the common area is stored as StoredPartition.Common.
        var (rag, builder, _, _, store) = Core.Tests.Fakes.TestRag.Create(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); });
        var options = new RagOptions { CollectionName = "test", EmbeddingDimensions = 64 };
        options.Metadata.Add<string>("tenant");
        await rag.IngestAsync(Core.Tests.Fakes.TestRag.Md("c1", "alpha beta"), cancellationToken: TestContext.Current.CancellationToken);

        var chunk = await store.GetDynamicCollection("test", RecordDefinitions.Chunks(options))
            .GetAsync(ChunkKeys.Chunk("", "c1", 0), new RecordRetrievalOptions { IncludeVectors = false }, TestContext.Current.CancellationToken);
        var manifest = await store.GetDynamicCollection("test_documents", RecordDefinitions.Manifests(options))
            .GetAsync(ChunkKeys.Document("", "c1"), new RecordRetrievalOptions { IncludeVectors = false }, TestContext.Current.CancellationToken);
        Assert.Equal("~common", chunk!["Partition"]);
        Assert.Equal("~common", manifest!["Partition"]);

        Assert.Equal("c1:0", Assert.Single(await rag.RetrieveAsync("alpha beta", cancellationToken: TestContext.Current.CancellationToken)).ChunkKey);
        var stats = Assert.Single((await builder.BuildAdmin().GetStatsAsync(TestContext.Current.CancellationToken)).Partitions);
        Assert.Empty(stats.Partition);
        Assert.Equal(1, stats.DocumentCount);
        Assert.True(await rag.DeleteDocumentAsync("c1", TestContext.Current.CancellationToken));
        Assert.Empty(await rag.RetrieveAsync("alpha beta", cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class EmptyStrings : ExpressionVisitor
    {
        public int Count { get; private set; }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is "") Count++;
            return base.VisitConstant(node);
        }
    }

    [Fact]
    public void Read_filters_compare_no_empty_strings()
    {
        var tenant = new ResolvedScope(new Dictionary<string, string> { ["tenant"] = "a" }, "tenant=a", null, RagPermissions.All, false);
        var filters = new[]
        {
            ReadFilters.Build(tenant, includeCommon: true, partitioned: true, null, Schema(), out _)!,
            ReadFilters.Build(tenant, includeCommon: false, partitioned: true, null, Schema(), out _)!,
            ReadFilters.Build(ResolvedScope.FullAccess, includeCommon: false, partitioned: true, null, Schema(), out _)!,
        };
        foreach (var filter in filters)
        {
            var visitor = new EmptyStrings();
            visitor.Visit(filter);
            Assert.Equal(0, visitor.Count);
        }
    }

    [Fact]
    public void Common_area_value_cannot_be_a_partition_key()
    {
        Dictionary<string, string>[] partitions =
        [
            new() { ["tenant"] = "~common" },
            new() { ["~common"] = "" },
            new() { ["~common"] = "~common" },
            new() { ["tenant"] = "" },
            new() { ["a"] = "1", ["b"] = "~common" },
        ];
        foreach (var partition in partitions)
        {
            var key = ChunkKeys.Partition(partition);
            Assert.Contains("=", key);
            Assert.NotEqual(StoredPartition.Common, key);
        }

        Assert.Equal(StoredPartition.Common, StoredPartition.ToStore(""));
        Assert.Equal("", StoredPartition.FromStore(StoredPartition.Common));
        Assert.Equal("tenant=a", StoredPartition.ToStore("tenant=a"));
        Assert.Equal("tenant=a", StoredPartition.FromStore("tenant=a"));
    }
}
