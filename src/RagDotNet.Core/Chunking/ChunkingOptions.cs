namespace RagDotNet.Chunking;

/// <summary>Options controlling chunking.</summary>
public sealed record ChunkingOptions
{
    /// <summary>The chunking strategy.</summary>
    public ChunkingStrategy Strategy { get; init; } = ChunkingStrategy.StructureAware;

    /// <summary>Maximum tokens per chunk; at least 16.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>Fraction of <see cref="MaxTokens"/> repeated at the start of the next chunk; in [0, 0.5).</summary>
    public double OverlapRatio { get; init; } = 0.10;
}
