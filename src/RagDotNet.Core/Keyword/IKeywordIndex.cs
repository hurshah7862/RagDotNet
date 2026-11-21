namespace RagDotNet.Keyword;

/// <summary>
/// A full-text index of chunk text, used for hybrid search when the vector store has no native hybrid search.
/// RagDotNet keeps it in step with the vector store through ingest, update and delete.
/// </summary>
public interface IKeywordIndex
{
    /// <summary>A short name for logs and telemetry, such as "in-memory" or "lucene".</summary>
    string Name { get; }

    /// <summary>Adds chunks, replacing any with the same key.</summary>
    /// <param name="chunks">The chunks.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the chunks are searchable.</returns>
    Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken cancellationToken);

    /// <summary>Removes chunks by key; unknown keys are ignored.</summary>
    /// <param name="chunkKeys">The chunk keys.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the chunks are removed.</returns>
    Task DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken cancellationToken);

    /// <summary>Finds the chunks that best match the query text and satisfy the filter.</summary>
    /// <param name="query">Plain query text; no query syntax.</param>
    /// <param name="filter">The partition and metadata constraint.</param>
    /// <param name="top">The most hits to return.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Hits, best first.</returns>
    Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken cancellationToken);

    /// <summary>The number of chunks in the index.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The chunk count.</returns>
    Task<long> CountAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the whole index with the given chunks.</summary>
    /// <param name="source">Every chunk.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the index holds exactly <paramref name="source"/>.</returns>
    Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken cancellationToken);
}
