namespace RagDotNet.Ocr;

/// <summary>
/// Renders a page of a document to an image, so an <see cref="IOcrProvider"/> can read a page that has no text layer.
/// The <c>RagDotNet.Readers.Pdf.Rendering</c> package provides one for PDF.
/// </summary>
public interface IPageRenderer
{
    /// <summary>Whether this renderer handles the given content type or file name.</summary>
    /// <param name="contentType">MIME content type, or null.</param>
    /// <param name="fileName">File name.</param>
    bool CanRender(string? contentType, string fileName);

    /// <summary>Renders one page.</summary>
    /// <param name="document">The document's bytes.</param>
    /// <param name="pageNumber">The page, starting at 1.</param>
    /// <param name="dpi">Resolution in dots per inch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page as an image.</returns>
    Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken);
}
