namespace RagDotNet.Documents;

/// <summary>Information about the document being read.</summary>
/// <param name="FileName">File name.</param>
/// <param name="ContentType">MIME content type, if known.</param>
public sealed record DocumentReadContext(string FileName, string? ContentType)
{
    /// <summary>
    /// Whether readers should extract the bytes of embedded images into <see cref="OcrCandidate.Image"/>. Ingest sets it
    /// only when an OCR provider will read them, so documents are not decoded for nothing; readers still report every
    /// image as a candidate either way. Default true.
    /// </summary>
    public bool ExtractImages { get; init; } = true;
}
