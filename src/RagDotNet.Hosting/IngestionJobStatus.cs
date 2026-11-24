using RagDotNet.Ingestion;

namespace RagDotNet.Hosting;

/// <summary>Where an ingestion job is in its life.</summary>
public enum IngestionJobState
{
    /// <summary>Waiting to run, or waiting out a retry delay after a transient error.</summary>
    Queued,

    /// <summary>An attempt is running.</summary>
    Running,

    /// <summary>Ingested; <see cref="IngestionJobStatus.Result"/> holds the result.</summary>
    Succeeded,

    /// <summary>Failed with a permanent error, or with a transient one after the last attempt; <see cref="IngestionJobStatus.Error"/> says why.</summary>
    Failed,

    /// <summary>Cancelled before it finished.</summary>
    Cancelled,
}

/// <summary>A snapshot of an ingestion job's status.</summary>
/// <param name="JobId">The job's id.</param>
/// <param name="DocumentId">The id of the document the job ingests.</param>
/// <param name="State">The job's state.</param>
/// <param name="Attempts">How many attempts have started.</param>
/// <param name="Result">The ingest result, once the job has succeeded.</param>
/// <param name="Error">The last attempt's error, as <c>"{ExceptionType}: {Message}"</c>; null when no attempt has failed.</param>
/// <param name="UpdatedAt">When the status last changed.</param>
public sealed record IngestionJobStatus(
    string JobId, string DocumentId, IngestionJobState State, int Attempts, IngestResult? Result, string? Error, DateTimeOffset UpdatedAt);
