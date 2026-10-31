namespace RagDotNet.Content;

/// <summary>A chunk's text and the data needed to join it to its vector record.</summary>
/// <param name="ChunkKey">The string chunk key; the same key the vector record uses before any Guid hashing.</param>
/// <param name="DocumentId">The document id.</param>
/// <param name="Partition">The raw partition values; empty when the collection is not partitioned.</param>
/// <param name="ChunkIndex">The zero-based chunk index within the document.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="PageStart">First page of the chunk, if known.</param>
/// <param name="PageEnd">Last page of the chunk, if known.</param>
/// <param name="HeadingPath">The headings above the chunk.</param>
public sealed record ChunkContent(
    string ChunkKey,
    string DocumentId,
    IReadOnlyDictionary<string, string> Partition,
    int ChunkIndex,
    string Text,
    int? PageStart,
    int? PageEnd,
    IReadOnlyList<string> HeadingPath);
