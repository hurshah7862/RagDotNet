using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace RagDotNet.TestAssetGen;

/// <summary>
/// Makes image-only copies of PDFs, like a photocopy: each page is rendered and stored as one JPEG with no text layer.
/// </summary>
internal static class ScannedWriter
{
    private const int Dpi = 200;
    private const int JpegQuality = 85;

#pragma warning disable CA1416 // PDFtoImage supports the desktop platforms this generator runs on
    public static void Write(string sourcePdf, string target)
    {
        var bytes = File.ReadAllBytes(sourcePdf);
        using var source = PdfDocument.Open(bytes);
        var builder = new PdfDocumentBuilder();
        foreach (var page in source.GetPages())
        {
            using var bitmap = Conversion.ToImage(bytes, page: page.Number - 1, options: new RenderOptions(Dpi: Dpi));
            using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            var scanned = builder.AddPage(page.Width, page.Height);
            scanned.AddJpeg(jpeg.ToArray(), new PdfRectangle(0, 0, page.Width, page.Height));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, builder.Build());
    }
#pragma warning restore CA1416
}
