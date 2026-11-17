namespace RagDotNet.Keyword;

/// <summary>A chunk as a keyword index stores it.</summary>
/// <param name="ChunkKey">The chunk's string key.</param>
/// <param name="DocumentId">The document id.</param>
/// <param name="Partition">The partition key; empty for the common area.</param>
/// <param name="Text">The text to index: the chunk's heading path, then its text, as they are embedded.</param>
/// <param name="Metadata">Declared metadata values, by field name.</param>
public sealed record KeywordChunk(string ChunkKey, string DocumentId, string Partition, string Text, IReadOnlyDictionary<string, object?> Metadata);
