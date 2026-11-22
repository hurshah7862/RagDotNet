# RagDotNet stage 8: store integration (design)

Stage 8 of the build order in the [main design](2026-10-18-ragdotnet-design.md#10-build-order-v1--all-stages-complete): `RagDotNet.VectorData.Sqlite`, and one shared integration suite run against all six supported vector stores (main design §2 "Supported vector stores", §3 package table, §8, §9.2).

## 1. Why

Everything so far has run against the in-memory store only. RagDotNet claims to support six stores, and an app should be able to move from the in-memory store to a real database without its answers, filters, access rules or lifecycle changing. Stage 8 turns that claim into something tested on every pull request.

## 2. Goals

- **One shared store suite**, written once and run against each store, covering what RagDotNet relies on (§5).
- **All six stores pass it**: InMemory, SQLite (sqlite-vec), PostgreSQL (pgvector), Qdrant, Redis, SQL Server 2025.
- **`RagDotNet.VectorData.Sqlite`**: RagDotNet's own Microsoft.Extensions.VectorData connector for SQLite with sqlite-vec, scoped as the main design says (§6).
- **CI runs the suite on every pull request** for every store *(owner's decision)*.
- Where a store can't do something RagDotNet asks for, the failure is a clear error, never a silently wrong result. Where a store behaves differently in a way RagDotNet can absorb (scores, ordering of ties, key format), Core absorbs it.

### 2.1 When a connector can't do something RagDotNet relies on *(owner's decision)*

Likely examples: listing manifests by paging in key order (Qdrant), `string[]` fields in Redis's hash storage mode, filter shapes a connector doesn't translate. In this order:

1. **Work around it**: change how Core asks (for example a different paging or filter shape that every store accepts), or use the connector's other mode (for example Redis JSON instead of hashes). A Core change gets a failing test first and must keep every store already passing green.
2. **If a core feature still can't work**, the store is **not marked supported** in this stage: it is postponed, the gap is reported upstream to the connector, and the docs say why. Core features are: lifecycle (ingest, update, delete, high-water cleanup), filters, access scope, reindex, and manifest listing. No store ships with a hole in multi-tenancy.
3. **A minor gap** (outside the core features) may ship as a documented `NotSupportedException` with a clear message, listed in the store's "known differences".
4. RagDotNet **does not fork or maintain its own copies** of the CommunityToolkit connectors. `RagDotNet.VectorData.Sqlite` stays the only connector RagDotNet owns.

### 2.2 Per-store settings *(owner's decision)*

Some stores need settings different from the defaults (Qdrant needs `RagKeyType.Guid`; a store may not accept the default distance function). These are **documented per store**, and the app sets them when it picks the store. A wrong setting fails at startup (the existing options validation, or the first collection call at the latest) with an error that names the setting to change. Core does not detect the store or change its defaults per store, and the public API does not change for this.

## 3. Non-goals

- Typed (POCO/attribute) mapping in the SQLite connector (main design: out of scope for v1).
- Stores beyond the six (Weaviate, Azure AI Search, Cosmos DB and others are best-effort, untested).
- Performance benchmarks or tuning store indexes for scale.
- Publishing packages (stage 10).

## 4. Anti-goals

- **A suite that passes by not running.** In CI, a store test that cannot start its store fails; it never skips. (This project has been bitten before by test runs that silently left out whole projects.)
- **Per-store special cases in the suite** that hide a real difference from users. A store-specific expectation is allowed only when it documents a real, user-visible difference, and the difference then goes in the docs.
- **Tests that depend on a model.** The suite uses the deterministic fake embedder, so it needs no Ollama and runs in CI.

## 5. The shared store suite

One abstract test class (or a small set) parameterized by a store fixture; each store supplies a fixture that creates a fresh `VectorStore` (and a fresh collection name per test or per class, so tests don't see each other's data). The suite drives RagDotNet through its public API (`IRag`, `IRagAdmin`, the builder or DI), not the store directly.

Coverage, from main design §8 and §9.2:

- **Lifecycle:** ingest, unchanged re-ingest (no writes), update with fewer and more chunks (the high-water cleanup leaves no orphan chunks), delete, stats.
- **Manifest enumeration:** filtered `GetAsync` with paging over more manifests than one page.
- **Filters:** `Eq`, `In`, `Contains`, `ContainsAny`, `And`, `Or` on every metadata type (`string`, `int`, `long`, `bool`, `DateTimeOffset`, `string[]`), and **missing values** for each type (a document without the field never matches).
- **Access scope:** partition isolation on ingest, retrieve and delete; the same `DocumentId` in two partitions; the common area (visible to tenants, read-only to them); `IncludeCommon`; full access without a scope.
- **Keys:** `RagKeyType.String` and `RagKeyType.Guid` where the store supports each (Qdrant needs Guid).
- **Hybrid:** native hybrid where the store offers `IKeywordHybridSearchable` (Qdrant, and SQLite through FTS5), otherwise the in-memory keyword index; an exact-term query finds its chunk either way.
- **Reindex:** a model change followed by `ReindexAsync` leaves every chunk on the new model, with reads limited to re-embedded data while it runs.
- **External content mode:** chunks stored without text, text fetched from `IChunkContentStore`.

The in-memory store runs the suite too, as the reference: the suite must pass there before any other store is added.

## 6. `RagDotNet.VectorData.Sqlite`

As fixed by the main design (§3 package table):

- A `VectorStore` for SQLite (Microsoft.Data.Sqlite) with sqlite-vec.
- Covers what RagDotNet needs: dynamic collections (`VectorStoreCollectionDefinition` with `Dictionary<string, object?>` records), the filters RagDotNet emits (equality including through `Nullable<T>`, `Enumerable.Contains` on `string[]`, `AndAlso`, `OrElse`, `NotEqual` on strings), filtered `GetAsync` with paging, vector search with filters, upsert, delete by key, and FTS5 keyword hybrid search (`IKeywordHybridSearchable`). Anything else throws `NotSupportedException` with a clear message.
- **sqlite-vec natives** for win-x64, linux-x64 and osx-arm64 are bundled in the package. There is no official sqlite-vec package on NuGet, so the package carries the loadable-extension builds from sqlite-vec's GitHub releases, at one pinned version, with their license.
- The connector has its own tests for exactly those operations, in addition to running the shared suite.
- Ships with the normal version (not preview).

## 7. Running the suite

- **Locally:** opt-in with `RAGDOTNET_INTEGRATION=1` (main design §9.2); a store whose dependency (Docker) is missing is skipped with a reason.
- **In CI:** a store-tests job on `ubuntu-latest` (which has Docker) runs the suite for every store on every pull request, with a setting that turns "dependency missing" into a failure instead of a skip. SQLite and InMemory need no Docker; the SQLite tests also run in the Windows job, since the package bundles a win-x64 native.
- **macOS** *(builder's call)*: from slice 2, a `macos-latest` (Apple Silicon) job runs the SQLite connector tests and the shared suite against SQLite, so the osx-arm64 native the package ships is loaded by a test on every pull request. Without it, that native would be shipped untested, which the "passes by not running" anti-goal rules out.
- **Main design update** *(builder's call)*: the main design's statement that CI never uses Docker is superseded by the owner's decision to run store tests on every pull request; slice 1 updates that text and points to this document.
- **Docker stores** start through Testcontainers (`Testcontainers.PostgreSql`, `.Qdrant`, `.Redis`, `.MsSql`), one container per store per test run, with pinned image tags.
- **Dev machine limits:** it has 16 GB of memory, so local runs start one store at a time; SQL Server may only be practical in CI.

## 8. Delivery: one store at a time *(owner's decision)*

Each slice is its own branch and pull request, merged before the next starts, and CI covers each store from the slice that adds it:

1. **The shared suite and the CI store job, against InMemory.**
2. **SQLite**: the new package, its own tests, and the shared suite.
3. **PostgreSQL + pgvector** (`CommunityToolkit.VectorData.PgVector`).
4. **Qdrant** (`CommunityToolkit.VectorData.Qdrant`): Guid keys and native hybrid.
5. **Redis** (`CommunityToolkit.VectorData.Redis`, Redis Stack).
6. **SQL Server 2025** (`CommunityToolkit.VectorData.SqlServer`).

A slice that finds a bug in Core fixes it in that slice, with a failing test first, and the fix also gets a unit test where it can be shown without the store.

Each slice updates the docs: a "Vector stores" page listing each store's package, setup, key types, hybrid support and known differences; the README's store table.

## 9. Core change already decided *(builder's call)*

The manifest collection's 1-dimension placeholder vector is hard-coded to cosine similarity. It will use the distance function the app configured in `RagOptions`, so a store that accepts only some distance functions sees one consistent setting across both collections (and §2.2's startup error covers both). Done in slice 1 with a unit test; existing in-memory data is unaffected because the placeholder is never searched.

## 10. Left to the builder

- Test project layout (one store-tests project, or one per store), fixture mechanics and xUnit collection setup.
- Container image tags, and the exact environment variable that makes CI fail instead of skip.
- The SQLite schema (main table, `vec0` virtual table, FTS5 table) and how filters translate to SQL.
- The sqlite-vec version to pin, and how the natives are downloaded and verified at build time.
- Whether a store needs Core changes (for example key formats or score scales) and how small they can be.
