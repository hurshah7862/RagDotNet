using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Ingestion;

namespace RagDotNet.Hosting;

/// <summary>
/// The default queue: a bounded channel worked by <see cref="IngestionQueueOptions.Concurrency"/> loops. Jobs and statuses live in
/// memory, so unfinished jobs are lost on restart. A stream source is copied to a temporary file at enqueue, so the caller can
/// dispose its stream; each attempt reads the copy afresh.
/// </summary>
internal sealed class InMemoryIngestionJobQueue : BackgroundService, IIngestionJobQueue, IAsyncDisposable
{
    private readonly IngestionJobRunner _runner;
    private readonly IngestionQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InMemoryIngestionJobQueue> _logger;
    private readonly Channel<Entry> _channel;
    private readonly ConcurrentDictionary<string, Entry> _jobs = new(StringComparer.Ordinal);

    private readonly object _pauseLock = new();
    private bool _paused;
    private TaskCompletionSource _resumed = Completed();
    private TaskCompletionSource? _idle;
    private int _running;

    private volatile bool _stopped;
    private long _sequence;

    /// <summary>The longest wait between attempts, however large <see cref="IngestionQueueOptions.RetryDelay"/> and the attempt count.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromDays(1);

    public InMemoryIngestionJobQueue(
        IngestionJobRunner runner, IOptions<IngestionQueueOptions> options, TimeProvider time, ILogger<InMemoryIngestionJobQueue> logger)
    {
        _runner = runner;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(_options.Capacity) { FullMode = BoundedChannelFullMode.Wait });
        TempDirectory = Path.Combine(_options.TempDirectory ?? Path.GetTempPath(), "ragdotnet-queue-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>This queue's own directory for stream copies; created on first use, deleted on stop and dispose.</summary>
    internal string TempDirectory { get; }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>
    /// A job as captured at enqueue: no reference to the caller's stream, metadata or partition survives, so the caller's later
    /// changes cannot alter what runs, and a finished job holds no content in memory.
    /// </summary>
    private sealed class Entry(IngestionJob job, string? tempFile, long sequence, IngestionJobStatus status)
    {
        public string JobId { get; } = job.JobId;

        public string DocumentId { get; } = job.Source.DocumentId;

        public AccessScope? Scope { get; } =
            job.Scope is null ? null : job.Scope with { Partition = new Dictionary<string, string>(job.Scope.Partition) };

        public string? FilePath { get; } = tempFile is null ? job.Source.FilePath : null;

        public string FileName { get; } = job.Source.FileName;

        public string? ContentType { get; } = job.Source.ContentType;

        public string? Title { get; } = job.Source.Title;

        public Dictionary<string, object?> Metadata { get; } = new(job.Source.Metadata);

        public string? TempFile { get; } = tempFile;

        /// <summary>Enqueue order, to tell an older job for a document from a newer one.</summary>
        public long Sequence { get; } = sequence;

        /// <summary>The document within its scope's partition: jobs with the same key write the same document.</summary>
        public string DocumentKey { get; } = string.Join(
            "\0",
            (job.Scope?.Partition ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)
                .Append(job.Scope is null ? "*" : "").Append(job.Source.DocumentId));

        public CancellationTokenSource Cancel { get; } = new();

        public object Gate { get; } = new();

        public IngestionJobStatus Status { get; set; } = status;

        public bool Finished => Status.State is IngestionJobState.Succeeded or IngestionJobState.Failed or IngestionJobState.Cancelled;
    }

    public async ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ThrowIfStopped();
        Sweep();

        string? tempFile = null;
        if (job.Source.Stream is { } stream)
        {
            Directory.CreateDirectory(TempDirectory);
            tempFile = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
            try
            {
                var copy = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (copy.ConfigureAwait(false))
                    await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                DeleteFile(tempFile);
                throw;
            }
        }

        var entry = new Entry(job, tempFile, Interlocked.Increment(ref _sequence), new IngestionJobStatus(job.JobId, job.Source.DocumentId, IngestionJobState.Queued, 0, null, null, _time.GetUtcNow()));
        if (!_jobs.TryAdd(job.JobId, entry))
        {
            DeleteFile(tempFile);
            throw new ArgumentException($"A job with id '{job.JobId}' is already queued or known.", nameof(job));
        }

        try
        {
            await _channel.Writer.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _jobs.TryRemove(job.JobId, out _);
            DeleteFile(tempFile);
            if (e is ChannelClosedException) throw Stopped();
            throw;
        }
    }

    public ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        Sweep();
        if (!_jobs.TryGetValue(jobId, out var entry)) return new((IngestionJobStatus?)null);
        lock (entry.Gate) return new(entry.Status);
    }

    public ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!_jobs.TryGetValue(jobId, out var entry)) return new(false);
        lock (entry.Gate)
        {
            if (entry.Finished) return new(false);
            entry.Cancel.Cancel();
            if (entry.Status.State == IngestionJobState.Queued)
            {
                // Not running, so nothing else touches the copy: the worker checks the state under this lock before an attempt.
                SetStatus(entry, IngestionJobState.Cancelled);
                DeleteFile(entry.TempFile);
            }
        }

        return new(true);
    }

    public async ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_pauseLock)
        {
            if (!_paused)
            {
                _paused = true;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (_running == 0) return;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _idle.Task;
        }

        await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_pauseLock)
        {
            if (_paused)
            {
                _paused = false;
                _resumed.TrySetResult();
            }
        }

        return default;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _options.Concurrency).Select(_ => Task.Run(() => WorkAsync(stoppingToken), CancellationToken.None)));

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await ProcessAsync(entry, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(Entry entry, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancel.Token, stoppingToken);
        var ct = linked.Token;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (!await BeginAttemptAsync(entry, attempt, ct).ConfigureAwait(false)) return;

                Exception? error = null;
                try
                {
                    var result = await RunAttemptAsync(entry, ct).ConfigureAwait(false);
                    lock (entry.Gate) SetStatus(entry, IngestionJobState.Succeeded, result: result, error: null);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lock (entry.Gate) SetStatus(entry, IngestionJobState.Cancelled);
                    return;
                }
#pragma warning disable CA1031 // Any error ends the attempt; it is classified and recorded in the job's status.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    error = e;
                }
                finally
                {
                    EndAttempt();
                }

                var message = $"{error.GetType().Name}: {error.Message}";
                if (attempt >= _options.MaxAttempts || !IsTransient(entry, error))
                {
                    lock (entry.Gate) SetStatus(entry, IngestionJobState.Failed, error: message);
                    _logger.LogError(
                        "Ingestion job {JobId} for document {DocumentId} failed after {Attempts} attempt(s) with {ExceptionType}.",
                        entry.JobId, entry.DocumentId, attempt, error.GetType().Name);
                    return;
                }

                var delay = TimeSpan.FromTicks((long)Math.Min(_options.RetryDelay.Ticks * Math.Pow(2, attempt - 1), MaxRetryDelay.Ticks));
                _logger.LogWarning(
                    "Ingestion job {JobId} for document {DocumentId} failed attempt {Attempt} with {ExceptionType}; retrying in {Delay}.",
                    entry.JobId, entry.DocumentId, attempt, error.GetType().Name, delay);

                // Start the timer before publishing Queued, so whoever sees the status sees a wait already under way.
                var wait = Task.Delay(delay, _time, ct);
                lock (entry.Gate)
                {
                    if (entry.Finished) return;
                    SetStatus(entry, IngestionJobState.Queued, error: message);
                }

                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lock (entry.Gate) SetStatus(entry, IngestionJobState.Cancelled);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (entry.Gate) SetStatus(entry, IngestionJobState.Cancelled);
        }
#pragma warning disable CA1031 // Nothing may end a worker loop: an unexpected error fails this job, and the worker takes the next one.
        catch (Exception e)
#pragma warning restore CA1031
        {
            lock (entry.Gate) SetStatus(entry, IngestionJobState.Failed, error: $"{e.GetType().Name}: {e.Message}");
            _logger.LogError(
                "Ingestion job {JobId} for document {DocumentId} failed with {ExceptionType} between attempts.",
                entry.JobId, entry.DocumentId, e.GetType().Name);
        }
        finally
        {
            DeleteFile(entry.TempFile);
        }
    }

    /// <summary>The runner's classification; a classifier that throws makes the error permanent rather than ending the worker.</summary>
    private bool IsTransient(Entry entry, Exception error)
    {
        try
        {
            return _runner.IsTransient(error);
        }
#pragma warning disable CA1031 // The app's IsTransient delegate threw; the job fails with its own error instead.
        catch (Exception e)
#pragma warning restore CA1031
        {
            _logger.LogError(
                "The IsTransient check threw {ExceptionType} for ingestion job {JobId}; the job's error is treated as permanent.",
                e.GetType().Name, entry.JobId);
            return false;
        }
    }

    /// <summary>A job for the same document, enqueued after this one, that has already succeeded; its content must not be overwritten.</summary>
    private string? NewerSucceeded(Entry entry)
    {
        foreach (var other in _jobs.Values)
        {
            if (other.Sequence > entry.Sequence && other.DocumentKey == entry.DocumentKey && other.Status.State == IngestionJobState.Succeeded)
                return other.JobId;
        }

        return null;
    }

    /// <summary>Waits for the pause gate, then marks the job running. False when the job was cancelled first.</summary>
    private async Task<bool> BeginAttemptAsync(Entry entry, int attempt, CancellationToken ct)
    {
        while (true)
        {
            Task resumed;
            lock (_pauseLock)
            {
                if (!_paused)
                {
                    lock (entry.Gate)
                    {
                        if (entry.Finished) return false;
                        if (ct.IsCancellationRequested)
                        {
                            SetStatus(entry, IngestionJobState.Cancelled);
                            return false;
                        }

                        if (NewerSucceeded(entry) is { } newer)
                        {
                            SetStatus(entry, IngestionJobState.Cancelled,
                                error: $"Superseded: job {newer}, enqueued later for the same document, has already succeeded.");
                            DeleteFile(entry.TempFile);
                            return false;
                        }

                        _running++;
                        SetStatus(entry, IngestionJobState.Running, attempts: attempt);
                        return true;
                    }
                }

                resumed = _resumed.Task;
            }

            try
            {
                await resumed.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (entry.Gate) SetStatus(entry, IngestionJobState.Cancelled);
                return false;
            }
        }
    }

    private void EndAttempt()
    {
        lock (_pauseLock)
        {
            _running--;
            if (_running == 0 && _idle is not null)
            {
                _idle.TrySetResult();
                _idle = null;
            }
        }
    }

    /// <summary>Runs one attempt from the snapshot: a fresh source each time, reading the copy (or the original file) afresh.</summary>
    private async Task<IngestResult> RunAttemptAsync(Entry entry, CancellationToken ct)
    {
        var stream = entry.TempFile is null
            ? null
            : new FileStream(entry.TempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            var source = stream is null
                ? DocumentSource.FromFile(entry.FilePath!, entry.DocumentId)
                : new DocumentSource(entry.DocumentId, stream, entry.FileName, entry.ContentType);
            source.Title = entry.Title;
            foreach (var (key, value) in entry.Metadata) source.Metadata[key] = value;
            return await _runner.RunAsync(new IngestionJob(entry.JobId, source, entry.Scope), ct).ConfigureAwait(false);
        }
        finally
        {
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Sets a job's status; the caller holds <see cref="Entry.Gate"/>. A finished status never changes.</summary>
    private void SetStatus(
        Entry entry, IngestionJobState state, int? attempts = null, IngestResult? result = null, string? error = null)
    {
        if (entry.Finished) return;
        var current = entry.Status;
        entry.Status = current with
        {
            State = state,
            Attempts = attempts ?? current.Attempts,
            Result = result,
            Error = state == IngestionJobState.Succeeded ? null : error ?? current.Error,
            UpdatedAt = _time.GetUtcNow(),
        };
    }

    /// <summary>Drops finished statuses older than the retention period.</summary>
    private void Sweep()
    {
        var cutoff = _time.GetUtcNow() - _options.StatusRetention;
        foreach (var (id, entry) in _jobs)
        {
            bool expired;
            lock (entry.Gate) expired = entry.Finished && entry.Status.UpdatedAt <= cutoff;
            if (expired) _jobs.TryRemove(new KeyValuePair<string, Entry>(id, entry));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Jobs still in the channel never ran: they are lost, like everything in memory.
        while (_channel.Reader.TryRead(out var entry))
        {
            lock (entry.Gate) SetStatus(entry, IngestionJobState.Cancelled);
        }

        DeleteTempDirectory();
    }

    public override void Dispose()
    {
        base.Dispose();
        DeleteTempDirectory();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private void ThrowIfStopped()
    {
        if (_stopped) throw Stopped();
    }

    private static InvalidOperationException Stopped() => new("The ingestion queue has stopped.");

    private void DeleteTempDirectory()
    {
        try
        {
            Directory.Delete(TempDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException e)
        {
            _logger.LogWarning("Could not delete the ingestion queue's temporary directory {Directory}: {Reason}", TempDirectory, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogWarning("Could not delete the ingestion queue's temporary directory {Directory}: {Reason}", TempDirectory, e.Message);
        }
    }

    private void DeleteFile(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException e)
        {
            _logger.LogWarning("Could not delete the temporary copy {File}: {Reason}", path, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogWarning("Could not delete the temporary copy {File}: {Reason}", path, e.Message);
        }
    }
}
