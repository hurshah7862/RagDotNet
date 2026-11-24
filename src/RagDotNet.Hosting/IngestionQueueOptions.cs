namespace RagDotNet.Hosting;

/// <summary>Options for the background ingestion queue.</summary>
public sealed class IngestionQueueOptions
{
    /// <summary>How many jobs the queue holds before <c>EnqueueAsync</c> waits for room. Default 1000.</summary>
    public int Capacity { get; set; } = 1000;

    /// <summary>How many jobs run at once. Default 2.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>How many attempts a job gets when its errors are transient. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The wait before the first retry; it doubles before each later one. Default 1 second.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a finished job's status is kept. Default 1 hour.</summary>
    public TimeSpan StatusRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Where enqueued streams are copied; default <see cref="Path.GetTempPath"/>. Each queue uses its own subdirectory.</summary>
    public string? TempDirectory { get; set; }

    /// <summary>Decides whether an error is worth retrying, replacing the default classification (see <see cref="IngestionJobRunner.IsTransient"/>).</summary>
    public Func<Exception, bool>? IsTransient { get; set; }
}
