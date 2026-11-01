using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>
/// Splits a parsed document into chunks. The library renumbers chunk indexes, drops blank chunks, splits chunks over
/// <see cref="ChunkingOptions.MaxTokens"/> and recomputes token counts, so a chunker need not get those right.
/// </summary>
public interface IChunker
{
    /// <summary>The unique name that <see cref="ChunkingOptions.Chunker"/> selects this chunker by. Matching is ordinal and case-sensitive.</summary>
    string Name { get; }

    /// <summary>The chunker's version. Changing it re-chunks documents on their next ingest.</summary>
    string Version { get; }

    /// <summary>Chunks <paramref name="document"/>.</summary>
    /// <param name="document">The document to split.</param>
    /// <param name="options">Chunking options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks in document order.</returns>
    Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken);
}
