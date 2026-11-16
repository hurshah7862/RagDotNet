namespace RagDotNet.Documents;

/// <summary>The result of reading a document.</summary>
/// <param name="Blocks">Blocks in document order.</param>
/// <param name="Title">Document title when known.</param>
public sealed record ParsedDocument(IReadOnlyList<DocumentBlock> Blocks, string? Title = null)
{
    /// <summary>Warnings raised while reading.</summary>
    public IReadOnlyList<IngestWarning> Warnings { get; init; } = [];

    /// <summary>Scanned pages and embedded images that only OCR can read, in document order.</summary>
    public IReadOnlyList<OcrCandidate> OcrCandidates { get; init; } = [];
}
