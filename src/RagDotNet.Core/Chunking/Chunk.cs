namespace RagDotNet.Chunking;

/// <summary>A retrievable slice of a document.</summary>
/// <param name="Index">Zero-based position of the chunk in the document.</param>
/// <param name="Text">Chunk text.</param>
/// <param name="HeadingPath">Headings enclosing the chunk.</param>
/// <param name="PageStart">First page the chunk covers, when known.</param>
/// <param name="PageEnd">Last page the chunk covers, when known.</param>
/// <param name="TokenCount">Number of tokens in <paramref name="Text"/>.</param>
public sealed record Chunk(int Index, string Text, IReadOnlyList<string> HeadingPath, int? PageStart, int? PageEnd, int TokenCount);
