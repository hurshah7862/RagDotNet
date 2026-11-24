# Background ingestion (RagDotNet.Hosting)

`RagDotNet.Hosting` ingests documents in the background. An app enqueues a document, gets a job id back straight away, and checks the job's status later. Ingesting a large PDF can take a while, because every chunk is embedded; with the queue, the request that uploaded the file does not wait for that.

## Setup

```csharp
builder.Services.AddRag(o => { /* ... */ })
    .AddIngestionQueue(o => o.Concurrency = 4);
```

`AddIngestionQueue` registers `IIngestionQueue` and a hosted worker that starts and stops with the host. The app needs a host, such as ASP.NET Core or the .NET Generic Host.

## Enqueue and check status

`IIngestionQueue` is scoped, like `IRagPipeline`, because it reads the caller's access scope. Inject it into a request handler:

```csharp
public sealed class UploadHandler(IIngestionQueue queue)
{
    public ValueTask<string> UploadAsync(Stream upload, string fileName, string? contentType, CancellationToken ct) =>
        queue.EnqueueAsync(new DocumentSource(fileName, upload, fileName, contentType), ct);

    public ValueTask<IngestionJobStatus?> StatusAsync(string jobId, CancellationToken ct) => queue.GetStatusAsync(jobId, ct);
}
```

The default queue copies a stream to a temporary file before `EnqueueAsync` returns, so the caller can close the upload straight away. A source made with `DocumentSource.FromFile` is not copied: the job reads the file when it runs, so the file must still be there.

A background service has no request scope, so it creates one first:

```csharp
await using var scope = services.CreateAsyncScope();
var queue = scope.ServiceProvider.GetRequiredService<IIngestionQueue>();
var jobId = await queue.EnqueueAsync(DocumentSource.FromFile("/data/handbook.pdf"));
```

`CancelAsync(jobId)` cancels a job: a queued job never runs, and a running one is stopped and not retried. It returns false for a job that has finished or is unknown.

### Statuses

`GetStatusAsync` returns an `IngestionJobStatus`, or null for an unknown job or one whose finished status has expired.

| State | Meaning |
|---|---|
| `Queued` | Waiting to run, or waiting to retry after a transient error (`Attempts` and `Error` show the last attempt) |
| `Running` | An attempt is running |
| `Succeeded` | Ingested; `Result` holds the `IngestResult` |
| `Failed` | A permanent error, or a transient one on the last attempt; `Error` is `"{ExceptionType}: {Message}"` |
| `Cancelled` | Cancelled with `CancelAsync`, or stopped by shutdown |

### Retries

A job gets up to `MaxAttempts` attempts (default 3) when its errors are transient. It waits `RetryDelay` before the first retry (default 1 second), and twice as long before each later one.

These errors are permanent, so the job fails after one attempt: `UnsupportedDocumentException`, `DocumentReadException`, `EmbeddingModelMismatchException`, `RagAccessDeniedException`, `ArgumentException`, `InvalidOperationException` (such as a store's settings being rejected), `FileNotFoundException`, `DirectoryNotFoundException` and `UnauthorizedAccessException`. Everything else is retried, so a provider's rate limit, a timeout or a store that is briefly down are retried without RagDotNet knowing each provider's exception types. Set `IsTransient` to decide yourself.

## Pause and resume

`PauseAsync` stops new attempts from starting and returns once no attempt is running, so when it returns nothing is being ingested. Queued jobs are kept, and enqueuing still works until the queue is full. Use it around work that must not overlap ingestion, such as a reindex:

```csharp
await queue.PauseAsync();      // returns once nothing is being ingested
try
{
    await admin.ReindexAsync();
}
finally
{
    await queue.ResumeAsync();
}
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `Capacity` | 1000 | Jobs the queue holds; when it is full, `EnqueueAsync` waits for room (and honours its cancellation token) |
| `Concurrency` | 2 | Jobs that run at once |
| `MaxAttempts` | 3 | Attempts per job when its errors are transient |
| `RetryDelay` | 1 second | Wait before the first retry; it doubles before each later one |
| `StatusRetention` | 1 hour | How long a finished job's status is kept |
| `TempDirectory` | `Path.GetTempPath()` | Where stream copies go; each queue uses its own `ragdotnet-queue-*` subdirectory |
| `IsTransient` | see [Retries](#retries) | Decides which errors are retried |

`Capacity`, `Concurrency` and `MaxAttempts` must be at least 1, `RetryDelay` must not be negative and `StatusRetention` must be positive; other values fail at startup.

## What the default queue does not do

**The default queue keeps jobs in memory. Jobs that have not finished when the app stops are lost, and so are all statuses.** On shutdown the worker cancels running attempts and deletes its temporary files. Enqueue a lost document again after a restart; re-ingesting a document that did finish is cheap, because unchanged documents are skipped. For jobs that survive restarts, write a durable queue (below).

Temporary files: each stream job's copy is deleted when the job finishes, whatever its state, and the queue's directory is deleted on shutdown. A crash can leave a `ragdotnet-queue-*` directory behind; it is safe to delete.

## Access scopes

The queue captures the caller's access scope when the job is enqueued, and the job runs under that scope, never under whatever scope is current when it runs. A scope that fails validation (such as the wrong partition keys) or lacks `RagPermissions.Write` is refused at enqueue, and nothing is queued. With no `IAccessScope` registered, or one that returns null, the job has full access.

`IRagAdmin.ForScope(null)` gives the same full-access pipeline, for code that must ingest outside any user's scope.

## Writing a durable queue

To keep jobs across restarts, implement `IIngestionJobQueue` and register it before `AddIngestionQueue`, which then uses yours instead of the default. If your queue is an `IHostedService`, `AddIngestionQueue` starts and stops it with the host; don't also register it as a hosted service.

A durable queue keeps the same contract as the default:

- **Store the job at enqueue.** Copy the source's content (the caller may dispose its stream as soon as `EnqueueAsync` returns), with its `DocumentId`, `FileName`, `ContentType`, `Title` and `Metadata`, and store the scope with `RagJson.Options`.
- **Run each attempt with `IngestionJobRunner.RunAsync`**, which ingests under the job's scope. Rebuild the `DocumentSource` from the stored content for every attempt.
- **Retry only when `IngestionJobRunner.IsTransient(e)` is true**, so the app's `IsTransient` option applies to your queue too.

```csharp
public sealed class MyDurableQueue(IngestionJobRunner runner, MyJobStore store) : BackgroundService, IIngestionJobQueue
{
    public async ValueTask EnqueueAsync(IngestionJob job, CancellationToken ct = default)
    {
        var scope = job.Scope is null ? null : JsonSerializer.Serialize(job.Scope, RagJson.Options);
        await store.SaveAsync(job.JobId, job.Source, scope, ct); // copies the content
    }

    // In ExecuteAsync, for each stored job:
    //   var scope = json is null ? null : JsonSerializer.Deserialize<AccessScope>(json, RagJson.Options);
    //   await runner.RunAsync(new IngestionJob(id, source, scope), ct);
    //   on an error, retry only if runner.IsTransient(e)
    // ...plus GetStatusAsync, CancelAsync, PauseAsync and ResumeAsync.
}

services.AddSingleton<IIngestionJobQueue, MyDurableQueue>();
services.AddRag(o => { /* ... */ }).AddIngestionQueue();
```

### JSON format

`RagJson.Options` reads and writes `AccessScope` and `RagFilter`:

```json
{
  "partition": { "tenant": "acme" },
  "filter": {
    "op": "and",
    "filters": [
      { "op": "eq", "field": "department", "value": { "string": "legal" } },
      { "op": "in", "field": "year", "values": [ { "int": 2025 }, { "int": 2026 } ] },
      { "op": "contains", "field": "tags", "value": "contract" },
      { "op": "containsAny", "field": "tags", "values": [ "nda", "msa" ] }
    ]
  },
  "permissions": "Read, Write"
}
```

- Filters: `eq` and `in` (with `value` or `values`), `contains` and `containsAny` (plain strings), and `and` and `or` (with `filters`).
- Values in `eq` and `in` name their type, so they read back as the same type: `{"string":"x"}`, `{"int":5}`, `{"long":5}`, `{"bool":true}` or `{"date":"2026-01-02T03:04:05.0000000+00:00"}` (the round-trip `"O"` format, keeping the offset). Writing a value of any other type throws `JsonException`.
- `partition` and `permissions` are required, so a damaged record is refused rather than read as full access; `filter` may be null or missing. `permissions` is a `RagPermissions` name or comma-separated list, as `ToString()` writes it.
