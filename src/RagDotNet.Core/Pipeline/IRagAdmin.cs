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

    /// <summary>Creates a pipeline bound to an explicit scope, ignoring any registered <see cref="Access.IAccessScope"/>. Use it for writes that must apply a tenant user's read filter and permissions. The pipeline can ask questions when a chat client is configured.</summary>
    /// <param name="scope">The scope to apply to every operation of the returned pipeline.</param>
    /// <returns>A pipeline restricted to <paramref name="scope"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    /// <exception cref="ArgumentException">The scope fails the validation applied to access scopes: its partition does not match the configured partition keys, a value is empty, or a key is not a declared string field.</exception>
    IRagPipeline ForScope(Access.AccessScope scope);

    /// <summary>Deletes a document from the named partition, ignoring any access scope. It works during an embedding model mismatch, since it writes no vectors.</summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="partition">The partition values, one per configured partition key; null or empty targets the common area.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the document existed.</returns>
    /// <exception cref="ArgumentException">A non-empty partition does not match the configured partition keys.</exception>
    Task<bool> DeleteDocumentAsync(string documentId, IReadOnlyDictionary<string, string>? partition, CancellationToken cancellationToken = default);

    /// <summary>Reports document and chunk counts per partition and the embedding model ids in the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statistics.</returns>
    Task<RagStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
