using RagDotNet.Chunking;
using RagDotNet.Evaluation.Scoring;

namespace RagDotNet.Evaluation.Running;

/// <summary>One point of the configuration matrix.</summary>
/// <param name="Chunker">Chunker name.</param>
/// <param name="MaxTokens">Maximum tokens per chunk.</param>
/// <param name="Overlap">Overlap ratio.</param>
/// <param name="Mode">Query mode: <c>vector</c>, <c>multi-query</c> or <c>hyde</c>.</param>
/// <param name="Retrieval">Retrieval: <c>vector</c>, <c>hybrid</c> or <c>hybrid-rerank</c>.</param>
public sealed record EvaluationConfig(string Chunker, int MaxTokens, double Overlap, string Mode, string Retrieval = EvaluationConfig.RetrievalVector)
{
    /// <summary>Vector search only (<c>HybridMode.Off</c>).</summary>
    public const string RetrievalVector = "vector";

    /// <summary>Vector and keyword search fused with RRF, through the in-memory keyword index.</summary>
    public const string RetrievalHybrid = "hybrid";

    /// <summary>Hybrid, then a reranker over the fused candidates.</summary>
    public const string RetrievalHybridRerank = "hybrid-rerank";

    /// <summary>All supported retrievals.</summary>
    public static IReadOnlyList<string> Retrievals { get; } = [RetrievalVector, RetrievalHybrid, RetrievalHybridRerank];

    /// <summary>Plain vector search.</summary>
    public const string Vector = "vector";

    /// <summary>Vector search over the question and 3 rephrasings, fused.</summary>
    public const string MultiQuery = "multi-query";

    /// <summary>Vector search over a hypothetical answer.</summary>
    public const string Hyde = "hyde";

    private static readonly IReadOnlyList<double> NoOverlap = [0];

    /// <summary>Whether the overlap setting means anything for this configuration's chunker.</summary>
    public bool UsesOverlap => Chunker != Chunkers.Semantic;

    /// <summary>All supported modes.</summary>
    public static IReadOnlyList<string> Modes { get; } = [Vector, MultiQuery, Hyde];

    /// <summary>
    /// Every combination, in nested order chunker → MaxTokens → overlap → mode → retrieval. The semantic chunker ignores
    /// overlap, so it runs once per MaxTokens with overlap 0 whatever <paramref name="overlaps"/> holds. Retrieval defaults to vector.
    /// </summary>
    public static IReadOnlyList<EvaluationConfig> Matrix(
        IReadOnlyList<string> chunkers, IReadOnlyList<int> maxTokens, IReadOnlyList<double> overlaps, IReadOnlyList<string> modes,
        IReadOnlyList<string>? retrievals = null) =>
        [
            .. from c in chunkers
               from t in maxTokens
               from o in c == Chunkers.Semantic ? NoOverlap : overlaps
               from m in modes
               from r in retrievals ?? [RetrievalVector]
               select new EvaluationConfig(c, t, o, m, r),
        ];
}

/// <summary>The scores of one configuration on one dataset set.</summary>
/// <param name="Set">The dataset set's name.</param>
/// <param name="Config">The configuration.</param>
/// <param name="ChunkCount">Total chunks the set's corpus produced.</param>
/// <param name="Scores">Recall and MRR.</param>
/// <param name="Ranks">Rank of the first relevant chunk per question, in question order.</param>
public sealed record ConfigResult(string Set, EvaluationConfig Config, int ChunkCount, ScoreSummary Scores, IReadOnlyList<int?> Ranks);
