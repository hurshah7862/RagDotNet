using RagDotNet.Evaluation.Datasets;
using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Scoring;

/// <summary>Recall@k and mean reciprocal rank over a set of questions.</summary>
/// <param name="RecallAt1">Share of questions whose first relevant chunk is at rank 1.</param>
/// <param name="RecallAt3">Share of questions with a relevant chunk in the top 3.</param>
/// <param name="RecallAt5">Share of questions with a relevant chunk in the top 5.</param>
/// <param name="Mrr">Mean of 1/rank, counting 0 when nothing relevant was retrieved.</param>
public sealed record ScoreSummary(double RecallAt1, double RecallAt3, double RecallAt5, double Mrr);

/// <summary>Ranks and retrieval metrics.</summary>
public static class Metrics
{
    /// <summary>The 1-based position of the first relevant chunk, or null when none is relevant.</summary>
    public static int? RankOfFirstRelevant(IReadOnlyList<RetrievedChunk> results, Question q)
    {
        ArgumentNullException.ThrowIfNull(results);
        for (var i = 0; i < results.Count; i++)
        {
            if (Relevance.IsRelevant(results[i], q))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>Summarizes the ranks of a set of questions.</summary>
    /// <exception cref="ArgumentException"><paramref name="ranks"/> is empty.</exception>
    public static ScoreSummary Summarize(IReadOnlyList<int?> ranks)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        if (ranks.Count == 0)
        {
            throw new ArgumentException("At least one rank is needed.", nameof(ranks));
        }

        double n = ranks.Count;
        double Recall(int k) => ranks.Count(r => r <= k) / n;
        return new ScoreSummary(Recall(1), Recall(3), Recall(5), ranks.Sum(r => r is { } v ? 1.0 / v : 0) / n);
    }
}
