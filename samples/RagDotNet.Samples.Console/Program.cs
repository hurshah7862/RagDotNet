// Quick start: ingest a folder of Markdown and text files into an in-memory store, then answer questions about them.
using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Documents;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: RagDotNet.Samples.Console <folder containing .md/.txt files>");
    return 1;
}

Microsoft.Extensions.AI.IChatClient chat;
Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> embedder;
int dimensions;
try
{
    (chat, embedder, dimensions) = ProviderFactory.Create(ProviderFactory.LoadSettings());
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Markdown and plain text need no extra reader package.
var pipeline = new RagPipelineBuilder()
    .UseChatClient(chat)
    .UseEmbeddingGenerator(embedder)
    .UseVectorStore(new InMemoryVectorStore(), "samples", dimensions)
    .Build();

var files = Directory.EnumerateFiles(args[0], "*", SearchOption.AllDirectories)
    .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal)
    .ToList();

try
{
    foreach (string file in files)
    {
        var result = await pipeline.IngestAsync(DocumentSource.FromFile(file));
        Console.WriteLine($"{result.DocumentId}: {result.Status}, {result.ChunkCount} chunks, {result.Warnings.Count} warnings");
    }

    await QuestionLoop.RunAsync(pipeline);
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    // TaskCanceledException here is HttpClient's timeout: a local model loading on a busy machine can take minutes.
    Console.Error.WriteLine("Could not reach the model provider, or it did not answer in time — is `ollama serve` running and are the models pulled?");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return 0;
