using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Filtering;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;

namespace RagDotNet.StoreTests.Suite;

/// <summary>
/// The shared store suite: what RagDotNet relies on from a vector store, driven through the public API. Each store binds it
/// to a fixture and a key type in a sealed class under Stores/. A fresh store instance and collection name per test keep
/// tests apart.
/// </summary>
public abstract partial class StoreSuite<TFixture> : IClassFixture<TFixture>, IAsyncDisposable
    where TFixture : StoreFixture
{
    protected const int Dimensions = 64;

    protected StoreSuite(TFixture fixture, RagKeyType keyType)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        StoreGate.Check(fixture.Name, fixture.UnavailableReason, StoreGate.Required);
        Fixture = fixture;
        KeyType = keyType;
        Store = fixture.CreateStore();
        // Letters and digits only, starting with a letter: valid in every supported store, also with the "_documents" suffix.
        Collection = "rag" + Guid.NewGuid().ToString("N")[..12];
    }

    protected TFixture Fixture { get; }

    protected RagKeyType KeyType { get; }

    protected VectorStore Store { get; }

    protected string Collection { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected RagPipelineBuilder Builder(Action<RagOptions>? configure = null, IEmbeddingGenerator<string, Embedding<float>>? embedder = null) =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder ?? new FakeEmbeddingGenerator(Dimensions))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(Store, Collection, Dimensions)
            .Configure(o =>
            {
                o.KeyType = KeyType;
                o.ModelCheckInterval = TimeSpan.Zero;
                Fixture.Configure(o);
                configure?.Invoke(o);
            });

    /// <summary>Deletes this test's two collections and disposes its store, so a shared container doesn't fill up.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Store.EnsureCollectionDeletedAsync(Collection, CancellationToken.None);
            await Store.EnsureCollectionDeletedAsync(Collection + "_documents", CancellationToken.None);
        }
        finally
        {
            Store.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected static DocumentSource Md(string id, string markdown, IDictionary<string, object?>? metadata = null)
    {
        var source = new DocumentSource(id, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown)), id + ".md", "text/markdown");
        if (metadata is not null)
            foreach (var (k, v) in metadata) source.Metadata[k] = v;
        return source;
    }

    /// <summary>
    /// The number of chunks the store holds for a document, read back through a filtered vector-only retrieve. Retrieval
    /// returns at most 100, so a test document must stay below that; reaching it fails instead of undercounting.
    /// </summary>
    protected static async Task<int> CountChunksAsync(IRagPipeline rag, string documentId)
    {
        const int max = 100;
        var hits = await rag.RetrieveAsync(
            "count",
            new RetrievalOptions { TopK = max, Hybrid = HybridMode.Off, Filter = RagFilter.Eq("DocumentId", documentId) },
            Ct);
        var count = hits.Select(h => h.ChunkKey).Distinct().Count();
        Assert.True(count < max, $"'{documentId}' has {max} or more chunks; CountChunksAsync can only count fewer than {max}.");
        return count;
    }

    [Fact]
    public async Task Ingest_then_retrieve_finds_the_document()
    {
        var rag = Builder().Build();
        var r = await rag.IngestAsync(Md("d1", "alpha beta gamma"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, r.Status);
        Assert.Equal("d1", Assert.Single(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct)).DocumentId);
    }
}
