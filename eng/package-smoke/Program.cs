// Installs the packed RagDotNet packages into a fresh app and uses each one, the way a NuGet user would: it catches
// packaging mistakes the unit tests cannot see (a native library missing from runtimes/, a dependency left out, the wrong
// target framework). No model is needed: embeddings and chat replies are faked.
// Usage: dotnet run -f <tfm> -- <repository root>. OCR runs when RAGDOTNET_TESSDATA is set; SMOKE_REQUIRE_OCR=1 makes it required.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.VectorData;
using RagDotNet.Configuration;
using RagDotNet.DataIngestion;
using RagDotNet.Documents;
using RagDotNet.Hosting;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.VectorData.Sqlite;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run -f <tfm> -- <repository root>");
    return 2;
}

string root = Path.GetFullPath(args[0]);
string assets = Path.Combine(root, "tests", "TestAssets");
string work = Directory.CreateTempSubdirectory("ragdotnet-smoke-").FullName;
const int Dimensions = 256;

var core = typeof(RagPipelineBuilder).Assembly;
Console.WriteLine($"RagDotNet.Core {core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} from {core.Location}");
Console.WriteLine($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.RuntimeIdentifier}");

// A package-built assembly comes from the NuGet cache, never from a project's bin folder.
if (core.Location.Contains(Path.Combine("src", "RagDotNet.Core"), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("FAIL RagDotNet.Core was loaded from the source tree, not from a package.");
    return 1;
}

var failures = new List<string>();

async Task Check(string name, Func<Task> body)
{
    try
    {
        await body();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add(name);
        Console.WriteLine($"FAIL {name}: {ex}");
    }
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// Core, the three readers and the SQLite store, whose sqlite-vec native library must come from the package's runtimes/ folder.
await Check("Core, readers, SQLite store: ingest, hybrid retrieve, cited answer", async () =>
{
    var rag = new RagPipelineBuilder()
        .UseChatClient(new FakeChatClient())
        .UseEmbeddingGenerator(new FakeEmbeddingGenerator(Dimensions))
        .UseVectorStore(new SqliteVectorStore($"Data Source={Path.Combine(work, "rag.db")};Pooling=False"), "docs", Dimensions)
        .AddPdfReader()
        .AddDocxReader()
        .AddHtmlReader()
        .Build();

    string[] files =
    [
        Path.Combine(assets, "markdown", "install-guide.md"),
        Path.Combine(assets, "text", "plain.txt"),
        Path.Combine(assets, "pdf", "sample.pdf"),
        Path.Combine(assets, "docx", "sample.docx"),
        Path.Combine(assets, "html", "sample.html"),
    ];
    foreach (var file in files)
    {
        var result = await rag.IngestAsync(DocumentSource.FromFile(file));
        Expect(result.Status == IngestStatus.Created && result.ChunkCount > 0, $"{Path.GetFileName(file)}: {result.Status}, {result.ChunkCount} chunks");
        Console.WriteLine($"     {Path.GetFileName(file)}: {result.ChunkCount} chunks");
    }

    var again = await rag.IngestAsync(DocumentSource.FromFile(files[0]));
    Expect(again.Status == IngestStatus.Unchanged, $"re-ingest gave {again.Status}, expected Unchanged");

    var hits = await rag.RetrieveAsync("install");
    Expect(hits.Count > 0, "no chunks retrieved");

    var answer = await rag.AskAsync("How do I install it?");
    Expect(answer.Citations.Count > 0, $"no citations in \"{answer.Text}\"");
});

// The Lucene keyword index, kept on disk.
await Check("Lucene keyword index on disk", async () =>
{
    string index = Path.Combine(work, "lucene");
    var rag = new RagPipelineBuilder()
        .UseChatClient(new FakeChatClient())
        .UseEmbeddingGenerator(new FakeEmbeddingGenerator(Dimensions))
        .UseVectorStore(new InMemoryVectorStore(), "kw", Dimensions)
        .AddLuceneKeywordIndex(o => o.Directory = index)
        .Build();

    await rag.IngestAsync(DocumentSource.FromFile(Path.Combine(assets, "markdown", "install-guide.md")));
    var hits = await rag.RetrieveAsync("install");
    Expect(hits.Count > 0, "no chunks retrieved");
    Expect(Directory.Exists(index) && Directory.EnumerateFiles(index, "*", SearchOption.AllDirectories).Any(), "the index folder is empty");
});

// The background ingestion queue in a generic host, ingesting through a Microsoft.Extensions.DataIngestion reader.
await Check("Hosting queue with a DataIngestion reader", async () =>
{
    var builder = Host.CreateApplicationBuilder();
    builder.Services.AddSingleton<IChatClient>(new FakeChatClient());
    builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new FakeEmbeddingGenerator(Dimensions));
    builder.Services.AddSingleton<VectorStore>(new InMemoryVectorStore());
    builder.Services.AddRag(o =>
        {
            o.CollectionName = "hosted";
            o.EmbeddingDimensions = Dimensions;
        })
        .AddIngestionQueue()
        .AddDataIngestionReader(_ => new MarkdownReader(), [".md"], ["text/markdown"]);

    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        await using var scope = host.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IIngestionQueue>();
        var markdown = "# Handbook\n\n## Leave\n\nYou get 25 days of leave. Ask your manager first.\n";
        var id = await queue.EnqueueAsync(new DocumentSource("handbook", new MemoryStream(Encoding.UTF8.GetBytes(markdown)), "handbook.md", "text/markdown"));

        IngestionJobStatus? status = null;
        for (var deadline = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < deadline;)
        {
            status = await queue.GetStatusAsync(id);
            if (status?.State is IngestionJobState.Succeeded or IngestionJobState.Failed or IngestionJobState.Cancelled) break;
            await Task.Delay(100);
        }

        Expect(status?.State == IngestionJobState.Succeeded, $"job ended {status?.State}: {status?.Error}");

        var rag = scope.ServiceProvider.GetRequiredService<IRagPipeline>();
        var hits = await rag.RetrieveAsync("days of leave");
        Expect(hits.Any(h => h.Text.Contains("25 days", StringComparison.Ordinal)), "the queued document was not retrieved");
    }
    finally
    {
        await host.StopAsync();
    }
});

// Tesseract and PDF page rendering on a scanned PDF. Both load native libraries: Tesseract from the package on Windows and
// from the system elsewhere, PDFium from its package.
string? tessdata = Environment.GetEnvironmentVariable("RAGDOTNET_TESSDATA");
if (string.IsNullOrEmpty(tessdata))
{
    if (Environment.GetEnvironmentVariable("SMOKE_REQUIRE_OCR") == "1")
    {
        failures.Add("OCR");
        Console.WriteLine("FAIL OCR: SMOKE_REQUIRE_OCR=1 but RAGDOTNET_TESSDATA is not set");
    }
    else
    {
        Console.WriteLine("SKIP OCR: RAGDOTNET_TESSDATA is not set");
    }
}
else
{
    await Check("Tesseract OCR and PDF page rendering on a scanned PDF", async () =>
    {
        var rag = new RagPipelineBuilder()
            .UseChatClient(new FakeChatClient())
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(Dimensions))
            .UseVectorStore(new InMemoryVectorStore(), "scans", Dimensions)
            .AddPdfReader()
            .AddTesseractOcr(o =>
            {
                o.DataPath = tessdata;
                o.Languages = "eng";
            })
            .AddPdfPageRendering()
            .Build();

        var scanned = Path.Combine(root, "tests", "RagDotNet.Evaluation", "Dataset", "base", "formats", "scanned", "install-guide.pdf");
        var result = await rag.IngestAsync(DocumentSource.FromFile(scanned));
        foreach (var warning in result.Warnings) Console.WriteLine($"     warning {warning.Code}: {warning.Message}");
        Expect(result.ChunkCount > 0, "no chunks from the scanned PDF");

        var hits = await rag.RetrieveAsync("Monterey");
        Expect(hits.Any(h => h.Text.Contains("Quill", StringComparison.Ordinal)), "OCR text does not contain the expected words");
    });
}

try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

Console.WriteLine(failures.Count == 0 ? "All checks passed." : $"{failures.Count} check(s) failed: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;

/// <summary>Deterministic hashed bag-of-words embeddings: similar words give similar vectors, with no model.</summary>
sealed class FakeEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly EmbeddingGeneratorMetadata _metadata = new("fake", null, "fake-embed", dimensions);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values) result.Add(new Embedding<float>(Embed(value)));
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(_metadata) ? _metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (var token in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes(token.Trim('.', ',', ':', ';', '(', ')', '"', '\''))) hash = (hash ^ b) * 16777619u;
            vector[hash % (uint)dimensions] += 1f;
        }

        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm > 0) for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }
}

/// <summary>Answers every question by citing the first source.</summary>
sealed class FakeChatClient : IChatClient
{
    private const string Reply = "The sources cover this [1].";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply)));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, Reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
