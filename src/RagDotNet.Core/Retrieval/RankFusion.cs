namespace RagDotNet.Retrieval;

/// <summary>Combines several ranked result lists into one.</summary>
internal static class RankFusion
{
    /// <summary>The reciprocal rank fusion constant.</summary>
    public const int K = 60;

    /// <summary>
    /// Reciprocal rank fusion: each chunk scores the sum of <c>1 / (K + rank)</c> over the rankings that contain it, with rank starting at 1.
    /// Chunks are de-duplicated by <see cref="RetrievedChunk.ChunkKey"/>, keeping the first record seen. Ordered by score descending,
    /// then <c>DocumentId</c> (ordinal), then <c>ChunkIndex</c>. The top <paramref name="topK"/> are returned with <see cref="ScoreKind.Fused"/>.
    /// </summary>
    public static IReadOnlyList<RetrievedChunk> Reciprocal(IReadOnlyList<IReadOnlyList<RetrievedChunk>> rankings, int topK)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var first = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);
        foreach (var ranking in rankings)
        {
            for (var i = 0; i < ranking.Count; i++)
            {
                var chunk = ranking[i];
                scores[chunk.ChunkKey] = scores.GetValueOrDefault(chunk.ChunkKey) + 1.0 / (K + i + 1);
                first.TryAdd(chunk.ChunkKey, chunk);
            }
        }

        var fused = first.Select(kv => kv.Value with { Score = scores[kv.Key], ScoreKind = ScoreKind.Fused }).ToList();
        fused.Sort(Compare);
        return fused.Count > topK ? fused.GetRange(0, topK) : fused;
    }

    /// <summary>Score descending, then DocumentId (ordinal), then ChunkIndex.</summary>
    public static int Compare(RetrievedChunk a, RetrievedChunk b)
    {
        var c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.DocumentId, b.DocumentId);
        return c != 0 ? c : a.ChunkIndex.CompareTo(b.ChunkIndex);
    }
}
