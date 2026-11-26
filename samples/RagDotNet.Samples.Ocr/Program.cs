// Scanned PDFs, and images inside PDF, Word and HTML files, read with Tesseract OCR and then searched like any other text.
using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ocr;
using RagDotNet.Ocr.Tesseract;

if (args.Length == 0 || args.Any(a => !File.Exists(a)))
{
    Console.Error.WriteLine("Usage: RagDotNet.Samples.Ocr <file> [<file> ...]");
    Console.Error.WriteLine("Reads .pdf, .docx and .html files. Tesseract's language data is read from RAGDOTNET_TESSDATA, else ./tessdata.");
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

// The folder holding eng.traineddata (from https://github.com/tesseract-ocr/tessdata_fast).
// On Debian and Ubuntu, `apt install tesseract-ocr` puts it in /usr/share/tesseract-ocr/5/tessdata.
string tessdata = Environment.GetEnvironmentVariable("RAGDOTNET_TESSDATA") ?? "./tessdata";

try
{
    // Created here and disposed when the app ends, so Tesseract frees its language data before the process exits.
    // (`builder.AddTesseractOcr(...)` is shorter, but keeps the engine for the life of the process.)
    using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = tessdata, Languages = "eng" });

    var pipeline = new RagPipelineBuilder()
        .UseChatClient(chat)
        .UseEmbeddingGenerator(embedder)
        .UseVectorStore(new InMemoryVectorStore(), "scans", dimensions)
        .AddPdfReader()
        .AddDocxReader()
        .AddHtmlReader()
        .UseOcrProvider(ocr)
        // Turns scanned PDF pages into images for the OCR provider; without it they stay PageWithoutText warnings.
        .AddPdfPageRendering()
        // OCR returns a scanned page as one block of text, so with the default 512 tokens a page becomes one chunk.
        // Smaller chunks for OCR text help a small model answer from the right sentence; other text keeps 512.
        .Configure(o => o.Ocr.ChunkMaxTokens = 128)
        .Build();

    foreach (string file in args)
    {
        Console.WriteLine($"Reading {Path.GetFileName(file)} (OCR takes a few seconds a page)...");
        try
        {
            var result = await pipeline.IngestAsync(DocumentSource.FromFile(file));
            Console.WriteLine($"{Path.GetFileName(file)}: {result.Status}, {result.ChunkCount} chunks");

            // OcrLowConfidence means text was recognised but may hold mistakes; it is kept and searched anyway.
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"  warning {warning.Code}: {warning.Message}");
            }
        }
        catch (UnsupportedDocumentException ex)
        {
            // OCR reads scanned pages and images inside documents; a bare .png or .jpg is not a document format.
            Console.WriteLine($"{Path.GetFileName(file)}: not read: {ex.Message}");
        }
        catch (DocumentReadException ex)
        {
            // A corrupt or password-protected file stops only that file.
            Console.WriteLine($"{Path.GetFileName(file)}: not read: {ex.Message}");
        }
    }

    await QuestionLoop.RunAsync(pipeline);
}
catch (OcrUnavailableException ex)
{
    // Tesseract or its language data is missing; the message says what to install.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
{
    // TaskCanceledException here is HttpClient's timeout: a local model loading on a busy machine can take minutes.
    Console.Error.WriteLine("Could not reach the model provider, or it did not answer in time — is `ollama serve` running and are the models pulled?");
    Console.Error.WriteLine(ex.Message);
    return 1;
}

return 0;
