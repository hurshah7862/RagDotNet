using RagDotNet.Chunking;

namespace RagDotNet.Ingestion;

/// <summary>Per-call ingestion options.</summary>
public sealed record IngestOptions
{
    /// <summary>Chunking options for this call; overrides configured defaults when set.</summary>
    public ChunkingOptions? Chunking { get; init; }
}
