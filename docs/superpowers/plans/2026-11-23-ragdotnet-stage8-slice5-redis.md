# Stage 8 slice 5: Redis Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The shared store suite passes against Redis Stack through `CommunityToolkit.VectorData.Redis` (JSON storage), with String and Guid keys, in the CI store job on every pull request.

**Architecture:** Three Core changes, all found by a throwaway spike (below), each uniform across stores (Core never detects the store):
- **Every field Core filters on is indexed.** Redis ignores a filter on a field that isn't indexed, which returns wrong results without an error. `ContentStorage` is the one filtered field that isn't indexed today.
- **The common area is stored with a non-empty partition value.** Redis can't query empty strings in an indexed field.
- **Bool and date metadata are stored as numbers.** The Redis connector indexes only strings, string arrays and numbers. Bools are stored as `int` 0/1, and dates as `long` microseconds since the Unix epoch, UTC. Core converts at the store boundary only, so its API, the keyword index and filter values keep the declared types.

The store tests get a Redis container through the existing `SharedContainer<T>`.

**Tech Stack:** CommunityToolkit.VectorData.Redis 1.0.0 (StackExchange.Redis), Testcontainers.Redis 4.16.0, image `redis/redis-stack-server:7.4.0-v8`, xUnit v3, GitHub Actions (ubuntu, Docker).

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` §2, §2.1, §2.2, §4, §5, §7, §8 (slice 5).

## What the spike found (2026-11-23, throwaway branch, nothing kept)

The suite was bound to Redis (JSON storage, String and Guid keys). 24 of 78 tests passed, and the failures had three causes:
- **The connector rejects the definition** for the suite's bool field (40 failures): "Property 'meta_b' is marked as IsIndexed, but the property type 'System.Nullable`1[System.Boolean]' is not supported. Only string, IEnumerable<string> and numeric properties are supported for filtering by the Redis VectorStore." `DateTimeOffset` is in the same position.
- **Common-area filters fail** (10 failures): `Partition == ""` gives "Use `INDEXEMPTY` in field creation in order to index and query for empty strings", and the connector has no way to set that.
- **The content-mode check misfires** (2 failures, plus 2 reindex timeouts): `ContentStorage != "VectorStore"` matched records whose value is "VectorStore", because `ContentStorage` isn't indexed and Redis ignores the condition.

A probe on a JSON collection showed:
- **What works:**
  - `Partition == "~"` and `!= "~"`.
  - Core's own `Convert(get_Item, int?) == Constant(int?)` shape, on `int?` and `long?` fields; a record missing the field never matches.
  - String-array `Contains`.
  - `ListOrder >= <large long>`.
  - `OrderBy ListOrder` with `Skip`.
- **What fails or misbehaves:**
  - A C# lambda `(int?)r["x"] == 1` throws, because the compiler wraps the constant in a Convert node. Core doesn't build that shape.
  - `r["x"] != null` throws.
  - A filter on a field that isn't indexed returns every record.
  - Hash storage fails outright, so JSON (the connector's default) is the mode to use.
- **Hybrid search:** `RedisCollection` has no native hybrid search, so the in-memory keyword index serves it; those suite tests passed.

## Global Constraints

- Branch `stage8-slice5-redis` from `main` (bf49c82); the first commit adds this plan.
- Build and test in Docker (the bash twin of `./docker/sdk/run.ps1` with the Docker socket and `TESTCONTAINERS_HOST_OVERRIDE`); local `dotnet` is blocked by App Control.
- Spec §7: the dev machine has 16 GB of memory, so run test projects one at a time (a whole-solution run was killed for memory in slice 4).
- Pinned:
  - `CommunityToolkit.VectorData.Redis` 1.0.0, the only stable release.
  - `Testcontainers.Redis` 4.16.0; 4.17.0 is a week old.
  - Image `redis/redis-stack-server:7.4.0-v8`, the spec's Redis Stack.
- **Spec §2.1, in this order:**
  1. Work around the gap in Core: failing test first, and every store that passes today stays green.
  2. If a core feature still can't work, Redis is not marked supported.
  3. A minor gap may ship as a documented `NotSupportedException`.
  4. Never fork the connector.
- Spec §2.2: Redis needs no setting beyond the defaults.
- Spec §4: no store-specific expectations in the shared suite; CI fails a store that cannot start.
- Collections written by earlier builds need re-ingesting (bool/date column types and the common-area value change); nothing is released, and `docs/vector-stores.md` "Collections from earlier builds" says so.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **Dates before 1970 or with sub-microsecond ticks** → a negative microsecond count round-trips to the same instant (floor, not truncation toward zero), and an `Eq` filter with the same `DateTimeOffset` matches the stored value even when its ticks have a sub-microsecond part. Tests `Dates_are_stored_as_unix_microseconds` and `Date_filters_match_values_with_finer_ticks` (Task 3).
2. **A real partition key equal to the common-area value** → impossible: every partition key has a `k=v` pair, so it contains `=`, and the stored common value `~common` has none. Test `Common_area_value_cannot_be_a_partition_key` (Task 2).
3. **`long` metadata beyond 2^53 on Redis** → Redis compares numbers as doubles, so two such values can compare equal. This is documented as a known difference, not hidden. There is no test: it is a store property, and the docs carry it (Task 5).
4. **An empty-string metadata value in a filter (`Eq("tenant", "")`) on Redis** → the store's own "INDEXEMPTY" error, documented as a known difference. Core can't detect the store, so it doesn't reword it (Task 5).
5. **A future Core filter on a field that isn't indexed** → caught by `Filtered_fields_are_indexed` (Task 1), which lists every field Core's filters read.

---

### Task 1: Every filtered field is indexed

**Files:**
- Modify: `src/RagDotNet.Core/Storage/RecordDefinitions.cs` (Manifests: `ContentStorage` gets `IsIndexed = true`)
- Test: `tests/RagDotNet.Core.Tests/StoreShapeTests.cs`

- [ ] **Step 1: Failing test** `Filtered_fields_are_indexed`:
  - In the chunks definition, `DocumentId`, `Partition`, `EmbeddingModelId` and every `meta_*` have `IsIndexed`.
  - In the manifests definition, `DocumentId`, `Partition`, `EmbeddingModelId`, `ChunkCount`, `ContentStorage`, `ListOrder` and every `meta_*` have `IsIndexed`.
  - Each list carries a comment naming where Core filters on the field.

  Expected now: FAIL on `ContentStorage`.
- [ ] **Step 2: Implement; Core tests green.**
- [ ] **Step 3: Commit**: `fix(core): index every field Core filters on (ContentStorage)`

### Task 2: The common area is stored as `~common`

**Files:**
- Create: `src/RagDotNet.Core/Storage/StoredPartition.cs`
- Modify: `Ingestion/Ingestor.cs` (chunk records), `Storage/ManifestRecord.cs` (`ToDynamic`, `FromDynamic`), `Access/ReadFilters.cs` (partition clause), `Pipeline/RagEngine.cs:302` (chunk key rebuilt from a stored record)
- Test: `tests/RagDotNet.Core.Tests/StoreShapeTests.cs`

**Interfaces:**
- Produces: `internal static class StoredPartition { public const string Common = "~common"; public static string ToStore(string partitionKey); public static string FromStore(string stored); }`.
  - `ToStore("")` returns `Common`; `FromStore(Common)` returns `""`.
  - Every other value passes through unchanged.
  - Record keys (`ChunkKeys`) don't change: they still use the empty partition key, so the public `ChunkKeys.Partition` is untouched.

- [ ] **Step 1: Failing tests:**
  - `Common_area_is_stored_with_a_non_empty_partition`: after ingesting a common-area document on InMemory, the raw chunk and manifest records have `Partition == "~common"`, and retrieve, stats and delete still see it.
  - `Read_filters_compare_no_empty_strings`: a visitor over `ReadFilters.Build`, for scoped with common, scoped without common, and full access without common, finds no `ConstantExpression` whose value is `""`.
  - `Common_area_value_cannot_be_a_partition_key`: for several partition dictionaries, including keys and values like `"~common"`, `ChunkKeys.Partition(d)` never equals `StoredPartition.Common`. A partition key always contains `=`.
- [ ] **Step 2: Implement.** Convert on write (chunk record, manifest record) and on read (`FromDynamic`, the `RagEngine` key rebuild). The partition clause compares to `StoredPartition.ToStore(...)`. The access tests in Core stay green unchanged.
- [ ] **Step 3: Commit**: `fix(core): store the common area with a non-empty partition value`

### Task 3: Bool and date metadata stored as numbers

**Files:**
- Create: `src/RagDotNet.Core/Storage/StoredValues.cs`
- Modify: `RecordDefinitions.StoredType`, `Ingestion/Ingestor.cs:141`, `Storage/ManifestRecord.cs` (metadata write and read), `Retrieval/Retriever.cs:440` (hit metadata), `Filtering/RagFilterTranslator.cs` (`BuildEq` compares the stored type with the stored value)
- Test: `tests/RagDotNet.Core.Tests/StoredValueTests.cs`, update `StoreShapeTests.Value_type_metadata_is_nullable_in_store_definitions`

**Interfaces:**
- Produces: `internal static class StoredValues { public static Type StoreType(Type declared); public static object? ToStore(object? value, Type declared); public static object? FromStore(object? stored, Type declared); }`:
  - `bool` → `int?` (1 or 0).
  - `DateTimeOffset` → `long?`: microseconds since `DateTimeOffset.UnixEpoch`, from `UtcTicks`, using floor division (`Math.DivRem` adjusted for negatives).
  - Any other value type → `Nullable<T>`, as today.
  - Reference types are unchanged.
  - `FromStore` accepts any numeric CLR type a connector returns (`int`, `long`, `double`) and returns the declared type; dates come back with offset zero.
  - `RecordDefinitions.StoredType` delegates to `StoreType`.

- [ ] **Step 1: Failing tests** (`StoredValueTests`):

| Test | Assertions |
|---|---|
| `Stored_types_are_strings_arrays_or_numbers` | `StoreType`: `bool` → `int?`, `DateTimeOffset` → `long?`, `int` → `int?`, `long` → `long?`, `string` → `string`, `string[]` → `string[]` |
| `Dates_are_stored_as_unix_microseconds` | `ToStore(UnixEpoch.AddTicks(15), dto)` == `1L`. `ToStore(UnixEpoch.AddTicks(-5), dto)` == `-1L`. `FromStore(-1L)` == `UnixEpoch.AddTicks(-10)`. 2026-01-01T10:30:15.123456+05:30 round-trips to the same instant, offset zero |
| `Bools_are_stored_as_ints` | `ToStore(true)` == `1`; `FromStore(0L, bool)` == `false` |
| `Metadata_comes_back_with_its_declared_type` | InMemory pipeline with `bool`, `DateTimeOffset`, `int` metadata: the raw chunk record holds `int`/`long`, and `RetrieveAsync` hits and the manifest metadata (via `IRagAdmin` listing or `ManifestRecord.FromDynamic`) hold `bool` and `DateTimeOffset` |
| `Date_filters_match_values_with_finer_ticks` | ingest a date `D` with ticks finer than a microsecond; `Eq("d", D)` and `In("d", D)` retrieve it |
| `Bool_filters_compare_stored_ints` | translated `Eq("b", true)` compares `Convert(get_Item, int?)` with `Constant(1, int?)` |

  Expected now: compile errors (no `StoredValues`), then FAIL.
- [ ] **Step 2: Implement.** `UtcDateTests` and `StoreShapeTests` stay green (update the expected stored types to `int?` and `long?`). The keyword index keeps declared types: it gets metadata from ingest or from `ManifestRecord.FromDynamic`, never raw records.
- [ ] **Step 3: Core tests, the SQLite connector tests, and the store tests with integration on (InMemory, NativeHybridFake, SQLite, PgVector, Qdrant): all green.** `Metadata_round_trips_with_its_type` in the suite now holds to the microsecond on every store.
- [ ] **Step 4: Commit**: `fix(core): store bool and date metadata as numbers, which every store can index`

### Task 4: The shared suite on Redis

**Files:**
- Create: `tests/RagDotNet.StoreTests/Stores/Redis.cs`
- Modify: `Infrastructure/SharedContainer.cs` (`Containers.Redis`), `SharedContainerTests.cs` (theory gains "Redis"), `ContainerLifetime`, `RagDotNet.StoreTests.csproj`, `Directory.Packages.props`

**Interfaces:**
- Produces:
  - `Containers.Redis`: `SharedContainer<RedisContainer>` ("Redis", `redis/redis-stack-server:7.4.0-v8`, endpoint `GetConnectionString()`).
  - `RedisFixture : StoreFixture`:
    - `Name` is "Redis"; `NativeHybrid` stays false.
    - It holds one `ConnectionMultiplexer`, opened in `InitializeAsync` and disposed in `DisposeAsync`.
    - `CreateStore()` returns `new RedisVectorStore(multiplexer.GetDatabase())` (JSON storage, the default).
  - Bindings `RedisStringKeyTests` and `RedisGuidKeyTests`.

- [ ] **Step 1: The container, test first.** `SharedContainerTests` gains "Redis":
  - Integration off starts nothing.
  - An unstartable image names its cause.
  - Bindings share one container, and the server's version starts with "7.4". Check it with `ConnectionMultiplexer.GetServer(...).Version`.
- [ ] **Step 2: Bind Redis; run its tests with `RAGDOTNET_INTEGRATION=1`.**
  - Expected: 78 pass after Tasks 1–3.
  - Triage anything new by spec §2.1: test first, with a ledger ruling per failure.
  - If a core-feature gap remains, stop and tell the user before any PR.
- [ ] **Step 3: Whole store-tests project, net8.0, integration on.**
  - The count is 3 gate + 9 container + 39 × 10 + 1 = 403, with 3 skipped (the integration-off checks), so 400 run.
  - Without integration, nothing starts.
- [ ] **Step 4: Commit**: `test(stores): run the shared suite against Redis (JSON storage, String and Guid keys)`

### Task 5: CI and docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `docs/vector-stores.md`

- [ ] **Step 1: CI.** The store-tests floor becomes 400, and the comment is updated to list the bindings.
- [ ] **Step 2: Docs:**
  - **The Redis row:** both key types; hybrid "In-memory keyword index"; "Tested".
  - **A "Redis" section:**
    - setup with `new RedisVectorStore(ConnectionMultiplexer.Connect("localhost").GetDatabase())`;
    - JSON storage, the connector's default; hash storage is not supported;
    - Redis Stack, or Redis 8 with its query engine.
  - **Redis known differences:**
    - hybrid search runs through the in-memory keyword index;
    - numbers compare as doubles, so `long` metadata beyond 2^53 can compare inexactly;
    - an `Eq` filter on an empty string fails with Redis's "INDEXEMPTY" error.
  - **The dates paragraph:** dates are kept to the microsecond on every store. Move PostgreSQL's precision note there.
  - **"Collections from earlier builds":** also covers the bool and date storage types and the common-area value. For every store, delete and re-ingest.
- [ ] **Step 3: Each test project, one at a time, in Docker, integration on: 0 failed.**
- [ ] **Step 4: Commit, push, open the PR** "Stage 8 slice 5: Redis". Done when all four CI jobs pass.
