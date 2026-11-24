using Microsoft.Extensions.Options;
using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Embedding;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;

namespace RagDotNet.Hosting;

/// <summary>Runs ingestion jobs under their stored scope, and decides which errors are worth retrying. Every queue, durable or not, runs its jobs with it.</summary>
/// <param name="admin">The admin service, which gives the pipeline for a job's scope.</param>
/// <param name="options">The queue options.</param>
public sealed class IngestionJobRunner(IRagAdmin admin, IOptions<IngestionQueueOptions> options)
{
    /// <summary>Runs one attempt of a job: ingests its source under its scope (full access when the scope is null), ignoring any ambient scope.</summary>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ingest result.</returns>
    public Task<IngestResult> RunAsync(IngestionJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        return admin.ForScope(job.Scope).IngestAsync(job.Source, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Whether an attempt's error is worth retrying. <see cref="IngestionQueueOptions.IsTransient"/> decides when set. Otherwise these
    /// are permanent: <see cref="UnsupportedDocumentException"/>, <see cref="DocumentReadException"/>,
    /// <see cref="EmbeddingModelMismatchException"/>, <see cref="RagAccessDeniedException"/>, <see cref="ArgumentException"/>,
    /// <see cref="InvalidOperationException"/>, <see cref="FileNotFoundException"/>, <see cref="DirectoryNotFoundException"/> and
    /// <see cref="UnauthorizedAccessException"/>; everything else, such as a provider's rate limit or a store that is down, is transient.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns>True to retry.</returns>
    public bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (options.Value.IsTransient is { } decide) return decide(exception);
        return exception is not (UnsupportedDocumentException or DocumentReadException or EmbeddingModelMismatchException
            or RagAccessDeniedException or ArgumentException or InvalidOperationException or FileNotFoundException
            or DirectoryNotFoundException or UnauthorizedAccessException);
    }
}
