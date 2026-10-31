namespace RagDotNet.Retrieval;

/// <summary>A chunk returned by retrieval.</summary>
/// <param name="ChunkKey">The string key of the chunk record.</param>
/// <param name="DocumentId">The document id.</param>
/// <param name="ChunkIndex">The chunk's position within its document.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Score">The relevance score; higher is better.</param>
/// <param name="ScoreKind">How the score was produced.</param>
/// <param name="PageStart">The first page, when known.</param>
/// <param name="PageEnd">The last page, when known.</param>
/// <param name="HeadingPath">The headings enclosing the chunk, outermost first.</param>
/// <param name="SourceName">The source name of the document.</param>
/// <param name="Metadata">Declared metadata, keyed by field name.</param>
public sealed record RetrievedChunk(
    string ChunkKey,
    string DocumentId,
    int ChunkIndex,
    string Text,
    double Score,
    ScoreKind ScoreKind,
    int? PageStart,
    int? PageEnd,
    IReadOnlyList<string> HeadingPath,
    string SourceName,
    IReadOnlyDictionary<string, object?> Metadata);
