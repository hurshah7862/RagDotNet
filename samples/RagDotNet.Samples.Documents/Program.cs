// PDF, Word and HTML documents in a SQLite file, with a Lucene keyword index next to it for hybrid search.
// Both are saved to disk, so a second run skips documents that have not changed.
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.VectorData.Sqlite;

if (args.Length == 0 || args.Any(a => !File.Exists(a) && !Directory.Exists(a)))
{
    Console.Error.WriteLine("Usage: RagDotNet.Samples.Documents <file or folder> [<file or folder> ...]");
    Console.Error.WriteLine("Reads .pdf, .docx, .html, .md and .txt files.");
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

// The index lives next to the app. The collection name includes the dimensions, so switching to a provider
// whose embeddings have a different size starts a new collection instead of failing on the old one.
string indexFolder = Path.Combine(AppContext.BaseDirectory, "index");
Directory.CreateDirectory(indexFolder);
Console.WriteLine($"Index folder: {indexFolder}");

var pipeline = new RagPipelineBuilder()
    .UseChatClient(chat)
    .UseEmbeddingGenerator(embedder)
    .UseVectorStore(new SqliteVectorStore($"Data Source={Path.Combine(indexFolder, "rag.db")}"), $"docs_{dimensions}", dimensions)
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader()
    // With a keyword index, retrieval combines vector and keyword search (hybrid), which finds exact terms
    // such as command names and version numbers that embeddings alone can miss.
    .AddLuceneKeywordIndex(o => o.Directory = Path.Combine(indexFolder, $"keywords_{dimensions}"))
    .Build();

string[] extensions = [".pdf", ".docx", ".html", ".htm", ".md", ".txt"];
var files = args
    .SelectMany(a => File.Exists(a) ? [a] : Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories))
    .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
    .Select(Path.GetFullPath)
    .Distinct()
    .Order(StringComparer.Ordinal)
    .ToList();

try
{
    foreach (string file in files)
    {
        try
        {
            // The document id is the full path, so two files with the same name in different folders stay separate.
            IngestResult result = await pipeline.IngestAsync(DocumentSource.FromFile(file, documentId: file));
            Console.WriteLine($"{Path.GetFileName(file)}: {result.Status}, {result.ChunkCount} chunks");

            // Nothing is skipped silently: a scanned page or an image the reader could not read is a warning.
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"  warning {warning.Code}: {warning.Message}");
            }
        }
        catch (DocumentReadException ex)
        {
            // A corrupt or password-protected file stops only that file.
            Console.WriteLine($"{Path.GetFileName(file)}: not read: {ex.Message}");
        }
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
