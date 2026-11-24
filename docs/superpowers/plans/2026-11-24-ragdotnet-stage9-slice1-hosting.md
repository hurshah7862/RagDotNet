# Stage 9 slice 1: RagDotNet.Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A new `RagDotNet.Hosting` package gives apps a background ingestion queue. They enqueue a document, get a job id, and check its status later. The default in-memory queue runs jobs on an `IHostedService` worker, with retries, cancel, and pause and resume. Each job runs under the access scope the caller had when it was enqueued.

**Architecture:**
- **What the app calls, `IIngestionQueue`.** A scoped facade, like `IRagPipeline`. It captures the caller's `AccessScope` at enqueue time, validates it, checks it grants Write, and hands an `IngestionJob { JobId, Source, Scope }` to the backend.
- **The backend, `IIngestionJobQueue`.** The default is an in-memory `System.Threading.Channels` queue with a worker. A durable queue replaces it by implementing the same interface, and runs each job with the public `IngestionJobRunner`.
- **The runner.** It ingests a job through `IRagAdmin.ForScope(job.Scope)`. Core gains one small change so that `ForScope(null)` means full access.
- **An optional `RagJson` helper.** It serializes `AccessScope` and `RagFilter`, so a durable queue can persist a job's scope.

**Tech Stack:** .NET 8/10, Microsoft.Extensions.Hosting.Abstractions 10.0.14, System.Threading.Channels and System.Text.Json (both in the box), xUnit v3, Microsoft.Extensions.TimeProvider.Testing 10.11.0 (tests).

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`:
- §5.12 (background ingestion);
- §6 (error handling: "The Hosting queue retries");
- §5.8 (access: no scope means full access);
- §5.13 (logging);
- §9.1 ("queue retries and statuses");
- §10, stage 9.

Slice 2, the `RagDotNet.DataIngestion` adapters, gets its own plan.

## Global Constraints

- **Branch:** `stage9-slice1-hosting` from `main` (dd528fc). The first commit adds this plan.
- **Build and test in Docker** (the `dn.sh` twin of `./docker/sdk/run.ps1`). Local `dotnet` is blocked by App Control. Run test projects one at a time.
- **Package:** `src/RagDotNet.Hosting`, targeting `net8.0;net10.0`.
  - It depends only on Core and `Microsoft.Extensions.Hosting.Abstractions` (§4). Core never references it (anti-goal: Core pulling in Hosting).
  - It is a stable package, not a preview.
  - It has PublicAPI files and a README.
- **§5.12 values:**
  - The default is a bounded Channels queue processed by an `IHostedService`.
  - Concurrency defaults to 2.
  - Up to 3 attempts, with exponential backoff, on transient errors.
  - Statuses are kept in memory for a configurable retention period.
  - Jobs are not durable, and the docs say so.
  - A stream source is copied to a temporary file on enqueue; a file-path source is not.
  - The worker ingests under the stored scope, never an ambient one.
  - `PauseAsync`/`ResumeAsync` hold queued jobs without losing them.
- **§5.12 statuses:** Queued, Running, Succeeded(IngestResult), Failed(error, attempts). This plan adds Cancelled, the result of `CancelAsync`.
- **§5.13:** `ILogger` for warnings. No document text is logged.
- **Commits** end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Decisions this plan makes (builder's calls, §12)

- **Job ids** are `Guid.NewGuid().ToString("N")` strings: unguessable, and durable queues can keep their own string ids. `GetStatusAsync` returns `null` for an unknown or expired id.
- **The status record** is `IngestionJobStatus(string JobId, string DocumentId, IngestionJobState State, int Attempts, IngestResult? Result, string? Error, DateTimeOffset UpdatedAt)`.
  - `Error` is `"{ExceptionTypeName}: {Message}"`.
  - During a backoff wait, the state is `Queued`, with `Attempts` and `Error` from the last attempt.
- **Transient vs. permanent** (`IngestionJobRunner.IsTransient`):
  - **Permanent:** `UnsupportedDocumentException`, `DocumentReadException`, `EmbeddingModelMismatchException`, `RagAccessDeniedException`, `ArgumentException`, `InvalidOperationException` (settings rejections, hybrid unavailable), `FileNotFoundException`, `DirectoryNotFoundException` and `UnauthorizedAccessException`, each matched on the top-level exception.
  - **Transient:** everything else, so provider-specific errors (HTTP 429/503, timeouts, a store that is down) are retried without RagDotNet knowing each provider's exception types.
  - `IngestionQueueOptions.IsTransient` overrides the default.
- **Backoff:**
  - The delay is `RetryDelay × 2^(attempt−1)`, with a default `RetryDelay` of 1 s, so the waits are 1 s and then 2 s.
  - The worker keeps its slot during the wait. The delay uses the registered `TimeProvider`.
  - Before each attempt, the job waits for the pause gate.
- **Pause:**
  - `PauseAsync` stops new attempts from starting, and returns once no attempt is running. When it returns, the queue is writing nothing.
  - Enqueue still works while paused, up to the queue's capacity.
- **Capacity:**
  - `Capacity` defaults to 1000.
  - When the queue is full, `EnqueueAsync` waits for room. It honours its cancellation token, and leaves no temporary file behind when cancelled.
- **Cancel:**
  - A queued job, including one waiting out a backoff, becomes `Cancelled` and never runs.
  - A running job's token is cancelled. It ends `Cancelled` and is not retried.
  - `CancelAsync` on a finished or unknown job returns `false`.
- **Temporary files:**
  - Each queue instance has its own directory, `<TempDirectory or Path.GetTempPath()>/ragdotnet-queue-<guid>`.
  - A job's file is deleted when the job finishes in any state.
  - The whole directory is deleted on stop and on dispose.
  - Each attempt opens a fresh stream, keeping `FileName`, `ContentType`, `Title` and `Metadata`.
- **Retention:** finished statuses are dropped `StatusRetention` after `UpdatedAt` (default 1 hour), swept on `EnqueueAsync` and `GetStatusAsync`.
- **Shutdown:**
  - On `StopAsync`, the worker stops taking jobs and cancels running attempts.
  - Unfinished jobs are lost, and their temporary files are deleted.
  - `EnqueueAsync` after stop throws `InvalidOperationException` ("The ingestion queue has stopped.").
- **Registration:** `builder.AddIngestionQueue(Action<IngestionQueueOptions>? configure = null)` on `IRagBuilder`. It registers:
  - `IIngestionQueue`, scoped;
  - `IngestionJobRunner`, singleton;
  - `IIngestionJobQueue`, singleton, with `TryAdd`, so an app's own queue registered first wins;
  - an `IHostedService` that starts the registered `IIngestionJobQueue` if it is an `IHostedService`, and does nothing otherwise;
  - `TimeProvider.System`, with `TryAdd`;
  - option validation: `Capacity`, `Concurrency` and `MaxAttempts` ≥ 1, `RetryDelay` ≥ 0, `StatusRetention` > 0.
- **JSON (§5.12 "if durable queues need it"):** they do. A job's scope holds a `RagFilter` tree with typed values, and every durable-queue author would otherwise write the same polymorphic converter. So `RagJson` ships in this package (Task 5). Core stays free of it.

## Review Focus

1. **The caller disposes its stream as soon as `EnqueueAsync` returns** → the job still succeeds, because it reads the temporary copy. Test `Job_succeeds_after_the_caller_disposes_its_stream` (Task 3).
2. **The host shuts down with jobs queued and running** → `StopAsync` throws nothing and no temporary file is left behind. Test `Stop_cancels_running_jobs_and_leaves_no_temp_files` (Task 3).
3. **A permanent error, such as an unsupported format** → `Failed` after one attempt, not three. Test `Permanent_error_fails_after_one_attempt` (Task 3).
4. **The queue is full during a long pause** → `EnqueueAsync` waits and honours its token, without throwing away jobs or leaking files. Test `Full_queue_waits_and_honours_cancellation` (Task 3).
5. **The same document enqueued twice and run concurrently** → both jobs succeed, and the store holds one document with the content of the last ingest. Core's per-document lock serializes them. Test `Two_jobs_for_one_document_leave_one_document` (Task 3).

---

### Task 1: Core: `ForScope(null)` means full access

**Files:**
- Modify:
  - `src/RagDotNet.Core/Pipeline/IRagAdmin.cs:40-45`
  - `src/RagDotNet.Core/Pipeline/RagAdmin.cs:16-21`
  - `src/RagDotNet.Core/PublicAPI.Unshipped.txt:374`
- Test: `tests/RagDotNet.Core.Tests/` (the existing ForScope test file; find it with `grep -rln ForScope tests/RagDotNet.Core.Tests`)

**Interfaces:**
- Produces: `IRagPipeline IRagAdmin.ForScope(AccessScope? scope)`. With `null`, the pipeline has full access and ignores any registered `IAccessScope`. The XML doc says so.

- [ ] **Step 1: Failing test** `ForScope_null_is_full_access_and_ignores_the_registered_scope`:
  - DI with `AddRag(...)` and `PartitionKeys = ["tenant"]`.
  - `AddAccessScope<…>()` returns a tenant-`a` scope with `RagPermissions.Read` only.
  - `admin.ForScope(null).IngestAsync(Md("d", "alpha"))` succeeds; the scope alone would be refused Write.
  - `admin.ForScope(null).RetrieveAsync("alpha")` finds `d`.
- [ ] **Step 2: Run** `dn.sh test tests/RagDotNet.Core.Tests -f net10.0 -- --filter-method '*ForScope_null*'`. Expected: FAIL with `ArgumentNullException`.
- [ ] **Step 3: Implement.** `null` gives `new RagPipeline(engine, accessScope: null, chat)`; otherwise validate as today. Update the PublicAPI line to `AccessScope? scope`.
- [ ] **Step 4: Run** all Core tests, net8.0 and net10.0. Expected: all pass.
- [ ] **Step 5: Commit** `feat(core): IRagAdmin.ForScope(null) gives a full-access pipeline`.

### Task 2: The package, job types and `IngestionJobRunner`

**Files:**
- Create:
  - `src/RagDotNet.Hosting/RagDotNet.Hosting.csproj` (shaped like `RagDotNet.Keyword.Lucene.csproj`, without the preview target; references Core and `Microsoft.Extensions.Hosting.Abstractions`; `InternalsVisibleTo RagDotNet.Hosting.Tests`)
  - `PublicAPI.Shipped.txt` (empty) and `PublicAPI.Unshipped.txt`
  - `IngestionJob.cs`, `IngestionJobStatus.cs` (with `IngestionJobState`), `IngestionQueueOptions.cs`, `IngestionJobRunner.cs`
  - `tests/RagDotNet.Hosting.Tests/RagDotNet.Hosting.Tests.csproj` (like the Lucene tests project; links the Core test fakes `FakeEmbeddingGenerator`, `FakeChatClient`, `GatedEmbeddingGenerator` and `TestRag`, if `TestRag` is a standalone file)
- Modify:
  - `Directory.Packages.props`: add `Microsoft.Extensions.Hosting.Abstractions` 10.0.14 and `Microsoft.Extensions.TimeProvider.Testing` 10.11.0.
  - `RagDotNet.slnx`: add both projects.
  - `.github/workflows/ci.yml`: a "Test hosting (net8.0)" step in the Linux build job and the Windows job, after the Lucene step.
- Namespace: `RagDotNet.Hosting`.

**Interfaces:**
- Produces:
  - `public sealed record IngestionJob(string JobId, DocumentSource Source, AccessScope? Scope)`
  - `public enum IngestionJobState { Queued, Running, Succeeded, Failed, Cancelled }`
  - `public sealed record IngestionJobStatus(string JobId, string DocumentId, IngestionJobState State, int Attempts, IngestResult? Result, string? Error, DateTimeOffset UpdatedAt)`
  - `public sealed class IngestionQueueOptions`, with:
    - `int Capacity = 1000`
    - `int Concurrency = 2`
    - `int MaxAttempts = 3`
    - `TimeSpan RetryDelay = 1 s`
    - `TimeSpan StatusRetention = 1 h`
    - `string? TempDirectory`
    - `Func<Exception, bool>? IsTransient`
  - `public sealed class IngestionJobRunner(IRagAdmin admin, IOptions<IngestionQueueOptions> options)`, with:
    - `Task<IngestResult> RunAsync(IngestionJob job, CancellationToken ct)`, which calls `admin.ForScope(job.Scope).IngestAsync(job.Source, cancellationToken: ct)`;
    - `bool IsTransient(Exception e)`: the option if set, otherwise the default classification in "Decisions".

- [ ] **Step 1: Failing tests** in `IngestionJobRunnerTests`:
  - `Job_without_scope_ingests_with_full_access`: a registered read-only tenant scope is ignored, and the document is retrievable through `ForScope(null)`.
  - `Job_with_scope_ingests_into_that_scope`: a tenant-`a` scope puts the document in partition `a`. Admin stats show it under `a`, and a tenant-`b` `ForScope` cannot retrieve it.
  - `Default_classification` (a theory):
    - **Not transient:** `UnsupportedDocumentException`, `DocumentReadException`, `EmbeddingModelMismatchException`, `RagAccessDeniedException`, `ArgumentException`, `InvalidOperationException` and `FileNotFoundException`.
    - **Transient:** `HttpRequestException`, `TimeoutException`, `IOException`, a `VectorStoreException`, and a plain `Exception`.
  - `Option_overrides_classification`: with `IsTransient = _ => false`, an `HttpRequestException` is not transient.
- [ ] **Step 2: Run.** Expected: compile errors, then FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dn.sh test tests/RagDotNet.Hosting.Tests -f net10.0`, then `-f net8.0`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(hosting): RagDotNet.Hosting package with job types and IngestionJobRunner`.

### Task 3: The in-memory queue and its worker

**Files:**
- Create:
  - `src/RagDotNet.Hosting/IIngestionJobQueue.cs`
  - `src/RagDotNet.Hosting/InMemoryIngestionJobQueue.cs` (internal sealed, `BackgroundService, IIngestionJobQueue, IAsyncDisposable`)
- Test: `tests/RagDotNet.Hosting.Tests/InMemoryIngestionJobQueueTests.cs`. Tests construct the queue directly:
  - a runner over a pipeline built with the InMemory store and fakes;
  - `FakeTimeProvider`;
  - a temp directory per test, deleted in `Dispose`;
  - the queue started with `StartAsync`.

  A flaky embedding generator, failing the first N calls with `HttpRequestException`, lives in the test project.

**Interfaces:**
- Consumes: `IngestionJob`, `IngestionJobStatus`, `IngestionJobState`, `IngestionQueueOptions`, `IngestionJobRunner` (Task 2).
- Produces: `public interface IIngestionJobQueue`, with:
  - `ValueTask EnqueueAsync(IngestionJob job, CancellationToken cancellationToken = default)`
  - `ValueTask<IngestionJobStatus?> GetStatusAsync(string jobId, CancellationToken cancellationToken = default)`
  - `ValueTask<bool> CancelAsync(string jobId, CancellationToken cancellationToken = default)`
  - `ValueTask PauseAsync(CancellationToken cancellationToken = default)`
  - `ValueTask ResumeAsync(CancellationToken cancellationToken = default)`

  The XML docs state the contract every implementation keeps, durable or not:
  - persist `Source` and `Scope`;
  - run jobs with `IngestionJobRunner`;
  - retry only when `IsTransient` is true.

  `InMemoryIngestionJobQueue(IngestionJobRunner runner, IOptions<IngestionQueueOptions> options, TimeProvider time, ILogger<InMemoryIngestionJobQueue> logger)`.

- [ ] **Step 1: Failing tests.** A helper `WaitForStateAsync(queue, id, state)` polls `GetStatusAsync` with a 10 s timeout, using real time for polling only.

| Test | Setup → assertions |
|---|---|
| `Enqueued_job_runs_and_succeeds` | Enqueue a Markdown stream → `Succeeded`, `Attempts == 1`, `Result.Status == Created`, `Result.ChunkCount > 0`; the document is retrievable |
| `Unknown_job_has_no_status` | `GetStatusAsync("nope")` → `null`; `CancelAsync("nope")` → `false` |
| `Job_succeeds_after_the_caller_disposes_its_stream` | Enqueue, then dispose the stream at once → `Succeeded` (Review Focus 1) |
| `File_source_is_read_from_its_path` | `DocumentSource.FromFile` on a temp `.md` → `Succeeded`; the queue's temp directory never holds a copy |
| `Transient_errors_retry_with_backoff` | The generator fails twice:<br>• after the first failure: `Queued`, `Attempts == 1`, `Error` starting `"HttpRequestException:"`;<br>• advance 1 s → second failure; advance 2 s → `Succeeded`, `Attempts == 3` |
| `Transient_errors_fail_after_max_attempts` | The generator always fails; advance time → `Failed`, `Attempts == 3`, `Error` starting `"HttpRequestException:"` |
| `Permanent_error_fails_after_one_attempt` | A `.xyz` source (no reader) → `Failed`, `Attempts == 1`, `Error` starting `"UnsupportedDocumentException:"` (Review Focus 3) |
| `Cancel_queued_job_never_runs` | Pause; enqueue; cancel → `true`, `Cancelled`; resume; the document is never ingested; its temp file is gone |
| `Cancel_running_job_ends_cancelled` | Hold the job `Running` with `GatedEmbeddingGenerator`; cancel → `true`; it ends `Cancelled`, `Attempts == 1`, with no retry |
| `Cancel_finished_job_returns_false` | After `Succeeded`, cancel → `false`; the state stays `Succeeded` |
| `Pause_holds_jobs_and_resume_runs_them` | Pause; enqueue 3 → after 500 ms all are still `Queued`; resume → all `Succeeded` |
| `Pause_returns_when_running_jobs_finish` | Hold one job `Running`; `PauseAsync` does not complete until the gate opens; then that job is `Succeeded`, and a job enqueued after it stays `Queued` |
| `Concurrency_limits_running_jobs` | `Concurrency = 2`, gated generator, 4 jobs → at most 2 `Running` at once; all `Succeeded` after the gate opens |
| `Full_queue_waits_and_honours_cancellation` | `Capacity = 1`, `Concurrency = 1`:<br>• job A held `Running`, job B enqueued;<br>• enqueuing C with a token cancelled after 200 ms throws `OperationCanceledException`;<br>• C has no status, and the temp directory holds only B's file (Review Focus 4) |
| `Finished_status_expires_after_retention` | After `Succeeded`, advance `StatusRetention` + 1 s → `GetStatusAsync` returns `null` |
| `Stop_cancels_running_jobs_and_leaves_no_temp_files` | One job held `Running` and two queued; `StopAsync` completes without throwing; the queue's temp directory no longer exists; a later `EnqueueAsync` throws `InvalidOperationException` (Review Focus 2) |
| `Two_jobs_for_one_document_leave_one_document` | `Concurrency = 2`; enqueue `d` with "alpha" then `d` with "bravo" → both `Succeeded`; stats show 1 document; the store holds the content of whichever job ran last, so assert on stats, not on order (Review Focus 5) |

- [ ] **Step 2: Run.** Expected: compile errors, then FAIL.
- [ ] **Step 3: Implement**, as "Decisions" describes:
  - **Statuses:** a `ConcurrentDictionary<string, JobEntry>`, where each entry holds the job, its status, its `CancellationTokenSource` and its temp path.
  - **Queue:** a bounded `Channel<JobEntry>` with `FullMode = Wait`.
  - **Workers:** `Concurrency` loops in `ExecuteAsync`. Each one reads a job, skips it if cancelled, then for each attempt waits for the pause gate, runs it, and classifies the outcome.
  - **Pause gate:** a `TaskCompletionSource` that is replaced on pause and completed on resume, plus a count of running attempts that `PauseAsync` waits on.
  - **Logging:** a warning per retry, and an error per failure, giving the job id, document id and exception type, with no document text.
- [ ] **Step 4: Run** the Hosting tests on net10.0 and net8.0, twice each to catch timing flakiness. Expected: all pass, both times.
- [ ] **Step 5: Commit** `feat(hosting): in-memory ingestion queue with retries, cancel, pause and resume`.

### Task 4: `IIngestionQueue` and `AddIngestionQueue`

**Files:**
- Create:
  - `src/RagDotNet.Hosting/IIngestionQueue.cs`
  - `IngestionQueue.cs` (internal, scoped)
  - `IngestionQueueBuilderExtensions.cs`
  - `QueueHostedService.cs` (internal)
- Test: `tests/RagDotNet.Hosting.Tests/IngestionQueueTests.cs`. These tests use a real `Host.CreateApplicationBuilder()` with `AddRag(...)` (InMemory store, fakes), `.AddIngestionQueue(o => o.RetryDelay = TimeSpan.Zero)`, the host started, and a DI scope per "request".

**Interfaces:**
- Consumes: `IIngestionJobQueue` and `InMemoryIngestionJobQueue` (Task 3), `IngestionJobRunner` (Task 2), and `IRagAdmin.ForScope` (Task 1).
- Produces:
  - `public interface IIngestionQueue`, with:
    - `ValueTask<string> EnqueueAsync(DocumentSource source, CancellationToken cancellationToken = default)`;
    - `GetStatusAsync`, `CancelAsync`, `PauseAsync` and `ResumeAsync`, with the same signatures as `IIngestionJobQueue`.
  - `public static IRagBuilder AddIngestionQueue(this IRagBuilder builder, Action<IngestionQueueOptions>? configure = null)`, in `RagDotNet.Hosting.IngestionQueueBuilderExtensions`.
  - **The facade's `EnqueueAsync`:**
    1. Resolve the scope: `IAccessScope?` from DI, then `GetScopeAsync`.
    2. If the scope is not null, call `admin.ForScope(scope)` to validate it (an `ArgumentException` for a malformed scope).
    3. If it lacks `RagPermissions.Write`, throw `RagAccessDeniedException("enqueue", RagPermissions.Write, "enqueue: The access scope does not grant Write.")`.
    4. Create the job id, then `jobQueue.EnqueueAsync(new IngestionJob(id, source, scope))`.

    Nothing is queued when any of these steps throws.

- [ ] **Step 1: Failing tests:**

| Test | Setup → assertions |
|---|---|
| `Enqueue_through_DI_runs_on_the_hosted_worker` | No scope registered; enqueue from a DI scope → `Succeeded`; retrievable via `IRagAdmin.ForScope(null)` |
| `Scope_is_captured_at_enqueue` | A registered `IAccessScope` reads a mutable tenant value: tenant `a` at enqueue, switched to `b` straight after → the document lands in `a` (`ForScope(tenant a)` finds it, `b` doesn't) |
| `Scope_without_write_is_refused_at_enqueue` | A read-only scope → `RagAccessDeniedException`; the in-memory queue holds no job, so no status and no temp file |
| `Malformed_scope_is_refused_at_enqueue` | Wrong partition keys → `ArgumentException`; nothing queued |
| `App_queue_registered_first_replaces_the_default` | `services.AddSingleton<IIngestionJobQueue>(recording)` before `AddIngestionQueue` → enqueue reaches `recording` with the captured scope and the same `DocumentSource` instance; the in-memory worker never starts (no `ragdotnet-queue-*` directory appears under the configured `TempDirectory`) |
| `Invalid_options_fail_at_startup` | `Concurrency = 0` → `OptionsValidationException` when the queue is resolved |

- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Hosting tests on net10.0 and net8.0. Expected: all pass.
- [ ] **Step 5: Commit** `feat(hosting): IIngestionQueue captures the caller's scope; AddIngestionQueue registration`.

### Task 5: `RagJson` for durable queues

**Files:**
- Create: `src/RagDotNet.Hosting/RagJson.cs`, `RagFilterJsonConverter.cs`, `AccessScopeJsonConverter.cs`
- Test: `tests/RagDotNet.Hosting.Tests/RagJsonTests.cs`

**Interfaces:**
- Produces:
  - `public static class RagJson { public static JsonSerializerOptions Options { get; } }` (read-only, with both converters).
  - `public sealed class RagFilterJsonConverter : JsonConverter<RagFilter>`
  - `public sealed class AccessScopeJsonConverter : JsonConverter<AccessScope>`
- **Format** (exact, documented in `docs/hosting.md`):
  - **Filters:**
    - `{"op":"eq","field":"f","value":V}`
    - `{"op":"in","field":"f","values":[V,…]}`
    - `{"op":"contains","field":"f","value":"s"}`
    - `{"op":"containsAny","field":"f","values":["s",…]}`
    - `{"op":"and","filters":[…]}`
    - `{"op":"or","filters":[…]}`
  - **Typed values `V`:** an object with one property naming the type: `{"string":"x"}`, `{"int":5}`, `{"long":5}`, `{"bool":true}`, `{"date":"2026-01-02T03:04:05.1234567+00:00"}` (round-trip `"O"` format).
  - **Scope:** `{"partition":{…},"filter":F|null,"permissions":"Read, Write"}`, with the permissions written by `RagPermissions.ToString()` and read back by `Enum.Parse`.
  - Writing a value of any other type throws `JsonException`, naming the type and the five supported types.

- [ ] **Step 1: Failing tests:**
  - `Every_filter_round_trips`: one filter that nests all six node types and all five value types. Assert that serializing, deserializing and serializing again gives the same JSON. Assert each value's runtime type survives (`long` stays `long`, `int` stays `int`, `DateTimeOffset` keeps its offset).
  - `Scope_round_trips`: partition, filter and `Read | Write` all survive, and `null` filter survives.
  - `Restored_scope_still_restricts_reads`: ingest `a1` (`tag = "x"`) and `a2` (`tag = "y"`) in tenant `a`. A scope with `Filter = Eq("tag", "x")`, serialized and restored, then passed to `ForScope`, retrieves only `a1`.
  - `Unsupported_value_type_is_refused`: `Eq("f", 1.5)` → `JsonException` containing `"Double"`.
  - `Malformed_json_is_refused`: an unknown `op` → `JsonException` naming the op.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Hosting tests on both frameworks. Expected: all pass.
- [ ] **Step 5: Commit** `feat(hosting): RagJson serializes AccessScope and RagFilter for durable queues`.

### Task 6: Docs

**Files:**
- Create: `docs/hosting.md`, `src/RagDotNet.Hosting/README.md` (packed, like Lucene's)
- Modify: the root `README.md` package list, if it has one.
- Test: `tests/RagDotNet.Hosting.Tests/DocsExamplesTests.cs`

- [ ] **Step 1: Write `docs/hosting.md`:**
  - **Setup:** `builder.Services.AddRag(...).AddIngestionQueue(...)`.
  - **Usage:**
    - Enqueue from a request, poll the status, then cancel, pause and resume.
    - `IIngestionQueue` is scoped, so a background service creates a DI scope first.
    - **Pause during a reindex:** `await queue.PauseAsync(); await admin.ReindexAsync(); await queue.ResumeAsync();`
  - **Options table:** all seven options, with their defaults.
  - **Statuses**, and what's transient.
  - **Not durable, in bold:** unfinished jobs are lost on restart.
  - **Temporary files.**
  - **Scopes:** captured at enqueue; no scope means full access.
  - **Writing a durable queue:** implement `IIngestionJobQueue`, register it before `AddIngestionQueue`, persist `Source` content and `Scope` (with `RagJson`), and run jobs with `IngestionJobRunner`, retrying only when `IsTransient` is true. The JSON format reference goes here.
- [ ] **Step 2: `DocsExamplesTests`**, keeping the doc's code compiling and running:
  - `Setup_and_enqueue_example_runs` (the setup and enqueue example, end to end on a host);
  - `Pause_around_reindex_example_runs`.
- [ ] **Step 3: Run each test project once, in Docker:**
  - Core, net8.0 and net10.0;
  - Hosting, net8.0 and net10.0;
  - SQLite connector, Lucene, Readers, OCR and Evaluation;
  - the store tests without integration.

  Expected: 0 failed.
- [ ] **Step 4: Commit** `docs: background ingestion with RagDotNet.Hosting`. Then the final review, one fix pass, and the PR "Stage 9 slice 1: RagDotNet.Hosting". Done when all four CI jobs pass.
