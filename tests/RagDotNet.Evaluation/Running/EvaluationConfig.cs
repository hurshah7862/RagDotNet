using RagDotNet.Evaluation.Scoring;

namespace RagDotNet.Evaluation.Running;

/// <summary>One point of the configuration matrix.</summary>
/// <param name="Chunker">Chunker name.</param>
/// <param name="MaxTokens">Maximum tokens per chunk.</param>
/// <param name="Overlap">Overlap ratio.</param>
/// <param name="Mode">Retrieval mode: <c>vector</c>, <c>multi-query</c> or <c>hyde</c>.</param>
public sealed record EvaluationConfig(string Chunker, int MaxTokens, double Overlap, string Mode)
{
    /// <summary>Plain vector search.</summary>
    public const string Vector = "vector";

    /// <summary>Vector search over the question and 3 rephrasings, fused.</summary>
    public const string MultiQuery = "multi-query";

    /// <summary>Vector search over a hypothetical answer.</summary>
    public const string Hyde = "hyde";

    /// <summary>All supported modes.</summary>
    public static IReadOnlyList<string> Modes { get; } = [Vector, MultiQuery, Hyde];

    /// <summary>Every combination, in nested order chunker → MaxTokens → overlap → mode.</summary>
    public static IReadOnlyList<EvaluationConfig> Matrix(
        IReadOnlyList<string> chunkers, IReadOnlyList<int> maxTokens, IReadOnlyList<double> overlaps, IReadOnlyList<string> modes) =>
        [
            .. from c in chunkers
               from t in maxTokens
               from o in overlaps
               from m in modes
               select new EvaluationConfig(c, t, o, m),
        ];
}

/// <summary>The scores of one configuration.</summary>
/// <param name="Config">The configuration.</param>
/// <param name="ChunkCount">Total chunks the corpus produced.</param>
/// <param name="Scores">Recall and MRR.</param>
/// <param name="Ranks">Rank of the first relevant chunk per question, in question order.</param>
public sealed record ConfigResult(EvaluationConfig Config, int ChunkCount, ScoreSummary Scores, IReadOnlyList<int?> Ranks);
