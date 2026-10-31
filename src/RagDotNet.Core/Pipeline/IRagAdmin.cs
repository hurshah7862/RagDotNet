namespace RagDotNet.Pipeline;

/// <summary>Administrative operations. Not subject to any access scope; keep it away from end users.</summary>
public interface IRagAdmin
{
    /// <summary>Re-embeds every document not already on the configured model, across all partitions. Safe to rerun after an interruption.</summary>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reindex counts.</returns>
    /// <exception cref="InvalidOperationException">The embedding generator's dimensions differ from the configured dimensions.</exception>
    Task<ReindexResult> ReindexAsync(IProgress<ReindexProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a document from the named partition, ignoring any access scope. It works during an embedding model mismatch, since it writes no vectors.</summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="partition">The partition values, one per configured partition key; null or empty only when no partition keys are configured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the document existed.</returns>
    /// <exception cref="ArgumentException">The partition keys do not match the configured partition keys, including a null or empty partition when partition keys are configured.</exception>
    Task<bool> DeleteDocumentAsync(string documentId, IReadOnlyDictionary<string, string>? partition, CancellationToken cancellationToken = default);

    /// <summary>Reports document and chunk counts per partition and the embedding model ids in the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statistics.</returns>
    Task<RagStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
