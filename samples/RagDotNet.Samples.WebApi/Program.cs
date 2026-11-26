// A web API: upload documents, which are ingested in the background, check on the job, and ask questions.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagDotNet.Documents;
using RagDotNet.Hosting;
using RagDotNet.Pipeline;
using RagDotNet.VectorData.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// The model provider comes from providers.json, shared by all the samples.
var (chat, embedder, dimensions) = ProviderFactory.Create(ProviderFactory.LoadSettings());

// RagDotNet uses the app's own chat client, embedding generator and vector store.
builder.Services.AddSingleton(chat);
builder.Services.AddSingleton(embedder);
string indexFolder = Path.Combine(AppContext.BaseDirectory, "index");
Directory.CreateDirectory(indexFolder);
builder.Services.AddSingleton<VectorStore>(new SqliteVectorStore($"Data Source={Path.Combine(indexFolder, "rag.db")}"));

builder.Services.AddRag(o =>
    {
        o.CollectionName = $"docs_{dimensions}";
        o.EmbeddingDimensions = dimensions;
    })
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader()
    // Uploads are ingested by a background worker, so the upload request returns before every chunk is embedded.
    .AddIngestionQueue(o => o.Concurrency = 2);

var app = builder.Build();

// Upload a file: returns 202 Accepted with the job id, and a Location header to check on it.
// The queue copies the upload to a temporary file before returning, so the request stream can close.
string[] allowedExtensions = [".pdf", ".docx", ".html", ".htm", ".md", ".txt"];
app.MapPost("/documents", async (IFormFile file, IIngestionQueue queue, CancellationToken ct) =>
    {
        // The file name comes from the client: keep only its last part, and reject what the readers cannot read,
        // so a bad upload gets a 400 now instead of a failed job later.
        string fileName = Path.GetFileName(file.FileName.Replace('\\', '/')).Trim();
        if (fileName.Length == 0 || !allowedExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            return Results.BadRequest($"Upload a file named with one of: {string.Join(", ", allowedExtensions)}.");
        }

        // Clients send application/octet-stream when they don't know a file's type (curl does for .md, for example).
        // A content type, when given, decides which reader reads the file, so pass null for a generic one and let
        // the file extension decide.
        string? contentType = string.IsNullOrWhiteSpace(file.ContentType)
            || file.ContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? null
            : file.ContentType;

        await using var stream = file.OpenReadStream();
        string jobId = await queue.EnqueueAsync(new DocumentSource(fileName, stream, fileName, contentType), ct);
        return Results.Accepted($"/jobs/{jobId}", new { jobId });
    })
    .DisableAntiforgery(); // a sample API without browser forms; keep antiforgery on for apps that serve forms

// A job's state: Queued, Running, Succeeded, Failed or Cancelled.
app.MapGet("/jobs/{jobId}", async (string jobId, IIngestionQueue queue, CancellationToken ct) =>
    await queue.GetStatusAsync(jobId, ct) is { } job
        ? Results.Ok(new
        {
            job.JobId,
            job.DocumentId,
            State = job.State.ToString(),
            job.Attempts,
            job.Error,
            Chunks = job.Result?.ChunkCount,
            Warnings = job.Result?.Warnings.Select(w => $"{w.Code}: {w.Message}"),
        })
        : Results.NotFound());

// Ask a question: the answer, whether the documents held one, and the numbered sources it cites.
app.MapPost("/ask", async (Question question, IRagPipeline rag, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(question.Text)) return Results.BadRequest("Send {\"text\": \"your question\"}.");
    var answer = await rag.AskAsync(question.Text, cancellationToken: ct);
    return Results.Ok(new
    {
        answer.Text,
        answer.IsAnswered,
        Citations = answer.Citations.Select(c => new { c.Number, c.Chunk.SourceName, c.Location }),
    });
});

app.Run();

internal sealed record Question(string Text);
