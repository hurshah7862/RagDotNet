using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Pipeline;

namespace RagDotNet.Core.Tests;

/// <summary>
/// A store that rejects RagDotNet's collection definition (Qdrant with string keys, say) gets an error that names the
/// settings to change; a store that is down keeps its own error.
/// </summary>
public sealed class StoreSettingsErrorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IRagPipeline Build(FailingVectorStore store) =>
        new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(16))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "docs", 16)
            .Build();

    private static void AssertNamesTheSettings(Exception original, InvalidOperationException e)
    {
        Assert.Same(original, e.InnerException);
        Assert.Contains("'docs", e.Message);
        Assert.Contains("key type String", e.Message);
        Assert.Contains("16 dimensions", e.Message);
        Assert.Contains("Key properties must be either ulong or Guid", e.Message);
        Assert.Contains("RagOptions.KeyType", e.Message);
        Assert.Contains("docs/vector-stores.md", e.Message);
    }

    [Fact]
    public void Definition_rejection_names_the_settings()
    {
        var rejection = new NotSupportedException("Key properties must be either ulong or Guid.");
        var store = new FailingVectorStore(new InMemoryVectorStore()) { RejectCollection = rejection };

        AssertNamesTheSettings(rejection, Assert.Throws<InvalidOperationException>(() => Build(store)));
    }

    [Fact]
    public async Task Creation_rejection_names_the_settings()
    {
        var rejection = new ArgumentException("Key properties must be either ulong or Guid.");
        var store = new FailingVectorStore(new InMemoryVectorStore());
        var rag = Build(store);
        store.FailEnsureExists = rejection;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct));

        AssertNamesTheSettings(rejection, e);
    }

    [Fact]
    public async Task Connection_errors_are_not_reworded()
    {
        foreach (var failure in new Exception[] { new HttpRequestException("connection refused"), new VectorStoreException("store is down") })
        {
            var store = new FailingVectorStore(new InMemoryVectorStore());
            var rag = Build(store);
            store.FailCollectionExists = failure;

            var e = await Assert.ThrowsAnyAsync<Exception>(() => rag.IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct));

            Assert.Same(failure, e);
        }
    }

    [Fact]
    public async Task Creation_failure_on_a_reachable_store_names_the_settings()
    {
        var failure = new VectorStoreException("Call to vector store failed.", new InvalidOperationException("full-text key"));
        var store = new FailingVectorStore(new InMemoryVectorStore());
        var rag = Build(store);
        store.FailEnsureExists = failure;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct));

        Assert.Same(failure, e.InnerException);
        Assert.StartsWith("The vector store rejected RagDotNet's 'docs' collection (key type String, distance CosineSimilarity", e.Message);
        Assert.Contains("RagOptions.KeyType", e.Message);
        Assert.Contains("RagOptions.DistanceFunction", e.Message);
        Assert.Contains("docs/vector-stores.md", e.Message);
    }

    [Fact]
    public async Task Distance_rejected_by_search_fails_before_any_write()
    {
        var rejection = new NotSupportedException("Distance function CosineSimilarity is not supported.");
        var inner = new InMemoryVectorStore();
        var store = new FailingVectorStore(inner);
        var rag = Build(store);
        store.RejectSearch = rejection;

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct));

        Assert.Same(rejection, e.InnerException);
        Assert.Contains("'docs_documents' collection", e.Message);
        Assert.Contains("RagOptions.DistanceFunction", e.Message);
        Assert.False(await inner.CollectionExistsAsync("docs", Ct));
        Assert.False(await inner.CollectionExistsAsync("docs_documents", Ct));
    }

    [Fact]
    public async Task Rejected_creation_removes_the_collections_it_created()
    {
        var inner = new InMemoryVectorStore();
        var store = new FailingVectorStore(inner);
        var rag = Build(store);
        store.FailEnsureExists = new NotSupportedException("no");
        store.FailEnsureExistsOnly = "docs_documents";

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct));

        Assert.Contains("'docs_documents' collection", e.Message);
        Assert.False(await inner.CollectionExistsAsync("docs", Ct));
    }

    [Fact]
    public async Task Existing_collections_are_kept_when_startup_is_rejected()
    {
        var inner = new InMemoryVectorStore();
        await Build(new FailingVectorStore(inner)).IngestAsync(TestRag.Md("d", "alpha"), cancellationToken: Ct);
        var store = new FailingVectorStore(inner) { RejectSearch = new NotSupportedException("Distance function CosineSimilarity is not supported.") };
        var rag = Build(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(TestRag.Md("e", "beta"), cancellationToken: Ct));

        Assert.True(await inner.CollectionExistsAsync("docs", Ct));
        Assert.True(await inner.CollectionExistsAsync("docs_documents", Ct));
        store.RejectSearch = null;
        Assert.Contains(await rag.RetrieveAsync("alpha", cancellationToken: Ct), c => c.DocumentId == "d");
    }
}
