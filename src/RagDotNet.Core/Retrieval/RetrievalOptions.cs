using RagDotNet.Filtering;

namespace RagDotNet.Retrieval;

/// <summary>Per-call retrieval options.</summary>
public sealed record RetrievalOptions
{
    /// <summary>Maximum number of chunks to return, from 1 to 100. Defaults to 5.</summary>
    public int TopK { get; init; } = 5;

    /// <summary>Drops results scoring below this value. Null keeps everything.</summary>
    public double? MinScore { get; init; }

    /// <summary>Restricts results to chunks matching the filter.</summary>
    public RagFilter? Filter { get; init; }
}
