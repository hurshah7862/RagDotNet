using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ocr.Tesseract;
using static RagDotNet.Ocr.Tests.OcrTestEnvironment;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Ocr.Tests;

public class TesseractOcrProviderTests
{
    [Fact]
    public async Task Reads_a_rendered_text_page()
    {
        using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = TessData });

        var result = await ocr.RecognizeAsync(await PageImage("Quill sync keeps notes current"), Ct);

        Assert.Contains("Quill sync keeps notes current", result.Text, StringComparison.Ordinal);
        Assert.True(result.Confidence > 0.6, $"confidence {result.Confidence}");
    }

    [Fact]
    public async Task Pipeline_with_AddTesseractOcr_finds_text_on_a_scanned_page()
    {
        var pdf = await PdfWithScannedPage("Quill notebooks overview.", "Quill keeps every notebook encrypted at rest.");
        var rag = Builder().AddPdfReader().AddTesseractOcr(o => o.DataPath = TessData).Build();

        var result = await rag.IngestAsync(new DocumentSource("scan", new MemoryStream(pdf), "scan.pdf"), cancellationToken: Ct);

        Assert.DoesNotContain(result.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText);
        var chunks = await rag.RetrieveAsync("Is every notebook encrypted at rest?", cancellationToken: Ct);
        Assert.Contains(chunks, c => c.PageStart <= 2 && c.PageEnd >= 2 && c.Text.Contains("encrypted at rest", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_language_data_throws_OcrUnavailable_naming_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-tessdata-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<OcrUnavailableException>(() => new TesseractOcrProvider(new TesseractOcrOptions { DataPath = missing, Languages = "eng+deu" }));

        Assert.Contains(Path.Combine(missing, "eng.traineddata"), ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/tesseract-ocr/tessdata_fast", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Di_host_fails_at_start_when_language_data_is_missing()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(new HashEmbedder());
        builder.Services.AddSingleton<Microsoft.Extensions.VectorData.VectorStore>(new CommunityToolkit.VectorData.InMemory.InMemoryVectorStore());
        builder.Services.AddRag(o => o.EmbeddingDimensions = HashEmbedder.Dimensions).AddTesseractOcr(o => o.DataPath = "/nowhere/tessdata");
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));

        Assert.Contains("eng.traineddata", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Di_registers_tesseract_and_page_rendering()
    {
        var services = new ServiceCollection();
        services.AddRag().AddTesseractOcr(o => o.DataPath = TessData);
        await using var provider = services.BuildServiceProvider();

        Assert.IsType<TesseractOcrProvider>(provider.GetRequiredService<IOcrProvider>());
        Assert.Single(provider.GetServices<IPageRenderer>());
    }

    [Fact] // Review Focus 1
    public async Task Concurrent_recognitions_each_get_their_own_text()
    {
        using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = TessData });
        var alpha = await PageImage("alpha bravo charlie");
        var delta = await PageImage("delta echo foxtrot");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ocr.RecognizeAsync(i % 2 == 0 ? alpha : delta, Ct)));

        for (var i = 0; i < results.Length; i++)
            Assert.Contains(i % 2 == 0 ? "alpha bravo charlie" : "delta echo foxtrot", results[i].Text, StringComparison.Ordinal);
    }

    [Fact] // Review Focus 3
    public async Task Corrupt_image_bytes_throw_and_ingest_records_OcrFailed()
    {
        using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = TessData });
        var garbage = new byte[4096];
        new Random(3).NextBytes(garbage);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => ocr.RecognizeAsync(new ImageInput(garbage, "image/png"), Ct));
        Assert.IsNotType<OcrUnavailableException>(thrown);

        var html = $"<main><p>Release notes.</p><img src=\"data:image/png;base64,{Convert.ToBase64String(garbage)}\"></main>";
        var rag = Builder().AddHtmlReader().UseOcrProvider(ocr).Build();
        var result = await rag.IngestAsync(new DocumentSource("notes", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html)), "notes.html"), cancellationToken: Ct);

        Assert.Contains(result.Warnings, w => w.Code == IngestWarningCodes.OcrFailed);
        Assert.Equal(Ingestion.IngestStatus.Created, result.Status);
    }

    [Fact] // Final review minor 13: switching tessdata_fast for tessdata_best changed output but not the Id
    public void Id_names_the_languages_and_the_language_data()
    {
        using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = TessData });

        Assert.Matches("^tesseract/eng/[0-9a-f]{8}$", ocr.Id);
    }

    [Fact] // Final review Important 4: the link folder was a predictable shared /tmp path another user could plant
    public void Native_links_live_in_a_private_folder_of_this_process()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows loads the included binaries; no links are made.");
        using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = TessData });

        var root = TesseractNative.LinkRoot!;

        Assert.NotEqual(Path.Combine(Path.GetTempPath(), "ragdotnet-tesseract-" + Environment.UserName), root);
        Assert.StartsWith("ragdotnet-tesseract-", Path.GetFileName(root), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
    }
}
