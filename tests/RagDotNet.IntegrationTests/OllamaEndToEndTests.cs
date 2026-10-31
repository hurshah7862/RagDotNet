using CommunityToolkit.VectorData.InMemory;
using OllamaSharp;
using RagDotNet.Configuration;
using RagDotNet.Documents;

namespace RagDotNet.IntegrationTests;

public class OllamaEndToEndTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public async Task Ask_over_sample_corpus_with_Ollama_cites_install_guide()
    {
        var ct = TestContext.Current.CancellationToken;

        if (Environment.GetEnvironmentVariable("RAGDOTNET_INTEGRATION") != "1")
        {
            Assert.Skip("Set RAGDOTNET_INTEGRATION=1 to run the Ollama integration test.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(Endpoint + "/api/tags", ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Skip($"Ollama is not reachable at {Endpoint} ({ex.GetType().Name}). Run 'ollama serve'.");
        }

        var endpoint = new Uri(Endpoint);
        var pipeline = new RagPipelineBuilder()
            .UseChatClient(new OllamaApiClient(endpoint, "llama3.2:3b"))
            .UseEmbeddingGenerator(new OllamaApiClient(endpoint, "nomic-embed-text"))
            .UseVectorStore(new InMemoryVectorStore(), "integration", 768)
            .Build();

        string corpus = Path.Combine(AppContext.BaseDirectory, "TestAssets", "markdown");
        foreach (string file in Directory.EnumerateFiles(corpus, "*.md"))
        {
            await pipeline.IngestAsync(DocumentSource.FromFile(file), cancellationToken: ct);
        }

        var answer = await pipeline.AskAsync("How do I install on Linux?", cancellationToken: ct);

        Assert.True(answer.IsAnswered, answer.Text);
        Assert.Contains(answer.Citations, c => c.Chunk.DocumentId.EndsWith("install-guide.md", StringComparison.Ordinal));
    }
}
