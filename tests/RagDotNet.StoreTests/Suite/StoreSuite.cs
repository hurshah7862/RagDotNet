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
public abstract partial class StoreSuite<TFixture> : IClassFixture<TFixture>
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
                configure?.Invoke(o);
            });

    protected static DocumentSource Md(string id, string markdown, IDictionary<string, object?>? metadata = null)
    {
        var source = new DocumentSource(id, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown)), id + ".md", "text/markdown");
        if (metadata is not null)
            foreach (var (k, v) in metadata) source.Metadata[k] = v;
        return source;
    }

    /// <summary>The number of chunks the store holds for a document, read back through a filtered vector-only retrieve.</summary>
    protected static async Task<int> CountChunksAsync(IRagPipeline rag, string documentId)
    {
        var hits = await rag.RetrieveAsync(
            "count",
            new RetrievalOptions { TopK = 1000, Hybrid = HybridMode.Off, Filter = RagFilter.Eq("DocumentId", documentId) },
            Ct);
        return hits.Select(h => h.ChunkKey).Distinct().Count();
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
