using RagDotNet.Documents;

namespace RagDotNet.Ocr;

/// <summary>What an <see cref="IDocumentOcrProvider"/> read.</summary>
/// <param name="Blocks">Blocks in document order, each with its <see cref="DocumentBlock.PageNumber"/>.</param>
public sealed record DocumentOcrResult(IReadOnlyList<DocumentBlock> Blocks)
{
    /// <summary>Warnings the provider wants reported with the ingest result.</summary>
    public IReadOnlyList<IngestWarning> Warnings { get; init; } = [];
}
