namespace RagDotNet.Retrieval;

/// <summary>Reorders retrieval candidates by reading them against the question.</summary>
public interface IReranker
{
    /// <summary>Scores the candidates and returns the best ones, most relevant first.</summary>
    /// <param name="query">The user's question.</param>
    /// <param name="candidates">The fused candidates, best first.</param>
    /// <param name="top">The most results to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Indexes into <paramref name="candidates"/> with scores from 0 to 1; indexes must be distinct.</returns>
    Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken);
}

/// <summary>One reranked candidate.</summary>
/// <param name="Index">The candidate's index in the list passed to <see cref="IReranker.RerankAsync"/>.</param>
/// <param name="Score">Relevance from 0 to 1; higher is more relevant.</param>
public sealed record RerankResult(int Index, double Score);
