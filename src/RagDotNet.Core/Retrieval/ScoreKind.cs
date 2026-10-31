namespace RagDotNet.Retrieval;

/// <summary>Describes how a retrieval score was produced.</summary>
public enum ScoreKind
{
    /// <summary>Vector similarity; higher is more similar.</summary>
    Similarity,

    /// <summary>A fused score from several rankings.</summary>
    Fused,

    /// <summary>A reranker score.</summary>
    Rerank,
}
