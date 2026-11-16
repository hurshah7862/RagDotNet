using RagDotNet.Readers.Pdf.Rendering;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagDotNet.Ocr.Tests;

/// <summary>Where Tesseract's language data is, and helpers that make scanned test pages.</summary>
internal static class OcrTestEnvironment
{
    /// <summary>
    /// The tessdata folder: <c>RAGDOTNET_TESSDATA</c>, else the Ubuntu/Debian package path. When it holds no
    /// <c>eng.traineddata</c>, the test is skipped locally and fails on CI, so CI can never pass without running OCR.
    /// </summary>
    public static string TessData
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("RAGDOTNET_TESSDATA") ?? "/usr/share/tesseract-ocr/5/tessdata";
            if (File.Exists(Path.Combine(path, "eng.traineddata"))) return path;
            var message = $"Tesseract language data (eng.traineddata) not found in '{path}'; set RAGDOTNET_TESSDATA.";
            if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                Assert.Fail(message);
            Assert.Skip(message);
            return path;
        }
    }

    /// <summary>A one-page PDF with <paramref name="lines"/> as real text, 14 pt Helvetica.</summary>
    public static byte[] TextPdf(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        var y = 760.0;
        foreach (var line in lines)
        {
            page.AddText(line, 14, new PdfPoint(72, y), font);
            y -= 22;
        }

        return builder.Build();
    }

    /// <summary>The text page rendered at 300 dpi, as PNG.</summary>
    public static async Task<ImageInput> PageImage(params string[] lines) =>
        await new PdfPageRenderer().RenderPageAsync(TextPdf(lines), 1, 300, TestContext.Current.CancellationToken);

    /// <summary>A two-page PDF: page 1 has a text layer, page 2 is only an image of <paramref name="scannedLines"/>, like a scan.</summary>
    public static async Task<byte[]> PdfWithScannedPage(string pageOneText, params string[] scannedLines)
    {
        var scan = await PageImage(scannedLines);
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var first = builder.AddPage(595, 842);
        first.AddText(pageOneText, 11, new PdfPoint(72, 760), font);
        var second = builder.AddPage(595, 842);
        second.AddPng(scan.Bytes.ToArray(), new PdfRectangle(0, 0, 595, 842));
        return builder.Build();
    }
}
