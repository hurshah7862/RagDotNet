namespace RagDotNet.Keyword;

/// <summary>A keyword search hit.</summary>
/// <param name="ChunkKey">The chunk's string key.</param>
/// <param name="Score">The relevance score (BM25 for the built-in indexes); higher is better.</param>
public sealed record KeywordHit(string ChunkKey, double Score);
