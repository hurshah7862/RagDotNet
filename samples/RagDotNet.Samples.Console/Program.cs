using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Configuration;
using RagDotNet.Configuration;
using RagDotNet.Documents;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: RagDotNet.Samples.Console <folder containing .md/.txt files>");
    return 1;
}

IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

Microsoft.Extensions.AI.IChatClient chat;
Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>> embedder;
int dimensions;
try
{
    (chat, embedder, dimensions) = ProviderFactory.Create(config);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var pipeline = new RagPipelineBuilder()
    .UseChatClient(chat)
    .UseEmbeddingGenerator(embedder)
    .UseVectorStore(new InMemoryVectorStore(), "samples", dimensions)
    .Build();

var files = Directory.EnumerateFiles(args[0], "*", SearchOption.AllDirectories)
    .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal)
    .ToList();

foreach (string file in files)
{
    var result = await pipeline.IngestAsync(DocumentSource.FromFile(file));
    Console.WriteLine($"{result.DocumentId}: {result.Status}, {result.ChunkCount} chunks, {result.Warnings.Count} warnings");
}

while (true)
{
    Console.Write("\nQuestion (empty line to exit): ");
    string? question = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(question)) break;

    RagDotNet.Generation.RagAnswer? final = null;
    await foreach (var update in pipeline.AskStreamingAsync(question))
    {
        if (update.Text is not null) Console.Write(update.Text);
        if (update.Final is not null) final = update.Final;
    }

    Console.WriteLine();
    if (final is null) continue;
    if (!final.IsAnswered) Console.WriteLine("(no answer found in the documents)");
    foreach (var c in final.Citations)
    {
        Console.WriteLine($"[{c.Number}] {c.Chunk.SourceName} — {c.Location}");
    }
}

return 0;
