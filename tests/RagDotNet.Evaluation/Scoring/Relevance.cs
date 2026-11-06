using RagDotNet.Evaluation.Datasets;
using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Scoring;

/// <summary>Decides whether a retrieved chunk answers a question.</summary>
public static class Relevance
{
    /// <summary>
    /// True when the chunk belongs to the question's document and its normalized text contains the normalized evidence.
    /// </summary>
    public static bool IsRelevant(RetrievedChunk chunk, Question q)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(q);
        return string.Equals(chunk.DocumentId, q.DocumentId, StringComparison.Ordinal)
            && TextNormalizer.Normalize(chunk.Text).Contains(TextNormalizer.Normalize(q.Evidence), StringComparison.Ordinal);
    }
}
