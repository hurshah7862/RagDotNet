# Stage 8 slice 6: SQL Server 2025 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The shared store suite passes against SQL Server 2025 through `CommunityToolkit.VectorData.SqlServer`, with Guid keys and native hybrid search, in the CI store job on every pull request. A wrong key type or distance function fails before any data is written, with an error naming the setting.

**Architecture:**
- **One Core change, in `StoreCollections`:** creating the two collections becomes one step that proves the settings before RagDotNet writes anything:
  1. Check whether each collection exists. A store that can't be reached fails here, unchanged.
  2. Create the missing collections.
  3. Run a one-result vector search on the document list's 1-dimension placeholder, which tests the distance function.

  If any of this rejects the settings, Core removes the collections it created in this call and throws the existing "rejected RagDotNet's collection" error. That error now names both `RagOptions.KeyType` and `RagOptions.DistanceFunction`.
- **Test-side:** SQL Server comes from a pinned 2025 image with Full-Text Search added (a Dockerfile built by Testcontainers), through the existing `SharedContainer<T>`.

**Tech Stack:** CommunityToolkit.VectorData.SqlServer 1.0.1, Testcontainers.MsSql 4.16.0, base image `mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04` plus `mssql-server-fts=17.0.5065.9-3`, xUnit v3, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` §2, §2.1, §2.2, §4, §5, §7, §8 (slice 6).

## What the spike found (2026-11-23, throwaway branch, nothing kept)

The suite was bound to SQL Server (Testcontainers.MsSql, String and Guid keys).
- **The container's connection string points at `master`**, where full-text search is not allowed. The test container needs its own database (`CREATE DATABASE rag`).
- **The official image has no Full-Text Search** ("Full-Text Search is not installed"). The connector creates a full-text index on the chunk text, because Core marks it `IsFullTextIndexed`, and implements native hybrid search with `FREETEXTTABLE`. An image built `FROM` the pinned 2025 image with `mssql-server-fts` at the server's exact version (17.0.5065.9-3) works; building it took about 4.5 minutes locally.
- **String keys can't work while the text has a full-text index.**
  - The connector maps string keys to `NVARCHAR(4000)`, and SQL Server refuses that primary key as a full-text key (the maximum is 900 bytes).
  - The connector has no setting for the key length.
  - SQL Server therefore needs `RagOptions.KeyType = RagKeyType.Guid`, like Qdrant.
- **`CosineSimilarity` is not supported; `CosineDistance` is.** RagDotNet's default is `CosineSimilarity`.
- **Results on the full-text image with `CosineDistance`:**
  - Guid keys: 38 of 39 suite tests passed. The other one failed only because the spike fixture said "no native hybrid".
  - String keys: all 39 failed on the full-text key.
- **How the wrong settings fail today. Both break spec §2.2:**
  - **Guid keys with `CosineSimilarity`:** the collections are created and ingest succeeds. Only the first retrieve fails, with a bare `NotSupportedException: Distance function CosineSimilarity is not supported.` By then data is written, and the error names no setting.
  - **String keys:** the first ingest fails with `VectorStoreException("Call to vector store failed.")`, which wraps the full-text-key `SqlException` and names no setting. **The chunks table is left behind.**
- **A probe:** a one-result vector search on an *empty* collection already throws the distance `NotSupportedException`, so a startup search catches a wrong distance before any write.

## Global Constraints

- Branch `stage8-slice6-sqlserver` from `main` (33c4493); the first commit adds this plan.
- Build and test in Docker (the bash twin of `./docker/sdk/run.ps1` with the Docker socket and `TESTCONTAINERS_HOST_OVERRIDE`); local `dotnet` is blocked by App Control.
- Spec §7: 16 GB dev machine, so run test projects one at a time. SQL Server needs about 2 GB in its container.
- Pinned:
  - `CommunityToolkit.VectorData.SqlServer` 1.0.1, published 2026-10-14.
  - `Testcontainers.MsSql` 4.16.0, matching the other Testcontainers packages; 4.17.0 is a week old.
  - Base image `mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04`, created 2026-11-05.
  - `mssql-server-fts=17.0.5065.9-3`, the server's exact build.
- **Spec §2.1:** work around a gap in Core first, with a failing test first, keeping every store that passes today green. Never fork the connector.
- **Spec §2.2:**
  - SQL Server's settings, `KeyType = Guid` and `DistanceFunction = CosineDistance`, are documented per store.
  - A wrong setting fails at startup, or at the first collection call at the latest, with an error naming the setting.
  - Core does not detect the store, and the public API does not change.
- Spec §4: no store-specific expectations in the shared suite; CI fails a store that cannot start.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **The startup probe search on stores that pass today** → it must succeed on an empty or full document list on InMemory, SQLite, PgVector, Qdrant and Redis. A store that rejected it would break every app. The whole store suite with integration on is the test (Task 1 Step 4, Task 2 Step 3).
2. **A store that is down** → the error passes through unchanged, since it fails at the existence check. The existing `Connection_errors_are_not_reworded` keeps covering it, with its failure moved to the existence check (Task 1).
3. **Collections that existed before a rejected startup** → never deleted. Only collections created in this call are removed. Test `Existing_collections_are_kept_when_startup_is_rejected` (Task 1).
4. **SQL Server's full-text index fills in the background** → a chunk ingested moments ago may be missed by the keyword leg of hybrid search for a few seconds (the vector leg still finds it). This is documented as a known difference. If the suite's exact-term hybrid test is flaky on SQL Server, that is a finding to report, not to hide with a store-specific wait (Task 3 docs; Task 2 Step 3 runs the binding twice).
5. **A SQL Server without Full-Text Search** → the first call fails with the "rejected RagDotNet's collection" error carrying SQL Server's "Full-Text Search is not installed". The docs list Full-Text Search as a prerequisite, and External content mode (no text column) does not need it (Task 3).

---

### Task 1: Startup proves the settings before anything is written

**Files:**
- Modify:
  - `src/RagDotNet.Core/Storage/StoreCollections.cs` (one `EnsureExistsAsync` over both collections, the probe search, cleanup, message)
  - `ChunkStore.cs` / `ManifestStore.cs` (expose `internal VectorStoreCollection<object, Dictionary<string, object?>> Collection`, and drop their own `EnsureExistsAsync`)
  - `Pipeline/RagEngine.cs:171-172` (one call)
- Test:
  - `tests/RagDotNet.Core.Tests/StoreSettingsErrorTests.cs`
  - `Fakes/FailingVectorStore.cs`: add `FailCollectionExists` (thrown by `CollectionExistsAsync` on collections and the store) and `RejectSearch` (an exception thrown when a search is enumerated)

**Interfaces:**
- Produces: `internal static Task StoreCollections.EnsureExistsAsync(VectorStoreCollection<object, Dictionary<string, object?>> chunks, VectorStoreCollection<object, Dictionary<string, object?>> manifests, RagOptions options, CancellationToken ct)`, which runs in this order:
  1. `CollectionExistsAsync` for both. Any exception passes through unchanged.
  2. `EnsureCollectionExistsAsync` for each that didn't exist.
  3. `manifests.SearchAsync(new ReadOnlyMemory<float>([1f]), 1)`, enumerated to the end.
  4. If step 2 or 3 throws `NotSupportedException`, `ArgumentException` or `VectorStoreException`:
     - call `EnsureCollectionDeletedAsync` on each collection this call created, ignoring a failure of the delete itself;
     - throw `Rejected(name of the collection that failed, ...)` with the original as inner. Use the chunks name for a step-2 failure on chunks, and the documents name otherwise.
  5. Any other exception passes through unchanged.
- The rejection message becomes: `"The vector store rejected RagDotNet's '{name}' collection (key type {KeyType}, distance {DistanceFunction}, {EmbeddingDimensions} dimensions): {inner.Message} Some stores need other settings, such as RagOptions.KeyType or RagOptions.DistanceFunction; see docs/vector-stores.md."`

- [ ] **Step 1: Failing tests** in `StoreSettingsErrorTests`:

| Test | Setup → assertions |
|---|---|
| `Creation_failure_on_a_reachable_store_names_the_settings` | `FailEnsureExists = new VectorStoreException("Call to vector store failed.", new InvalidOperationException("full-text key"))` → ingest throws `InvalidOperationException` starting "The vector store rejected RagDotNet's 'test' collection (key type String, distance CosineSimilarity", containing "RagOptions.KeyType", "RagOptions.DistanceFunction" and "docs/vector-stores.md"; `InnerException` is the same instance |
| `Distance_rejected_by_search_fails_before_any_write` | `RejectSearch = new NotSupportedException("Distance function CosineSimilarity is not supported.")` → ingest throws the rejection naming `RagOptions.DistanceFunction` and the `test_documents` collection; afterwards neither `test` nor `test_documents` exists in the inner store, and no chunk was written |
| `Rejected_creation_removes_the_collections_it_created` | `FailEnsureExists` set only for the `_documents` collection (a name filter on the fake) → after the throw, `test` (created in this call) no longer exists |
| `Existing_collections_are_kept_when_startup_is_rejected` | Build and ingest once with a clean fake. Then a new pipeline on the same inner store with `RejectSearch` set → throws the rejection; `test` and `test_documents` still exist and still hold the document |
| `Connection_errors_are_not_reworded` (changed) | the failures (`HttpRequestException`, `VectorStoreException("store is down")`) move from `FailEnsureExists` to `FailCollectionExists` → `Assert.Same(failure, e)` |

  `Definition_rejection_names_the_settings` and `Creation_rejection_names_the_settings` stay. Update their message assertions only where the hint text changed.

  Expected now: compile errors on the new fake members, then FAIL on the first four tests.
- [ ] **Step 2: Implement.** `RagEngine.EnsureReadyAsync` calls `StoreCollections.EnsureExistsAsync(Chunks.Collection, Manifests.Collection, Options, ct)` once. Core tests green.
- [ ] **Step 3: Commit**: `fix(core): prove the store settings at startup and leave nothing behind when they are rejected`
- [ ] **Step 4: SQLite connector tests, then the store tests with integration on, one project at a time:**
  - Expected: SQLite connector 60/60.
  - Stores: 403 total, 400 run, as before. This is Review Focus 1.
  - The Qdrant settings test still passes. Its assertions (`StartsWith ... (key type String`, `RagOptions.KeyType`, `docs/vector-stores.md`) still hold.

### Task 2: The shared suite on SQL Server

**Files:**
- Create:
  - `tests/RagDotNet.StoreTests/Stores/SqlServer.cs`
  - `tests/RagDotNet.StoreTests/Infrastructure/SqlServer/Dockerfile`
- Modify:
  - `Infrastructure/SharedContainer.cs`: an optional `prepare` step, `Containers.SqlServer`, and `ContainerLifetime`
  - `SharedContainerTests.cs` (the theory gains "SqlServer")
  - `RagDotNet.StoreTests.csproj` (copy the Dockerfile to the output)
  - `Directory.Packages.props`

**Interfaces:**
- Produces:
  - **The `prepare` step:** `SharedContainer<TContainer>` gains an optional `Func<string, Task<string>>? prepare` constructor parameter, after `ready`.
    - It turns the requested image into the image to run. The default is the identity.
    - It runs inside `StartAsync`'s try, so a failed build becomes the unavailable reason (Docker, the image, the store).
  - **The Dockerfile:**
    - `ARG BASE_IMAGE`, then `FROM ${BASE_IMAGE}`.
    - As root: add Microsoft's `mssql-server-2025` apt source for Ubuntu 24.04 (`gpg --batch --yes --dearmor`), then `apt-get install -y --no-install-recommends mssql-server-fts=17.0.5065.9-3`.
    - Back to `USER mssql`.
  - **`Containers.SqlServer`:** `SharedContainer<MsSqlContainer>`:
    - Name "SqlServer"; image `mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04`.
    - `prepare` builds the Dockerfile with `ImageFromDockerfileBuilder` (`BASE_IMAGE` = the requested image), tagged `ragdotnet-mssql-fts:2025-cu11`, and returns that name.
    - `build` is `new MsSqlBuilder(image).Build()`.
    - `ready` runs `CREATE DATABASE rag;` and returns the connection string with `InitialCatalog = "rag"`.
  - **`SqlServerFixture : StoreFixture`:**
    - `Name` is "SqlServer"; `NativeHybrid` is true (`SqlServerCollection` implements `IKeywordHybridSearchable`).
    - `Configure` sets `DistanceFunction = DistanceFunction.CosineDistance`.
    - `CreateStore()` returns `new SqlServerVectorStore(connectionString)`.
  - **Bindings:** `SqlServerGuidKeyTests` only. Add a comment like Qdrant's: string keys can't be a full-text key.
  - **`SqlServerSettingsTests`:** two tests.

- [ ] **Step 1: The container, test first.** `SharedContainerTests` gains "SqlServer":
  - Integration off starts nothing.
  - An unstartable image (`ragdotnet-test/does-not-exist:0` as the base) names its cause.
  - Bindings share one container.
  - The version check is a SQL query (`SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')`), which must return `17` and `1`.
- [ ] **Step 2: Settings tests, then the binding:**
  - `String_keys_fail_naming_the_setting`:
    - `KeyType = String`, `CosineDistance`, then ingest.
    - It throws `InvalidOperationException` starting `"The vector store rejected RagDotNet's '{collection}' collection (key type String"` and containing `RagOptions.KeyType` and `docs/vector-stores.md`.
    - Neither collection exists afterwards.
  - `Cosine_similarity_fails_naming_the_setting`:
    - Guid keys and the default `CosineSimilarity`, then ingest.
    - It throws the rejection naming `RagOptions.DistanceFunction`.
    - Neither collection exists afterwards, and nothing was written.
- [ ] **Step 3: Run SQL Server's tests with integration on, twice:**
  - Expected: 39 suite tests, 2 settings tests and 3 container tests pass, both times.
  - Run twice to catch a flaky hybrid test (Review Focus 4).
  - Triage anything new by spec §2.1, test first, with a ledger ruling per failure.
  - If a core-feature gap remains, stop and tell the user before any PR.
- [ ] **Step 4: Whole store-tests project, net8.0, integration on:**
  - Total: 403 + 39 + 2 + 3 = 447, with 4 skipped (the integration-off checks), so 443 run.
  - Without integration, nothing starts.
- [ ] **Step 5: Commit**: `test(stores): run the shared suite against SQL Server 2025 (Guid keys, CosineDistance, native hybrid)`

### Task 3: CI and docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `docs/vector-stores.md`

- [ ] **Step 1: CI.** The store-tests floor becomes 443. Update the comment to: 3 gate + 8 container + 39 × 11 bindings + 1 Qdrant + 2 SQL Server settings tests. The SQL Server image is built in the job, which takes a few minutes.
- [ ] **Step 2: Docs:**
  - **The SQL Server row:** Guid only; hybrid "Native (SQL Server full-text search)"; "Tested".
  - **The per-store settings table:** SQL Server needs `KeyType = Guid` and `DistanceFunction = DistanceFunction.CosineDistance`, both required.
  - **A "SQL Server" section:**
    - setup with `new SqlServerVectorStore(connectionString)` and both settings;
    - prerequisites: SQL Server 2025 or Azure SQL with the vector type, Full-Text Search installed, and a user database, not `master`;
    - External content mode stores no text, so it needs no Full-Text Search;
    - what each wrong setting reports.
  - **SQL Server known differences:**
    - the full-text index fills in the background, so the keyword leg can miss a chunk for a few seconds after ingest;
    - `IRagAdmin.RebuildKeywordIndexAsync` throws, since keyword search is native;
    - word splitting follows SQL Server's full-text rules.
  - **"Per-store settings":** a wrong setting now fails on the first call before anything is written, and RagDotNet removes collections it created in that call.
  - **"How stores are tested":** the image is built from the pinned base plus Full-Text Search.
- [ ] **Step 3: Each test project, one at a time, in Docker, integration on: 0 failed.**
- [ ] **Step 4: Commit, push, open the PR** "Stage 8 slice 6: SQL Server 2025". Done when all four CI jobs pass.
