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

    /// <summary>
    /// Whether the common area (documents not belonging to any partition) is searched together with the caller's own partition. Defaults to true.
    /// With full access, false limits the search to tenant partitions. Has no effect when no partition keys are configured.
    /// </summary>
    public bool IncludeCommon { get; init; } = true;
}
