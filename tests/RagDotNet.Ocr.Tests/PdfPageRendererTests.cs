using Microsoft.Extensions.DependencyInjection;
using RagDotNet.Configuration;
using RagDotNet.Documents;
using RagDotNet.Ocr;
using RagDotNet.Readers.Pdf.Rendering;
using UglyToad.PdfPig;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Ocr.Tests;

public class PdfPageRendererTests
{
    private static readonly byte[] Sample = File.ReadAllBytes(Asset("pdf", "sample.pdf"));

    [Fact]
    public async Task Renders_page_2_of_the_sample_at_300_dpi_as_png()
    {
        double pageWidth, pageHeight;
        using (var pdf = PdfDocument.Open(Sample))
            (pageWidth, pageHeight) = (pdf.GetPage(2).Width, pdf.GetPage(2).Height);

        var image = await new PdfPageRenderer().RenderPageAsync(Sample, 2, 300, Ct);

        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, image.Bytes.Span[..4].ToArray());
        Assert.InRange(image.Width!.Value, pageWidth * 300 / 72 - 2, pageWidth * 300 / 72 + 2);
        Assert.InRange(image.Height!.Value, pageHeight * 300 / 72 - 2, pageHeight * 300 / 72 + 2);
    }

    [Theory]
    [InlineData("application/pdf", "download", true)]
    [InlineData(null, "Report.PDF", true)]
    [InlineData(null, "report.docx", false)]
    [InlineData("text/html", "x.pdf", false)]
    public void CanRender_is_pdf_only(string? contentType, string fileName, bool expected) =>
        Assert.Equal(expected, new PdfPageRenderer().CanRender(contentType, fileName));

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task Page_number_out_of_range_throws_ArgumentOutOfRangeException(int page) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PdfPageRenderer().RenderPageAsync(Sample, page, 300, Ct));

    [Fact]
    public async Task Pipeline_with_AddPdfPageRendering_and_a_fake_ocr_provider_indexes_the_scanned_page()
    {
        var rag = Builder().AddPdfReader().AddPdfPageRendering().UseOcrProvider(new FakeOcr()).Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(Asset("pdf", "sample.pdf"), "teams"), cancellationToken: Ct);

        Assert.DoesNotContain(result.Warnings, w => w.Code == IngestWarningCodes.PageWithoutText);
        var chunks = await rag.RetrieveAsync("scanned roster of the support team", cancellationToken: Ct);
        Assert.Contains(chunks, c => c.PageStart <= 2 && c.PageEnd >= 2 && c.Text.Contains("scanned roster of the support team", StringComparison.Ordinal));
    }

    [Fact]
    public void Di_AddPdfPageRendering_registers_the_renderer()
    {
        var services = new ServiceCollection();
        services.AddRag().AddPdfPageRendering();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PdfPageRenderer>(Assert.Single(provider.GetServices<IPageRenderer>()));
    }

    private sealed class FakeOcr : IOcrProvider
    {
        public string Id => "fake";

        public Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult(image.Width > 1000 ? "The scanned roster of the support team." : "", 0.9));
    }
}
