using RagDotNet.Configuration;
using RagDotNet.Documents;
using static RagDotNet.Readers.Tests.ReaderTestKit;

namespace RagDotNet.Readers.Tests;

/// <summary>With no OCR provider registered, ingest reports exactly the warnings stage 5 did.</summary>
public class Stage5WarningParityTests
{
    [Fact]
    public async Task Pdf_sample_warnings_are_unchanged_without_ocr()
    {
        var rag = Builder().AddPdfReader().Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(Asset("pdf", "sample.pdf"), "pdf"), cancellationToken: Ct);

        Assert.Equal(
            [
                new IngestWarning(IngestWarningCodes.PageWithoutText, "Page 2 has no extractable text; register an OCR provider to read it.", 2),
                new IngestWarning(IngestWarningCodes.ImageNotRead, "Page 3 has 1 image(s) that were not read; register an OCR provider to read them.", 3),
            ],
            result.Warnings);
    }

    [Fact]
    public async Task Docx_sample_warnings_are_unchanged_without_ocr()
    {
        var rag = Builder().AddDocxReader().Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(Asset("docx", "sample.docx"), "docx"), cancellationToken: Ct);

        Assert.Equal([new IngestWarning(IngestWarningCodes.ImageNotRead, "1 embedded image(s) were not read; register an OCR provider to read them.")], result.Warnings);
    }

    [Fact]
    public async Task Html_sample_warnings_are_unchanged_without_ocr()
    {
        var rag = Builder().AddHtmlReader().Build();

        var result = await rag.IngestAsync(DocumentSource.FromFile(Asset("html", "sample.html"), "html"), cancellationToken: Ct);

        Assert.Equal([new IngestWarning(IngestWarningCodes.ImageNotRead, "1 embedded image(s) were not read; register an OCR provider to read them.")], result.Warnings);
    }
}
