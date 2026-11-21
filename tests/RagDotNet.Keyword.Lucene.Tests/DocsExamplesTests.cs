using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Keyword.Lucene;
using RagDotNet.Pipeline;
using RagDotNet.Retrieval;

namespace RagDotNet.Keyword.Lucene.Tests;

/// <summary>The code examples in README.md and docs/retrieval.md, kept compiling and running.</summary>
public sealed class DocsExamplesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragdotnet-docs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(64));
        services.AddSingleton<VectorStore>(new InMemoryVectorStore());
        return services;
    }

    [Fact]
    public async Task Readme_hybrid_search_examples()
    {
        var services = Services();
        services.AddSingleton<IChatClient, NullChatClient>();
        services.AddRag(o => { o.EmbeddingDimensions = 64; o.Hybrid = HybridMode.Off; });                         // vector-only, for the whole app
        services.AddRag().AddLuceneKeywordIndex(o => o.Directory = _dir);  // persisted index, for large corpora
        services.AddRag().AddLlmReranker();                                       // rerank the top 20 with the chat model
        await using var provider = services.BuildServiceProvider();
        var rag = provider.GetRequiredService<IRagPipeline>();
        var question = "What is new?";

        var chunks = await rag.RetrieveAsync(question, new RetrievalOptions { Hybrid = HybridMode.Off, UseReranker = false }, TestContext.Current.CancellationToken); // per call

        Assert.Empty(chunks);
    }

    [Fact]
    public async Task Retrieval_doc_examples()
    {
        var question = "What is new?";
        IRagPipeline rag;
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseVectorStore(new InMemoryVectorStore(), "docs", 64);

        using var index = new LuceneKeywordIndex(new LuceneKeywordIndexOptions { Directory = _dir });
        rag = builder.UseKeywordIndex(index).Build();

        var chunks = await rag.RetrieveAsync(question, new RetrievalOptions
        {
            Hybrid = HybridMode.Off,   // vector-only for this call
            UseReranker = false,       // skip the registered reranker for this call
        }, TestContext.Current.CancellationToken);

        Assert.Empty(chunks);
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
