using Microsoft.Extensions.VectorData;
using Qdrant.Client;
using CommunityToolkit.VectorData.Qdrant;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>Qdrant through CommunityToolkit.VectorData.Qdrant, on the run's shared container (gRPC).</summary>
public sealed class QdrantFixture : StoreFixture
{
    private string? _endpoint;

    public override string Name => "Qdrant";

    // QdrantCollection implements IKeywordHybridSearchable (Qdrant's full-text index with fusion).
    public override bool NativeHybrid => true;

    public override async ValueTask InitializeAsync()
    {
        (_endpoint, UnavailableReason) = await Containers.Qdrant.GetAsync();
    }

    public override VectorStore CreateStore() =>
        new QdrantVectorStore(new QdrantClient(new Uri(_endpoint ?? throw new InvalidOperationException("The Qdrant container is not available."))), ownsClient: true);
}

// No String-key binding: Qdrant point ids are ulong or Guid, so Qdrant needs RagOptions.KeyType = RagKeyType.Guid (spec §2.2).
public sealed class QdrantGuidKeyTests(QdrantFixture fixture) : StoreSuite<QdrantFixture>(fixture, RagKeyType.Guid);

/// <summary>What a Qdrant user who forgets the Guid key setting sees.</summary>
public sealed class QdrantSettingsTests(QdrantFixture fixture) : IClassFixture<QdrantFixture>
{
    [Fact]
    public async Task String_keys_fail_naming_the_setting()
    {
        StoreGate.Check(fixture.Name, fixture.UnavailableReason, StoreGate.Required);
        using var store = fixture.CreateStore();
        var collection = "rag" + Guid.NewGuid().ToString("N")[..12];

        var e = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var rag = new RagPipelineBuilder()
                .UseEmbeddingGenerator(new FakeEmbeddingGenerator(16))
                .UseChatClient(new FakeChatClient())
                .UseVectorStore(store, collection, 16)
                .Configure(o => o.KeyType = RagKeyType.String)
                .Build();
            await rag.RetrieveAsync("alpha", cancellationToken: TestContext.Current.CancellationToken);
        });

        Assert.Contains("RagOptions.KeyType", e.Message);
        Assert.Contains("docs/vector-stores.md", e.Message);
        Assert.False(await store.CollectionExistsAsync(collection, TestContext.Current.CancellationToken));
    }
}
