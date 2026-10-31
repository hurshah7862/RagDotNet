using RagDotNet.Documents;

namespace RagDotNet.Chunking;

/// <summary>Splits a parsed document into chunks.</summary>
public interface IChunker
{
    /// <summary>The strategy this chunker implements.</summary>
    ChunkingStrategy Strategy { get; }

    /// <summary>Chunks <paramref name="document"/>.</summary>
    /// <param name="document">The document to split.</param>
    /// <param name="options">Chunking options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks in document order.</returns>
    Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument document, ChunkingOptions options, CancellationToken cancellationToken);
}
