using CommunityToolkit.VectorData.SqlServer;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>SQL Server 2025 through CommunityToolkit.VectorData.SqlServer, on the run's shared container (with Full-Text Search).</summary>
public sealed class SqlServerFixture : StoreFixture
{
    private string? _connectionString;

    public override string Name => "SqlServer";

    // SqlServerCollection implements IKeywordHybridSearchable (FREETEXTTABLE over the full-text index, fused with the vector search).
    public override bool NativeHybrid => true;

    public override async ValueTask InitializeAsync()
    {
        (_connectionString, UnavailableReason) = await Containers.SqlServer.GetAsync();
    }

    // SQL Server supports CosineDistance, not RagDotNet's default CosineSimilarity (docs/vector-stores.md).
    public override void Configure(RagOptions options) => options.DistanceFunction = DistanceFunction.CosineDistance;

    public override VectorStore CreateStore() =>
        new SqlServerVectorStore(_connectionString ?? throw new InvalidOperationException("The SQL Server container is not available."));
}

// No String-key binding: the connector stores string keys as NVARCHAR(4000), which SQL Server refuses as the key of the
// chunk text's full-text index (900 bytes at most), so SQL Server needs RagOptions.KeyType = RagKeyType.Guid (spec §2.2).
public sealed class SqlServerGuidKeyTests(SqlServerFixture fixture) : StoreSuite<SqlServerFixture>(fixture, RagKeyType.Guid);

/// <summary>What a SQL Server user who misses one of its two settings sees: an error naming it, and nothing left behind.</summary>
public sealed class SqlServerSettingsTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(InvalidOperationException Error, string Collection, VectorStore Store)> IngestWith(RagKeyType keyType, string distance)
    {
        StoreGate.Check(fixture.Name, fixture.UnavailableReason, StoreGate.Required);
        var store = fixture.CreateStore();
        var collection = "rag" + Guid.NewGuid().ToString("N")[..12];
        var rag = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(16))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, collection, 16)
            .Configure(o =>
            {
                o.KeyType = keyType;
                o.DistanceFunction = distance;
            })
            .Build();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(new DocumentSource("d", new MemoryStream("alpha bravo"u8.ToArray()), "d.md", "text/markdown"), cancellationToken: Ct));
        return (e, collection, store);
    }

    [Fact]
    public async Task String_keys_fail_naming_the_setting()
    {
        var (e, collection, store) = await IngestWith(RagKeyType.String, DistanceFunction.CosineDistance);
        using var _ = store;

        Assert.StartsWith($"The vector store rejected RagDotNet's '{collection}' collection (key type String", e.Message);
        Assert.Contains("RagOptions.KeyType", e.Message);
        Assert.Contains("docs/vector-stores.md", e.Message);
        Assert.False(await store.CollectionExistsAsync(collection, Ct));
        Assert.False(await store.CollectionExistsAsync(collection + "_documents", Ct));
    }

    [Fact]
    public async Task Cosine_similarity_fails_naming_the_setting()
    {
        var (e, collection, store) = await IngestWith(RagKeyType.Guid, DistanceFunction.CosineSimilarity);
        using var _ = store;

        Assert.StartsWith($"The vector store rejected RagDotNet's '{collection}_documents' collection (key type Guid, distance CosineSimilarity", e.Message);
        Assert.Contains("RagOptions.DistanceFunction", e.Message);
        Assert.False(await store.CollectionExistsAsync(collection, Ct));
        Assert.False(await store.CollectionExistsAsync(collection + "_documents", Ct));
    }
}
