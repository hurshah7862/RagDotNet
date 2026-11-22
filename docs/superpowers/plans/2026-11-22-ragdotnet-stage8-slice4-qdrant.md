# Stage 8 slice 4: Qdrant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The shared store suite passes against Qdrant through `CommunityToolkit.VectorData.Qdrant`, with Guid keys and native hybrid search, in the CI store job on every pull request.

**Architecture:** Two Core changes, both found by a throwaway spike (below):
- **Document listing pages by a numeric field.** The document list currently pages with `OrderBy Key`, and Qdrant cannot order by the point id. Listing will instead page by a new 64-bit `ListOrder` field on each document record, derived from its key.
- **Definition rejections name the settings.** A store that rejects RagDotNet's collection definition (wrong key type, distance or dimensions) gets an error naming `RagOptions.KeyType` and `RagOptions.DistanceFunction`, as spec §2.2 requires.

The store tests get a Qdrant container shared like the pgvector one, through one generic shared-container helper.

**Tech Stack:** CommunityToolkit.VectorData.Qdrant 1.0.1 (Qdrant.Client 1.18.1), Testcontainers.Qdrant 4.16.0, image `qdrant/qdrant:v1.20.1`, xUnit v3, GitHub Actions (ubuntu, Docker).

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` §2, §2.1, §2.2, §4, §5, §7, §8 (slice 4).

## What the spike found (2026-11-22, throwaway branch, nothing kept)

The suite was bound to Qdrant with Guid keys, and 32 of 39 tests passed, including every filter case, the access-scope tests, and native hybrid search on the exact-term test. All 7 failures had one cause:
- **The cause:** `ManifestStore.ListAsync` orders by `r["Key"]`, and Qdrant rejects that: "No range index for `order_by` key: `Key`". The key is the point id, not a payload field.
- **The 7 tests:** stats ×2, delete, external content, listing, reindex ×2. The reindex-reads test timed out for the same reason.

A probe on a Guid-keyed collection, with an indexed `long` field and Core's `ChunkCount >= 0` filter, showed that:
- `OrderBy Ascending` on the `long` field sorts correctly, including `long.MinValue` and `long.MaxValue`.
- `Skip` combined with that order returns the right page.

## Global Constraints

- Branch `stage8-slice4-qdrant` from `main` (9ea714e); the first commit adds this plan.
- Build and test in Docker (`./docker/sdk/run.ps1`, or a bash twin with the Docker socket and `TESTCONTAINERS_HOST_OVERRIDE`); local `dotnet` is blocked by App Control.
- Pinned: `CommunityToolkit.VectorData.Qdrant` 1.0.1, `Testcontainers.Qdrant` 4.16.0 (4.17.0 is six days old), image `qdrant/qdrant:v1.20.1`.
- **Spec §2.1, in this order:**
  1. Work around the gap in Core: failing test first, and every store that passes today stays green.
  2. If a core feature still can't work, Qdrant is not marked supported.
  3. A minor gap may ship as a documented `NotSupportedException`.
  4. Never fork the connector.
- Spec §2.2: Qdrant needs `RagKeyType.Guid`. That requirement is documented, and only a Guid binding exists. A wrong key type fails at build or the first store call, with an error naming `RagOptions.KeyType`.
- Spec §4: no store-specific expectations in the shared suite; CI fails a store that cannot start.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **Listing more than one page while documents change** (reindex rewrites every document record) → every document is listed exactly once, on every store. Covered by the suite's `Listing_sees_every_document_across_pages` and both reindex tests, on all bindings (Task 3), plus `Listing_pages_by_list_order` (Task 1).
2. **Data written before `ListOrder` existed** (InMemory, SQLite or PostgreSQL collections created by earlier builds) → those documents stay listable. A document record without `ListOrder` gets one the next time it is written. Nothing is released yet, so an older collection only needs a re-ingest. The docs and the PR say so. Test `Records_without_list_order_are_still_listed` (Task 1), on InMemory.
3. **An app on Qdrant that forgets `RagKeyType.Guid`** → the error names `RagOptions.KeyType` and the vector-stores docs, never only the connector's "Key properties must be either ulong or Guid". Test `String_keys_fail_naming_the_setting` (Task 3).
4. **A store error that is not a definition problem** (network down, auth) → it is not reworded as a settings problem. Test `Connection_errors_are_not_reworded` (Task 2).
5. **Both PgVector and Qdrant in one run** → one container each, both started lazily, and neither starts without `RAGDOTNET_INTEGRATION`. Tests in `SharedContainerTests` (Task 3).

---

### Task 1: Document listing pages by `ListOrder`

**Files:**
- Modify: `src/RagDotNet.Core/Storage/RecordDefinitions.cs` (Manifests: `ListOrder` `long`, `IsIndexed`), `ManifestRecord.cs` (`ToDynamic` writes it), `ManifestStore.cs` (`ListAsync` orders by it)
- Create: `src/RagDotNet.Core/Storage/ListOrder.cs`
- Test: `tests/RagDotNet.Core.Tests/ManifestListingTests.cs`

**Interfaces:**
- Produces: `internal static class ListOrder { public static long Of(string manifestKey); }` returns the first 8 bytes of SHA-256(UTF-8 key), read as a little-endian `long`. It is deterministic across processes and platforms, and keys are hashed in their string form, before any Guid conversion. `ManifestStore.ListAsync` keeps Skip paging with pages of 100, ordered by `ListOrder` ascending.

- [ ] **Step 1: Write the failing tests**

| Test | Assertions |
|---|---|
| `List_order_is_stable_and_spreads_keys` | `ListOrder.Of("a") == ListOrder.Of("a")`. 10,000 distinct keys give 10,000 distinct values. A pinned value: `ListOrder.Of("tenant=a\|doc1")` equals the constant computed once and written into the test |
| `Manifest_records_carry_their_list_order` | after ingest on InMemory, the `_documents` record has `ListOrder == ListOrder.Of(<its string key>)` for String and Guid keys |
| `Listing_pages_by_list_order` | a recording `VectorStore` wrapper captures `FilteredRecordRetrievalOptions.OrderBy`. Evaluated on a fresh `OrderByDefinition`, it has one ascending sort whose selector reads `"ListOrder"` |
| `Records_without_list_order_are_still_listed` | an InMemory document record upserted without `ListOrder` (raw, as an older build wrote it) still appears in `IRagAdmin.GetStatsAsync`. Expected now: FAIL, if the store drops records without the sort field; then the fix is ordering that puts nulls in, or a documented re-ingest. Decide by the result and record a ruling |

- [ ] **Step 2: Run; they fail.** **Step 3: Implement.** **Step 4: Core tests and the whole store-tests project with integration on (InMemory, NativeHybridFake, SQLite, PgVector) stay green.**
- [ ] **Step 5: Commit**: `fix(core): page the document list by a stable numeric ListOrder, not the key`

---

### Task 2: Definition rejections name the settings

**Files:**
- Modify: `src/RagDotNet.Core/Storage/ChunkStore.cs`, `ManifestStore.cs` (collection creation and `EnsureExistsAsync`)
- Test: `tests/RagDotNet.Core.Tests/StoreSettingsErrorTests.cs`

**Interfaces:**
- Produces: when `GetDynamicCollection` or `EnsureCollectionExistsAsync` throws `NotSupportedException` or `ArgumentException`, Core throws an `InvalidOperationException` with the original exception as its inner exception and this message:
  > "The vector store rejected RagDotNet's '{collection}' collection (key type {KeyType}, distance {DistanceFunction}, {Dimensions} dimensions): {inner.Message} Some stores need other settings, for example RagOptions.KeyType = RagKeyType.Guid; see docs/vector-stores.md."

  Any other exception type passes through unchanged.

- [ ] **Step 1: Failing tests** with a fake store whose `GetDynamicCollection` (or `EnsureCollectionExistsAsync`) throws:
  - `Definition_rejection_names_the_settings`: a `NotSupportedException` comes back as `InvalidOperationException`; the message contains `RagOptions.KeyType`, `docs/vector-stores.md` and the inner message; `InnerException` is the original.
  - `Connection_errors_are_not_reworded`: an `HttpRequestException` or `VectorStoreException` from `EnsureCollectionExistsAsync` reaches the caller as the same exception.
- [ ] **Step 2–4:** fail → implement → Core and store tests green.
- [ ] **Step 5: Commit**: `fix(core): a store that rejects the collection definition gets an error naming the settings`

---

### Task 3: The shared suite on Qdrant

**Files:**
- Create: `tests/RagDotNet.StoreTests/Infrastructure/SharedContainer.cs`, `tests/RagDotNet.StoreTests/Stores/Qdrant.cs`, `tests/RagDotNet.StoreTests/SharedContainerTests.cs`
- Modify: `Infrastructure/PostgresContainer.cs` (use the helper), `PostgresContainerTests.cs` (merged into `SharedContainerTests`), `RagDotNet.StoreTests.csproj`, `Directory.Packages.props`

**Interfaces:**
- Produces:

```csharp
// One container per store per run; started on first use; never without RAGDOTNET_INTEGRATION; any start failure is the reason.
internal sealed class SharedContainer<TContainer>(string store, string image, Func<string, TContainer> build, Func<TContainer, Task<string>> ready)
    where TContainer : IContainer
{
    public Task<(string? Endpoint, string? UnavailableReason)> GetAsync();
    internal int Starts { get; }
    public ValueTask DisposeAsync();
}
internal static class Containers { public static SharedContainer<PostgreSqlContainer> Postgres; public static SharedContainer<QdrantContainer> Qdrant; }
// One assembly fixture disposes every started container.

public sealed class QdrantFixture : StoreFixture
{
    Name => "Qdrant"; NativeHybrid => true;
    CreateStore() => new QdrantVectorStore(new QdrantClient(new Uri(grpcEndpoint)), ownsClient: true);
}
public sealed class QdrantGuidKeyTests(QdrantFixture f) : StoreSuite<QdrantFixture>(f, RagKeyType.Guid);   // no String binding: spec §2.2
```

- [ ] **Step 1: The helper, test first.** `SharedContainerTests` keeps the three PgVector container tests, run once per store (theory over "PgVector" and "Qdrant"). It also fixes two deferred minors from slice 3: the unshared failure path no longer reads a disposed container, and the Docker-down case goes through `StoreGate.Check` (skip locally, fail when required).
- [ ] **Step 2: Bind Qdrant; run the suite with `RAGDOTNET_INTEGRATION=1`.** Expected: 39 pass after Tasks 1 and 2. Triage anything new by spec §2.1, test first, with a ledger ruling per failure, as in slice 3. If a core-feature gap remains, stop and tell the user before any PR.
- [ ] **Step 3: `String_keys_fail_naming_the_setting`** in `Stores/Qdrant.cs` (Qdrant-only, outside the shared suite): building and first use with `RagKeyType.String` → `InvalidOperationException` whose message contains `RagOptions.KeyType` (from Task 2).
- [ ] **Step 4: Whole store-tests project with integration on**; record the count: `3 gate + 6 container + 39 × 8 + 1` per framework. Without integration, nothing starts and the Docker tests skip with the reason.
- [ ] **Step 5: Commit**: `test(stores): run the shared suite against Qdrant (Guid keys, native hybrid)`

---

### Task 4: CI and docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `docs/vector-stores.md`

- [ ] **Step 1: CI.** The store-tests floor becomes the Task 3 count for net8.0, with the comment updated to list the bindings.
- [ ] **Step 2: Docs.**
  - The Qdrant row: package, Guid keys only, hybrid "Native", status "Tested".
  - A "Qdrant" section:
    - setup with `new QdrantVectorStore(new QdrantClient("host"), ownsClient: true)` and **`RagOptions.KeyType = RagKeyType.Guid` (required)**, with the error an app gets without it;
    - which keyword matching Qdrant's hybrid search does, as found in Task 3;
    - known differences found in Task 3.
  - "Per-store settings": Qdrant's key type, now real.
  - A note that document records carry `ListOrder`, and that collections created before this slice need a re-ingest. This applies only if Task 1's ruling says so.
- [ ] **Step 3: Whole solution in Docker with integration on: 0 failed.**
- [ ] **Step 4: Commit, push, open the PR** "Stage 8 slice 4: Qdrant". Done when all four CI jobs pass.
