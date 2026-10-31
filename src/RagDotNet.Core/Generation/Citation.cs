using RagDotNet.Retrieval;

namespace RagDotNet.Generation;

/// <summary>A source the answer cited.</summary>
/// <param name="Number">The marker number used in the answer, starting at 1.</param>
/// <param name="Chunk">The cited chunk.</param>
/// <param name="Location">A page range, heading path, or source name locating the chunk.</param>
public sealed record Citation(int Number, RetrievedChunk Chunk, string Location);
