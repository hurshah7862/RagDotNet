using RagDotNet.Retrieval;

namespace RagDotNet.Evaluation.Providers;

/// <summary>
/// A deterministic reranker for fake mode: it keeps the fused order and gives descending scores from 1. It only proves the
/// <c>hybrid-rerank</c> wiring; its scores mean nothing.
/// </summary>
public sealed class FakeReranker : IReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        IReadOnlyList<RerankResult> results = [.. Enumerable.Range(0, Math.Min(top, candidates.Count)).Select(i => new RerankResult(i, 1.0 - (double)i / candidates.Count))];
        return Task.FromResult(results);
    }
}
