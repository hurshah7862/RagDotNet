using PDFtoImage;
using RagDotNet.Ocr;
using SkiaSharp;

namespace RagDotNet.Readers.Pdf.Rendering;

/// <summary>
/// Renders PDF pages to PNG with PDFium (through PDFtoImage), so a scanned page can be handed to an <see cref="IOcrProvider"/>.
/// </summary>
public sealed class PdfPageRenderer : IPageRenderer
{
    /// <inheritdoc />
    public bool CanRender(string? contentType, string fileName) =>
        contentType is not null
            ? string.Equals(contentType.Split(';')[0].Trim(), "application/pdf", StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> is not a page of the document, or <paramref name="dpi"/> is not positive.</exception>
    public Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        // Ingest hands over the whole file as one array: use it as is rather than copying it for every page.
        var pdf = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(document, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : document.ToArray();
        return Task.Run(() => Render(pdf, pageNumber, dpi), cancellationToken);
    }

    // PDFtoImage marks its API with the platforms it ships native PDFium for: Windows, Linux, macOS (and mobile).
    // Those are the platforms this package supports, so the per-call platform check is not repeated for callers.
#pragma warning disable CA1416
    private static ImageInput Render(byte[] pdf, int pageNumber, int dpi)
    {
        var pages = Conversion.GetPageCount(pdf);
        if (pageNumber < 1 || pageNumber > pages)
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, $"The document has {pages} page(s).");

        using var bitmap = Conversion.ToImage(pdf, page: pageNumber - 1, options: new RenderOptions(Dpi: dpi));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new ImageInput(data.ToArray(), "image/png", bitmap.Width, bitmap.Height);
    }
#pragma warning restore CA1416
}
