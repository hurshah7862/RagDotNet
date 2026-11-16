using RagDotNet.Ocr;

namespace RagDotNet.Documents;

/// <summary>What a reader found that only OCR can read.</summary>
public enum OcrCandidateKind
{
    /// <summary>A page whose text is only in an image, such as a scanned page; it must be rendered to be read.</summary>
    ScannedPage,

    /// <summary>An image embedded in the document.</summary>
    Image,
}

/// <summary>
/// A scanned page or an embedded image a reader could not read itself. Ingest hands it to the registered OCR provider and
/// places the recognised text before <see cref="ParsedDocument.Blocks"/>[<see cref="InsertAt"/>]; with no provider it
/// becomes a <c>PageWithoutText</c> or <c>ImageNotRead</c> warning.
/// </summary>
/// <param name="Kind">A scanned page or an embedded image.</param>
/// <param name="InsertAt">The index in <see cref="ParsedDocument.Blocks"/> the recognised text goes before; the block count means at the end.</param>
/// <param name="PageNumber">The page, for paged formats such as PDF.</param>
/// <param name="Image">The image; null for a scanned page, and for an image whose bytes could not be extracted.</param>
public sealed record OcrCandidate(OcrCandidateKind Kind, int InsertAt, int? PageNumber, ImageInput? Image = null)
{
    /// <summary>For a scanned page: the page also has a little text, such as a page-number stamp over the scan.</summary>
    public bool HasTextLayer { get; init; }

    /// <summary>For an image: it covers most of a page that has a text layer, so it is the scan under that text.</summary>
    public bool IsPageBackground { get; init; }
}
