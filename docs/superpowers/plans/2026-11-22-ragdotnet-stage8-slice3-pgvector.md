# Stage 8 slice 3: PostgreSQL + pgvector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The shared store suite passes against PostgreSQL with pgvector, through `CommunityToolkit.VectorData.PgVector`, with String and Guid keys, in the CI store job on every pull request.

**Architecture:** No new package. Core gets one change the connector forces: `DateTimeOffset` metadata and filter values are normalized to UTC (the connector rejects any other offset). The store tests gain a Docker-backed fixture: one pgvector container per test run, started by Testcontainers on first use and shared by both key bindings. Anything else the suite finds is triaged by spec §2.1, test first.

**Tech Stack:** CommunityToolkit.VectorData.PgVector 1.0.2 (Npgsql 10, Pgvector 0.3.2), Testcontainers.PostgreSql 4.16.0, image `pgvector/pgvector:0.8.7-pg17`, xUnit v3, GitHub Actions (ubuntu, Docker).

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` §2, §2.1, §2.2, §4, §5, §7, §8 (slice 3).

## Global Constraints

- Branch `stage8-slice3-pgvector` from `main` (85c6d29); the first commit adds this plan.
- Build and test in Docker (`./docker/sdk/run.ps1`, or a bash twin of it); local `dotnet` is blocked by App Control. The Postgres tests need the host's Docker: the wrapper mounts the Docker socket (Task 2).
- Pinned versions: `CommunityToolkit.VectorData.PgVector` 1.0.2, `Testcontainers.PostgreSql` 4.16.0 (4.17.0 is six days old), image `pgvector/pgvector:0.8.7-pg17`.
- Spec §2.1, in order: work around in Core (failing test first, every passing store stays green), or the connector's other mode; a gap in a core feature (lifecycle, filters, access scope, reindex, manifest listing) means PgVector is **not** marked supported and is reported upstream; a minor gap may ship as a documented `NotSupportedException`; never fork the connector.
- Spec §2.2: a setting PgVector needs is documented in `docs/vector-stores.md` and set only in the fixture's `Configure`; Core does not detect the store.
- Spec §4: no store-specific expectations in the shared suite; Docker stores fail in CI (`RAGDOTNET_STORES_REQUIRED=1`), never skip.
- Spec §7: Docker stores run locally only with `RAGDOTNET_INTEGRATION=1`; one container per store per test run, pinned image tag.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **A document dated with a non-UTC offset** (`+05:30`) → ingests on every store, filters by the same instant match it, and it comes back as the same instant (offset zero). Tests `Metadata_dates_are_stored_in_utc` and `Date_filter_values_are_compared_in_utc` (Task 1); the suite's `eq-date`/`in-date` cases on PgVector (Task 3).
2. **Docker missing or the image cannot be pulled** → locally the PgVector tests skip with a reason naming Docker; with `RAGDOTNET_STORES_REQUIRED=1` they fail with that reason. Test `Unstartable_container_names_the_cause` (Task 2).
3. **`RAGDOTNET_INTEGRATION` unset** → no container is started at all (an InMemory/SQLite run stays Docker-free) and PgVector tests skip naming the variable. Test `Integration_off_starts_nothing` (Task 2).
4. **Both key bindings in one run** → exactly one container; each test still sees only its own collections. Test `Bindings_share_one_container` (Task 2).
5. **Hybrid on Postgres full-text search** → the exact-term suite test passes through the native path; any tokenizer difference is documented, not hidden. Suite test `Exact_term_query_finds_its_chunk_with_hybrid` (Task 3).

---

### Task 1: Core stores dates in UTC

**Files:**
- Modify: `src/RagDotNet.Core/Ingestion/Ingestor.cs` (`BuildMetadata`), `src/RagDotNet.Core/Filtering/RagFilterTranslator.cs` (`ConvertValue`)
- Test: `tests/RagDotNet.Core.Tests/Filtering/UtcDateTests.cs`

**Why:** `CommunityToolkit.VectorData.PgVector` throws "DateTimeOffset with non-zero offset is not supported with PostgreSQL" on write and on filter parameters. Spec §2 says Core absorbs differences it can; the instant is unchanged, and SQLite already returns UTC, so every store now behaves the same.

**Interfaces:**
- Produces: metadata values of declared type `DateTimeOffset` reach the store as `value.ToUniversalTime()`; `RagFilterTranslator.ConvertValue(..., typeof(DateTimeOffset), ...)` returns UTC for every accepted input (DateTimeOffset, UTC/local DateTime, ISO string with offset). The in-memory keyword index's evaluator goes through `ConvertValue`, so it agrees.

- [ ] **Step 1: Write the failing tests** (in-memory store, public API)

| Test | Assertions |
|---|---|
| `Metadata_dates_are_stored_in_utc` | ingest with `d = 2026-01-01T10:30:15.123+05:30`; the retrieved hit's `Metadata["d"]` is a `DateTimeOffset` with `Offset == TimeSpan.Zero` and the same `UtcTicks` |
| `Date_filter_values_are_compared_in_utc` | `RagFilter.Eq("d", <same instant at -07:00>)` and `Eq("d", "2026-01-01T05:00:15.123Z")` both find the document; the vector filter the store receives (capture via a recording store or `RagFilterTranslator.Translate` constant) holds an offset-zero value |
| `Manifest_dates_are_stored_in_utc` | after ingest, the manifest record's `meta_d` (read through `IRagAdmin` listing or the manifest collection) has offset zero |

- [ ] **Step 2: Run; they fail** (offset +05:30 comes back). `dotnet test --project tests/RagDotNet.Core.Tests --filter-class "*UtcDateTests"`.
- [ ] **Step 3: Implement** — `BuildMetadata`: a `DateTimeOffset` value → `ToUniversalTime()`; `ConvertValue`: the `DateTimeOffset` result → `ToUniversalTime()` (both the already-typed and converted paths).
- [ ] **Step 4: Run Core tests and the store tests** (InMemory, NativeHybridFake, SQLite stay green). The re-ingest of a document whose only change is offset normalization is a one-time metadata update; no test may rely on the original offset.
- [ ] **Step 5: Commit** — `fix(core): store DateTimeOffset metadata and filter values in UTC`

---

### Task 2: A shared pgvector container for the store tests

**Files:**
- Create: `tests/RagDotNet.StoreTests/Infrastructure/PostgresContainer.cs`, `tests/RagDotNet.StoreTests/PostgresContainerTests.cs`
- Modify: `tests/RagDotNet.StoreTests/RagDotNet.StoreTests.csproj`, `Directory.Packages.props` (both packages), `docker/sdk/run.ps1`, `docker/sdk/README.md`

**Interfaces:**
- Produces:

```csharp
namespace RagDotNet.StoreTests.Infrastructure;

/// One pgvector container per test run, started on first use (never when RAGDOTNET_INTEGRATION is unset).
internal static class PostgresContainer
{
    public const string Image = "pgvector/pgvector:0.8.7-pg17";
    // Starts the container once (Lazy<Task>), runs CREATE EXTENSION IF NOT EXISTS vector, returns its connection string;
    // or the reason it could not: "RAGDOTNET_INTEGRATION is not set" / "Docker is not available: <message>".
    public static Task<(string? ConnectionString, string? UnavailableReason)> GetAsync();
    internal static int Starts { get; }          // for tests: how many containers this run started
}

// Assembly fixture that disposes the container at the end of the run if it was started.
[assembly: AssemblyFixture(typeof(PostgresContainerLifetime))]
```

- Testcontainers' Ryuk also removes the container if the process dies.

- [ ] **Step 1: Write the failing tests** in `PostgresContainerTests.cs`

| Test | Assertions |
|---|---|
| `Integration_off_starts_nothing` | with `RAGDOTNET_INTEGRATION` unset (skip this test when it is set): `GetAsync()` → reason contains `RAGDOTNET_INTEGRATION`, `Starts == 0` |
| `Unstartable_container_names_the_cause` | the start path with an image that cannot exist (an internal overload taking the image name) → reason contains `Docker` and the error message; no exception escapes |
| `Bindings_share_one_container` | integration on (skip otherwise): two `GetAsync()` calls → same connection string, `Starts == 1`, and `SELECT extversion FROM pg_extension WHERE extname = 'vector'` returns `0.8.7` |

- [ ] **Step 2: Run; fail** (type missing).
- [ ] **Step 3: Implement** with `PostgreSqlBuilder().WithImage(Image)`; any exception from build/start becomes the "Docker is not available" reason.
- [ ] **Step 4: Local Docker access.** `run.ps1` (and the bash twin) mounts `/var/run/docker.sock` and sets `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`, so Testcontainers inside the SDK container starts sibling containers and reaches their mapped ports. One line in `docker/sdk/README.md`. Run with `RAGDOTNET_INTEGRATION=1`: all three tests pass (one skipped by design per mode). Expected: the container starts in under a minute.
- [ ] **Step 5: Commit** — `test(stores): one shared pgvector container per run, started on first use`

---

### Task 3: The shared suite on PgVector

**Files:**
- Create: `tests/RagDotNet.StoreTests/Stores/PgVector.cs`
- Modify: `tests/RagDotNet.StoreTests/RagDotNet.StoreTests.csproj` (`CommunityToolkit.VectorData.PgVector`); Core or the suite only as triage below requires

**Interfaces:**
- Consumes: `PostgresContainer.GetAsync()` (Task 2), `StoreFixture`/`StoreSuite<T>` (slice 1).
- Produces:

```csharp
public sealed class PgVectorFixture : StoreFixture
{
    Name => "PgVector"; NativeHybrid => true;   // PostgresCollection implements HybridSearchAsync
    InitializeAsync: (cs, reason) = await PostgresContainer.GetAsync(); UnavailableReason = reason.
    CreateStore() => new PostgresVectorStore(cs);   // one data source per test; the suite disposes it
}
public sealed class PgVectorStringKeyTests(PgVectorFixture f) : StoreSuite<PgVectorFixture>(f, RagKeyType.String);
public sealed class PgVectorGuidKeyTests(PgVectorFixture f) : StoreSuite<PgVectorFixture>(f, RagKeyType.Guid);
```

- [ ] **Step 1: Add the fixture and bindings; run the suite with `RAGDOTNET_INTEGRATION=1`**, filtered to `RagDotNet.StoreTests.Stores.PgVector*`. Expected 78 tests per framework.
- [ ] **Step 2: Triage every failure, one at a time, by spec §2.1.** For each: reproduce in the smallest form, then
  - a Core bug or a Core-absorbable difference → failing Core unit test first, fix, all stores green;
  - a setting PgVector needs (for example a distance function) → `Fixture.Configure` + the docs, with a ruling;
  - a minor gap → documented `NotSupportedException` and a "known differences" line;
  - a core-feature gap that cannot be worked around → stop: PgVector is not marked supported, the binding is removed, an upstream issue text is drafted, and the user is told before the PR.
  Every case gets a ledger ruling naming the failing test, the cause, and the choice.
- [ ] **Step 3: Whole store-tests project with integration on.** Expected `3 + 3 (container tests) + 39 × 7` per framework, 0 failed; record the exact count. Then without `RAGDOTNET_INTEGRATION`: PgVector tests skip with the reason, nothing else changes, no container starts.
- [ ] **Step 4: Commit** — `test(stores): run the shared suite against PostgreSQL with pgvector`

---

### Task 4: CI and docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `docs/vector-stores.md`, `README.md` (only if a store sentence needs it)

- [ ] **Step 1: CI.** `store-tests` floor = the Task 3 integration count for net8.0; the comment lists the 7 bindings and the container tests. The job already sets `RAGDOTNET_INTEGRATION=1` and `RAGDOTNET_STORES_REQUIRED=1` and has Docker. Windows and macOS keep their SQLite-only filters (no Linux containers there).
- [ ] **Step 2: Docs.** `docs/vector-stores.md`: the PostgreSQL row (package, keys String and Guid, hybrid "Native (PostgreSQL full-text search)", status "Tested") and a "PostgreSQL (pgvector)" section — setup with `new PostgresVectorStore(connectionString)`; the database needs the `vector` extension (`CREATE EXTENSION vector`, by a user allowed to); settings found in Task 3 (or "none beyond the defaults"); known differences found in Task 3; and a line under per-store behavior that metadata dates come back in UTC on every store (Task 1). "How stores are tested": Docker stores and the local `run.ps1` socket mount.
- [ ] **Step 3: Whole solution in Docker with integration on.** 0 failed.
- [ ] **Step 4: Commit, push, open the PR** "Stage 8 slice 3: PostgreSQL + pgvector". Done only when all four CI jobs pass and the store job reports the new floor.
