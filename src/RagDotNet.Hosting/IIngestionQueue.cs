using RagDotNet.Documents;

namespace RagDotNet.Hosting;

/// <summary>
/// Ingests documents in the background. Scoped, like <c>IRagPipeline</c>: each job runs under the access scope of the caller that
/// enqueued it, captured at enqueue time. A background service creates a DI scope before resolving it.
/// </summary>
public interface IIngestionQueue
{
    /// <summary>
    /// Queues a document. The caller's scope is checked now: a scope that fails validation or lacks
    /// <see cref="Access.RagPermissions.Write"/> is refused, and nothing is queued. It waits for room when the queue is full.
    /// </summary>
    /// <param name="source">The document. The default queue copies a stream source, so the caller may dispose the stream once this returns.</param>
    /// <param name="cancellationToken">Cancels the wait for room; nothing is queued when it is cancelled.</param>
    /// <returns>The job id, for <see cref="GetStatusAsync"/> and <see cref="CancelAsync"/>.</returns>
    /// <exception cref="ArgumentException">The caller's scope fails access-scope validation.</exception>
    /// <exception cref="Access.RagAccessDeniedException">The caller's scope does not grant Write.</exception>
    /// <exception cref="InvalidOperationException">The queue has stopped.</exception>
    ValueTask<string> EnqueueAsync(DocumentSource source, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IIngestionJobQueue.GetStatusAsync"/>
    ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IIngestionJobQueue.CancelAsync"/>
    ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IIngestionJobQueue.PauseAsync"/>
    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IIngestionJobQueue.ResumeAsync"/>
    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}
