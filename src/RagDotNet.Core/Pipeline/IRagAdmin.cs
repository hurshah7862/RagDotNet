namespace RagDotNet.Pipeline;

/// <summary>Administrative operations. Not subject to any access scope; keep it away from end users.</summary>
public interface IRagAdmin
{
    /// <summary>
    /// Re-embeds every document not already on the configured model, across all partitions. Safe to rerun after an interruption.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The embedding dimensions are probed first, before anything is written. Reindex then writes a reindex marker naming the
    /// configured model and this engine, and waits one <see cref="Configuration.RagOptions.ModelCheckInterval"/> (at most
    /// 1 minute) before writing any new vector, so other instances see the marker first. The heartbeat is refreshed once 60
    /// seconds have passed since the last refresh, checked after that wait, between embedding batches, after each document
    /// and every 60 seconds while waiting for a document that is being ingested or deleted in this process, so one slow
    /// embedding call delays it. The marker is deleted when every document is on the configured model.
    /// </para>
    /// <para>
    /// While the marker exists, retrieval and questions on pipelines configured with the same model keep working over the
    /// documents already re-embedded only, and ingest and delete keep working. Each document is re-embedded under the same
    /// in-process lock that ingest and delete take, and a document changed by another process during reindex is skipped
    /// rather than overwritten.
    /// </para>
    /// <para>
    /// On failure or cancellation the marker stays in place; calling this method again in the same process resumes at once.
    /// Another process can take the marker over once its heartbeat is more than 5 minutes old.
    /// </para>
    /// </remarks>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reindex counts. Documents skipped because they changed during reindex count as skipped.</returns>
    /// <exception cref="InvalidOperationException">
    /// The embedding generator's dimensions differ from the configured dimensions (nothing is written); a reindex is already
    /// running in this process; another process holds a reindex marker with a heartbeat less than 5 minutes old; or some
    /// documents changed during reindex and are not yet on the configured model (the marker is kept; run it again).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; a marker already written is kept.</exception>
    Task<ReindexResult> ReindexAsync(IProgress<ReindexProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a pipeline bound to an explicit scope, ignoring any registered <see cref="Access.IAccessScope"/>. Use it for writes that must apply a tenant user's read filter and permissions, or pass null for a full-access pipeline, such as for a background worker. The pipeline can ask questions when a chat client is configured.</summary>
    /// <param name="scope">The scope to apply to every operation of the returned pipeline; null gives full access (all partitions, no filter, all permissions).</param>
    /// <returns>A pipeline restricted to <paramref name="scope"/>, or with full access when it is null.</returns>
    /// <exception cref="ArgumentException">The scope fails the validation applied to access scopes: its partition does not match the configured partition keys, a value is empty, or a key is not a declared string field.</exception>
    IRagPipeline ForScope(Access.AccessScope? scope);

    /// <summary>Deletes a document from the named partition, ignoring any access scope. It works during an embedding model mismatch, since it writes no vectors.</summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="partition">The partition values, one per configured partition key; null or empty targets the common area.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the document existed.</returns>
    /// <exception cref="ArgumentException">A non-empty partition does not match the configured partition keys, or a partition value is empty.</exception>
    Task<bool> DeleteDocumentAsync(string documentId, IReadOnlyDictionary<string, string>? partition, CancellationToken cancellationToken = default);

    /// <summary>Reports document and chunk counts per partition and the embedding model ids in the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The statistics.</returns>
    Task<RagStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the keyword index from the stored chunks, for when it has drifted from the vector store (a log warning
    /// says so). Run it when no ingest is in progress: writes made during the rebuild may be lost until the next one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the index is rebuilt.</returns>
    /// <exception cref="InvalidOperationException">The keyword index is not in use (Hybrid is Off, or the vector store's native hybrid search is used).</exception>
    Task RebuildKeywordIndexAsync(CancellationToken cancellationToken = default);
}
