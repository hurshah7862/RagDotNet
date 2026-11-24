# RagDotNet.Hosting

Background ingestion for [RagDotNet](https://github.com/hurshah7862/RagDotNet): enqueue a document, get a job id back straight away, and check its status later.

```csharp
services.AddRag(o => { /* ... */ })
    .AddIngestionQueue(o => o.Concurrency = 4);

// In a request handler (IIngestionQueue is scoped, like IRagPipeline):
var jobId = await queue.EnqueueAsync(new DocumentSource(fileName, upload, fileName, contentType));
var status = await queue.GetStatusAsync(jobId); // Queued, Running, Succeeded, Failed or Cancelled
```

- Jobs run on a hosted worker, 2 at a time by default, with up to 3 attempts on transient errors (1 s, then 2 s apart). Known-permanent errors, such as an unsupported format, fail after one attempt.
- Each job runs under the access scope of the caller that enqueued it. A scope without Write permission is refused at enqueue.
- `CancelAsync`, `PauseAsync` and `ResumeAsync`; pause returns once nothing is being ingested, for example before a reindex.
- **The default queue keeps jobs in memory: unfinished jobs are lost when the app stops.** For jobs that survive restarts, implement `IIngestionJobQueue`; `IngestionJobRunner` runs the jobs and `RagJson` stores their scopes.

See [docs/hosting.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/hosting.md) for options, statuses, retries, and writing a durable queue.
