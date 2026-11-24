namespace RagDotNet.Hosting;

/// <summary>
/// The queue behind <c>IIngestionQueue</c>, which holds and runs ingestion jobs. The default keeps jobs in memory; register
/// your own implementation before <c>AddIngestionQueue</c> to keep jobs across restarts.
/// </summary>
/// <remarks>
/// Every implementation, durable or not, keeps this contract: it stores each job's <see cref="IngestionJob.Source"/> content and
/// <see cref="IngestionJob.Scope"/> at enqueue time (<c>RagJson.Options</c> serializes the scope), runs each attempt with
/// <see cref="IngestionJobRunner.RunAsync"/>, and retries only when <see cref="IngestionJobRunner.IsTransient"/> returns true.
/// The caller may dispose the source's stream as soon as <see cref="EnqueueAsync"/> returns.
/// </remarks>
public interface IIngestionJobQueue
{
    /// <summary>Adds a job. It waits for room when the queue is full.</summary>
    /// <param name="job">The job; its id must be new to this queue.</param>
    /// <param name="cancellationToken">Cancels the wait for room; nothing is queued when it is cancelled.</param>
    /// <returns>A task that completes once the job is queued.</returns>
    /// <exception cref="ArgumentException">A job with the same id is already known.</exception>
    /// <exception cref="InvalidOperationException">The queue has stopped.</exception>
    ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default);

    /// <summary>Gets a job's status.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status, or null for an unknown job or one whose finished status has expired.</returns>
    ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Cancels a job: a queued one never runs, and a running one is stopped and not retried. Either ends <see cref="IngestionJobState.Cancelled"/>.</summary>
    /// <param name="jobId">The job id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the job was queued or running; false if it had finished or is unknown.</returns>
    ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Stops new attempts from starting, and returns once no attempt is running. Queued jobs are kept, and enqueuing still works.</summary>
    /// <param name="cancellationToken">Cancels the wait for running attempts; the queue stays paused.</param>
    /// <returns>A task that completes when nothing is being ingested.</returns>
    ValueTask PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets queued jobs run again after <see cref="PauseAsync"/>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A completed task.</returns>
    ValueTask ResumeAsync(CancellationToken cancellationToken = default);
}
