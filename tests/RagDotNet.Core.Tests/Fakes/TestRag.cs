using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Pipeline;

namespace RagDotNet.Core.Tests.Fakes;

internal static class TestRag
{
    public static (IRagPipeline Pipeline, RagPipelineBuilder Builder, FakeEmbeddingGenerator Embedder, FakeChatClient Chat, InMemoryVectorStore Store) Create(
        Action<RagOptions>? configure = null, Action<RagPipelineBuilder>? builder = null)
    {
        var embedder = new FakeEmbeddingGenerator(64);
        var chat = new FakeChatClient();
        var store = new InMemoryVectorStore();
        var b = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder)
            .UseChatClient(chat)
            .UseVectorStore(store, "test", 64);
        if (configure is not null) b.Configure(configure);
        builder?.Invoke(b);
        return (b.Build(), b, embedder, chat, store);
    }

    public static DocumentSource Md(string id, string markdown, IDictionary<string, object?>? metadata = null)
    {
        var source = new DocumentSource(id, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown)), id + ".md", "text/markdown");
        if (metadata is not null)
            foreach (var (k, v) in metadata) source.Metadata[k] = v;
        return source;
    }
}
