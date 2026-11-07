namespace RagDotNet.Chunking;

/// <summary>Options controlling chunking.</summary>
public sealed record ChunkingOptions
{
    /// <summary>The name of the chunker to use: a built-in name from <see cref="Chunkers"/> or one registered with <c>AddChunker</c>.</summary>
    public string Chunker { get; init; } = Chunkers.StructureAware;

    /// <summary>Maximum tokens per chunk; at least 16.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>Fraction of <see cref="MaxTokens"/> repeated at the start of the next chunk; in [0, 0.5).</summary>
    public double OverlapRatio { get; init; } = 0.10;

    /// <summary>
    /// For the <see cref="Chunkers.Semantic"/> chunker: the percentile of neighbouring-sentence distances above which a
    /// new chunk starts; in (0, 100]. Higher values make fewer, larger chunks. Other chunkers ignore it.
    /// </summary>
    public double BreakpointPercentile { get; init; } = 95;
}
