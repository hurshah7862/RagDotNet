# Stage 8 slice 2: RagDotNet.VectorData.Sqlite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** RagDotNet's own Microsoft.Extensions.VectorData connector for SQLite with sqlite-vec, covering exactly what RagDotNet uses (dynamic collections, its filters, paging, vector search, FTS5 hybrid search), passing its own tests and the shared store suite on Linux, Windows and macOS in CI.

**Architecture:** One package `src/RagDotNet.VectorData.Sqlite`. Public surface is only `SqliteVectorStore` and `SqliteVectorStoreOptions`; the collection class is internal. Each collection is one ordinary table (key, one column per data property, the vector as a float32 BLOB) plus an FTS5 table for the full-text property; search is an exact scan using sqlite-vec's `vec_distance_cosine`, so filters never cut results short. A registry table records each collection's definition. The sqlite-vec natives are downloaded at build time from the pinned GitHub release, checked against pinned SHA-256 hashes, and packed under `runtimes/{rid}/native/`.

**Tech Stack:** .NET 10 SDK (net8.0 + net10.0), Microsoft.Data.Sqlite 10.0.14 (SQLitePCLRaw e_sqlite3: FTS5 and JSON1 built in, extension loading allowed), sqlite-vec v0.1.9, Microsoft.Extensions.VectorData.Abstractions 10.8.2, xUnit v3, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` §6, §7, §8 (slice 2), §2.1, §2.2.

## Global Constraints

- Branch `stage8-slice2-sqlite` from `main` (a825d28); the first commit adds this plan.
- Build and test in Docker (`./docker/sdk/run.ps1`, or a bash twin of it); local `dotnet` is blocked by App Control.
- sqlite-vec **v0.1.9**, loadable builds, pinned SHA-256 (from the GitHub release digests, verified by download on 2026-11-22):
  - `sqlite-vec-0.1.9-loadable-linux-x86_64.tar.gz` → `b959baa1d8dc88861b1edb337b8587178cdcb12d60b4998f9d10b6a82052d5d7` (contains `vec0.so`) → `runtimes/linux-x64/native/`
  - `sqlite-vec-0.1.9-loadable-macos-aarch64.tar.gz` → `8282126333399ddfe98bbbcc7a1936e7252625aac49df056a98be602e46bfd29` (`vec0.dylib`) → `runtimes/osx-arm64/native/`
  - `sqlite-vec-0.1.9-loadable-windows-x86_64.tar.gz` → `51581189d52066b4dfc6631f6d7a3eab7dedc2260656ab09ca97ab3fb8165983` (`vec0.dll`) → `runtimes/win-x64/native/`
  - URL pattern: `https://github.com/asg017/sqlite-vec/releases/download/v0.1.9/<file>`. A hash mismatch fails the build.
- sqlite-vec is MIT/Apache-2.0 (copyright Alex Garcia); the package ships its MIT license text in `THIRD-PARTY-NOTICES.md`.
- The package ships with the normal version (not preview). Target `net8.0;net10.0`. PublicAPI analyzers apply (Directory.Build.props does this for `src/`).
- Typed (POCO) collections are out of scope: `GetCollection<TKey,TRecord>` throws `NotSupportedException` pointing to `GetDynamicCollection`.
- Anything RagDotNet does not use throws `NotSupportedException` with a message naming the unsupported thing and "RagDotNet.VectorData.Sqlite".
- No store-specific special cases in the shared suite (spec §4). A real difference is documented in `docs/vector-stores.md`.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **Keyword input containing FTS5 syntax** (`"`, `-`, `*`, `:`, `(`, `NEAR`, `AND`, `OR`) → searched as plain words, never an FTS syntax error. Test `Hybrid_keywords_with_fts_syntax_are_plain_words` (Task 5).
2. **Parallel writers on one file** (Core ingests 4 documents at once) → no "database is locked" failure. Test `Parallel_upserts_do_not_fail` (Task 2).
3. **A record that omits a defined property, or sets it to null** → stored as NULL and read back as null; reads return every defined property. Test `Missing_and_null_values_read_back_as_null` (Task 2).
4. **The app restarts on an existing database** (new `SqliteVectorStore` on the same file) → records, vectors and keyword search still work, `EnsureCollectionExistsAsync` is a no-op, and a property added to the definition gets its column. Tests `Reopened_store_sees_existing_data` (Task 2) and `Hybrid_works_after_reopen` (Task 5).
5. **No usable sqlite-vec native** (unsupported OS/architecture, or a wrong `VecExtensionPath`) → `InvalidOperationException` naming `SqliteVectorStoreOptions.VecExtensionPath` and the platform, not a raw `SqliteException` or `DllNotFoundException`. Test `Missing_extension_names_the_option` (Task 1).

---

### Task 1: Package skeleton, natives, extension loading

**Files:**
- Create: `src/RagDotNet.VectorData.Sqlite/RagDotNet.VectorData.Sqlite.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `THIRD-PARTY-NOTICES.md`, `README.md` (package readme)
- Create: `src/RagDotNet.VectorData.Sqlite/SqliteVectorStore.cs`, `SqliteVectorStoreOptions.cs`, `VecExtension.cs`
- Create: `tests/RagDotNet.VectorData.Sqlite.Tests/RagDotNet.VectorData.Sqlite.Tests.csproj`, `ExtensionTests.cs`, `TempDatabase.cs`
- Modify: `Directory.Packages.props` (`Microsoft.Data.Sqlite` 10.0.14), `RagDotNet.slnx`, `.gitignore` (`src/RagDotNet.VectorData.Sqlite/natives/`)

**Interfaces:**
- Produces:

```csharp
namespace RagDotNet.VectorData.Sqlite;

public sealed class SqliteVectorStoreOptions
{
    /// Full path to a sqlite-vec loadable extension, for platforms the package has no native for.
    public string? VecExtensionPath { get; set; }
}

public sealed class SqliteVectorStore : VectorStore
{
    public SqliteVectorStore(string connectionString, SqliteVectorStoreOptions? options = null);
    // Opens a pooled connection with sqlite-vec loaded. File databases get PRAGMA journal_mode=WAL once, at construction.
    internal Task<SqliteConnection> OpenAsync(CancellationToken ct);
    // Dispose(bool) clears this connection string's pool (Windows file locks).
}

internal static class VecExtension
{
    // VecExtensionPath if set; else <AppContext.BaseDirectory>/runtimes/<rid>/native/vec0.<ext>, then <AppContext.BaseDirectory>/vec0.<ext>,
    // rid from RuntimeInformation: win-x64, linux-x64, osx-arm64. Nothing found → InvalidOperationException:
    // "sqlite-vec was not found for <os>/<arch>. RagDotNet.VectorData.Sqlite ships it for win-x64, linux-x64 and osx-arm64;
    //  on other platforms set SqliteVectorStoreOptions.VecExtensionPath to a sqlite-vec loadable extension."
    public static string Resolve(SqliteVectorStoreOptions options);
}
```

- Tests (`TempDatabase : IDisposable`) — a temp directory with `ConnectionString` for `<dir>/<guid>.db`; `Dispose` calls `SqliteConnection.ClearAllPools()` then deletes the directory. Used by every connector test.

- [ ] **Step 1: The csproj and the natives target.** Package refs: `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.Data.Sqlite`. An MSBuild target running before `BeforeBuild` (and reachable from `GetCopyToOutputDirectoryItems`, so a referencing project's build gets the files) that, for each of the three archives in Global Constraints, skips if `natives/0.1.9/<rid>/vec0.<ext>` exists, else `DownloadFile` → `GetFileHash` (SHA256) → `Error` on mismatch naming file, expected and actual hash → `Exec tar -xzf` into `natives/0.1.9/<rid>/`. The three files are `Content` items, `Link="runtimes/<rid>/native/vec0.<ext>"`, `CopyToOutputDirectory="PreserveNewest"`, `Pack="true"`, `PackagePath="runtimes/<rid>/native/"`. Also pack `THIRD-PARTY-NOTICES.md` and `README.md` (`PackageReadmeFile`).

- [ ] **Step 2: Write the failing tests** in `ExtensionTests.cs`

| Test | Assertions |
|---|---|
| `Shipped_native_loads_and_reports_its_version` | the file `<AppContext.BaseDirectory>/runtimes/<rid>/native/vec0.<ext>` exists for the running platform; a plain `SqliteConnection` with `EnableExtensions(true)` and `LoadExtension(that path)` returns `"v0.1.9"` from `SELECT vec_version()` |
| `Store_constructs_with_the_shipped_native` | `new SqliteVectorStore(db.ConnectionString)` does not throw, and its `GetService(typeof(VectorStoreMetadata))` reports `VectorStoreSystemName == "sqlite"` |
| `Missing_extension_names_the_option` | `new SqliteVectorStore(db.ConnectionString, new() { VecExtensionPath = "/nope/vec0.so" })` → `InvalidOperationException` whose message contains `SqliteVectorStoreOptions.VecExtensionPath` and `/nope/vec0.so` |

The constructor checks that the resolved file exists, so a missing native fails at construction with that message.

- [ ] **Step 3: Run; expect a build failure, then (after the skeleton compiles) the two tests failing.** Command: `dotnet test --project tests/RagDotNet.VectorData.Sqlite.Tests` in Docker.

- [ ] **Step 4: Implement** `SqliteVectorStoreOptions`, `VecExtension.Resolve`, the `SqliteVectorStore` constructor (resolve + existence check + WAL), `OpenAsync`, `Dispose(bool)`, `GetService` (returns `VectorStoreMetadata { VectorStoreSystemName = "sqlite", VectorStoreName = <Data Source> }` for that type, else null). Other abstract members throw `NotImplementedException` until Tasks 2–5 (none may remain at the end of Task 5).

- [ ] **Step 5: Run; both pass on net8.0 and net10.0.** Also confirm the test output contains `runtimes/linux-x64/native/vec0.so`.

- [ ] **Step 6: Commit** — `feat(sqlite): RagDotNet.VectorData.Sqlite skeleton with pinned, verified sqlite-vec natives`

---

### Task 2: Collections and records

**Files:**
- Create: `src/RagDotNet.VectorData.Sqlite/SqliteCollection.cs` (internal), `Schema.cs` (internal: column types, value conversion, DDL)
- Modify: `SqliteVectorStore.cs` (collection members)
- Test: `tests/RagDotNet.VectorData.Sqlite.Tests/CollectionTests.cs`

**Interfaces:**
- Produces: `internal sealed class SqliteCollection : VectorStoreCollection<object, Dictionary<string, object?>>, IKeywordHybridSearchable<Dictionary<string, object?>>` returned by `SqliteVectorStore.GetDynamicCollection(name, definition)`.

**Storage decisions (fixed here):**

| Definition | Column |
|---|---|
| Key `string` | `TEXT PRIMARY KEY` |
| Key `Guid` | `TEXT PRIMARY KEY`, `Guid.ToString("D")` lowercase; read back as `Guid` |
| `string` | `TEXT` |
| `int`, `long`, `int?`, `long?` | `INTEGER`; read back as the declared type |
| `bool`, `bool?` | `INTEGER` 0/1 |
| `DateTimeOffset`, `DateTimeOffset?` | `INTEGER` UTC ticks; read back with offset zero (same instant; documented) |
| `string[]` | `TEXT` JSON array (System.Text.Json) |
| vector `ReadOnlyMemory<float>` | `BLOB`, float32 little-endian (sqlite-vec's format) |
| other types, more than one vector property, or a vector distance other than CosineSimilarity / CosineDistance | `NotSupportedException` from `GetDynamicCollection` |

- Column name = `StorageName ?? Name`, always double-quoted. Collection names must match `^[A-Za-z_][A-Za-z0-9_]{0,127}$` and must not start with `ragdotnet_`; otherwise `ArgumentException`.
- `IsIndexed` data properties get `CREATE INDEX IF NOT EXISTS`.
- Registry table `ragdotnet_collections(name TEXT PRIMARY KEY, definition TEXT NOT NULL)`; `CollectionExistsAsync`, `ListCollectionNamesAsync` (store and collection) read it, so FTS shadow tables never show.
- `EnsureCollectionExistsAsync` in one transaction: create table, indexes, FTS table (Task 5), registry row. On an existing collection: add any missing data columns (`ALTER TABLE ADD COLUMN`), and update the registry row; nothing is ever dropped.
- `UpsertAsync(records)` in one transaction: `INSERT ... ON CONFLICT("<key>") DO UPDATE SET ... RETURNING rowid` (rowid stays stable for FTS). A missing property is NULL. A vector of the wrong length → `ArgumentException` naming the property, expected and actual dimensions.
- `GetAsync(keys)` in batches of 500 (`IN (...)`); `GetAsync(key)`; `DeleteAsync(key|keys)` in one transaction. Records come back with every defined property (null for NULL); vectors only when `IncludeVectors`.
- `SqliteException` is wrapped in `VectorStoreException` with `VectorStoreSystemName = "sqlite"`, `CollectionName` and `OperationName` set.

- [ ] **Step 1: Write the failing tests** in `CollectionTests.cs` (each against a `TempDatabase`, a definition built in the test with one property of each supported type, dimension 4):

| Test | Assertions |
|---|---|
| `Ensure_exists_creates_and_is_idempotent` | false → ensure twice → true; `ListCollectionNamesAsync` is exactly `["c1"]` |
| `Delete_collection_removes_it` | ensure, upsert, `EnsureCollectionDeletedAsync` → exists false, names empty, deleting again does not throw |
| `Every_type_round_trips` (theory over `string` and `Guid` keys) | upsert one record with every type set → get by key returns equal values with the declared CLR types; `DateTimeOffset` equal as an instant |
| `Missing_and_null_values_read_back_as_null` | one record omits each nullable property, another sets them null → both read back with every key present and null |
| `Vectors_only_when_asked` | `IncludeVectors = false` → no vector key or null; `true` → equal floats |
| `Upsert_replaces_and_delete_removes` | upsert, upsert changed → one record, new values; `DeleteAsync(keys)` → `GetAsync(keys)` empty |
| `Get_many_keys_spans_batches` | 1,201 records, `GetAsync` of all keys → 1,201 distinct records |
| `Wrong_dimension_is_rejected` | vector of length 3 → `ArgumentException` mentioning `4` and `3` |
| `Invalid_names_and_types_are_rejected` | name `"a-b"` and `"ragdotnet_x"` → `ArgumentException`; a `decimal` property, or `EuclideanDistance` → `NotSupportedException` mentioning the type or distance; `GetCollection<string, object>` → `NotSupportedException` mentioning `GetDynamicCollection` |
| `Parallel_upserts_do_not_fail` | 8 tasks upserting 50 records each, at once, through 8 collection instances on one file → 400 records |
| `Reopened_store_sees_existing_data` | upsert, dispose store, new store on the same file → records there; ensure is a no-op; a definition with one extra `string` property → ensure adds it, old records read it as null |

- [ ] **Step 2: Run; they fail** (NotImplementedException / missing behavior).

- [ ] **Step 3: Implement** `Schema` and `SqliteCollection` (all members except filtered `GetAsync`, `SearchAsync`, `HybridSearchAsync`, which throw `NotImplementedException` until Tasks 3–5).

- [ ] **Step 4: Run; all pass on both frameworks.**

- [ ] **Step 5: Commit** — `feat(sqlite): collections, schema and record storage`

---

### Task 3: Filters, filtered reads and paging

**Files:**
- Create: `src/RagDotNet.VectorData.Sqlite/FilterTranslator.cs` (internal)
- Modify: `SqliteCollection.cs` (`GetAsync(filter, top, options)`)
- Test: `tests/RagDotNet.VectorData.Sqlite.Tests/FilterTests.cs`

**Interfaces:**
- Produces: `internal static (string Sql, IReadOnlyList<SqliteParameter> Parameters) FilterTranslator.Translate(LambdaExpression filter, Schema schema, string prefix)` — a SQL boolean for a `WHERE`, parameters named `@{prefix}{n}`. Tasks 4 and 5 reuse it.

**Supported shapes (exactly what Core emits; anything else → `NotSupportedException` naming the node, e.g. "Filter node 'Modulo' is not supported by RagDotNet.VectorData.Sqlite"):**
- Field: `r["name"]` as an `IndexExpression` (Core's translator) **or** a `get_Item` `MethodCallExpression` (C# lambdas), optionally inside `Convert` to `T` or `T?`; unknown property name → `ArgumentException`.
- Value: any subtree without the lambda parameter, evaluated once (`Expression.Lambda(node).Compile().DynamicInvoke()`), so captured locals work; converted to its column form (bool → 0/1, `DateTimeOffset` → UTC ticks, `Guid` → text).
- `Equal` / `NotEqual` (either operand order). With a null value: `IS NULL` / `IS NOT NULL`. `NotEqual` with a value keeps C# semantics: `(col IS NULL OR col <> @p)`.
- `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual` on integer and `DateTimeOffset` columns.
- `AndAlso`, `OrElse`, `Not`; a constant `true`/`false` body.
- `Enumerable.Contains(field string[], value)` → `EXISTS (SELECT 1 FROM json_each(col) WHERE value = @p)`; a NULL array matches nothing.

`GetAsync(filter, top, options)`: `WHERE <filter> ORDER BY <options.OrderBy: each SortInfo's PropertySelector resolved to a column, ASC/DESC; none → rowid> LIMIT @top OFFSET @skip`; `top < 1` → `ArgumentOutOfRangeException`.

- [ ] **Step 1: Write the failing tests** — a corpus of 3 records (`full`, `other`, `bare`) with the same field types as the store suite's filter table (string, int, long, bool, DateTimeOffset, string[]), all values on `full`/`other`, none on `bare`.

| Test | Assertions |
|---|---|
| `Translated_filter_matches_exactly` (theory over the same 19 case names and expected sets as `tests/RagDotNet.StoreTests/Suite/StoreSuite.Filters.cs`, filters written as C# lambdas over `Dictionary<string, object?>`) | `GetAsync(filter, 10)` keys equal the expected set |
| `Core_style_index_expressions_are_understood` | a filter built with `Expression.MakeIndex` + `Expression.Convert(…, typeof(int?))` + `Expression.Equal` matches `full` |
| `Not_equal_keeps_csharp_null_semantics` | `r => (string)r["s"]! != "x"` → `other` and `bare` |
| `Captured_values_are_evaluated` | `var v = "y"; r => (string)r["s"]! == v` → `other` |
| `Comparisons_on_numbers_and_dates` | `(int)r["i"]! >= 2` → `other`; `(DateTimeOffset)r["d"]! < <other's date>` → `full` |
| `Unsupported_node_is_named` | `r => ((string)r["s"]!).StartsWith("x")` → `NotSupportedException` containing `StartsWith` |
| `Paging_with_order_by_key_is_stable` | 250 records, pages of 100 with `OrderBy Ascending(r => r["Key"])` and `Skip` 0/100/200 → 250 distinct keys in ascending order |
| `Descending_order_and_skip` | order by an int property descending, skip 1, top 2 → the 2nd and 3rd largest |

- [ ] **Step 2: Run; fail.** **Step 3: Implement.** **Step 4: Run; all pass.**

- [ ] **Step 5: Commit** — `feat(sqlite): translate RagDotNet's filters to SQL; filtered, ordered, paged reads`

---

### Task 4: Vector search

**Files:**
- Modify: `SqliteCollection.cs` (`SearchAsync`)
- Test: `tests/RagDotNet.VectorData.Sqlite.Tests/SearchTests.cs`

`SearchAsync<TInput>(value, top, options)`: `TInput` must be `ReadOnlyMemory<float>` or `float[]` (else `NotSupportedException`); length checked like upsert. Query:
`SELECT *, vec_distance_cosine("<vector>", @q) AS d FROM "<table>" WHERE "<vector>" IS NOT NULL [AND <filter>] ORDER BY d LIMIT @top OFFSET @skip` — an exact scan (documented: linear in collection size).
Score: `CosineSimilarity` → `1 - d`; `CosineDistance` → `d`. `ScoreThreshold`: similarity ≥ t / distance ≤ t, applied in SQL. `IncludeVectors` as in reads. `options.VectorProperty` other than the single vector → `NotSupportedException`.

- [ ] **Step 1: Write the failing tests**

| Test | Assertions |
|---|---|
| `Nearest_first_with_cosine_similarity` | vectors at 0°, 45°, 90° from the query → that order; scores ≈ 1, 0.7071, 0 (±1e-5) |
| `Cosine_distance_scores_are_distances` | same, definition with `CosineDistance` → scores ≈ 0, 0.2929, 1, same order |
| `Search_applies_filter_skip_and_threshold` | filter excludes the nearest → it is absent; `Skip = 1` drops the first; `ScoreThreshold = 0.5` keeps two |
| `Filter_never_cuts_results_short` | 300 records, filter matching 7 far-from-query ones, `top = 10` → exactly 7 |
| `Wrong_query_dimension_is_rejected` | length 3 → `ArgumentException` |

- [ ] **Step 2–4:** fail → implement → pass.

- [ ] **Step 5: Commit** — `feat(sqlite): exact vector search with filters, skip and score threshold`

---

### Task 5: FTS5 keyword hybrid search

**Files:**
- Modify: `SqliteCollection.cs` (FTS table, sync on upsert/delete, `HybridSearchAsync`), `Schema.cs`
- Test: `tests/RagDotNet.VectorData.Sqlite.Tests/HybridTests.cs`

- A collection with one `IsFullTextIndexed` string property gets `CREATE VIRTUAL TABLE "<table>_fts" USING fts5(text, tokenize = 'unicode61 remove_diacritics 2')`, rowid = the main table's rowid; more than one such property → `NotSupportedException`.
- Upsert: in the same transaction, `DELETE FROM fts WHERE rowid = @r` then insert the text when not null. Delete removes the FTS row too. Collection delete drops the FTS table.
- `HybridSearchAsync(value, keywords, top, options)`: no full-text property → `NotSupportedException` ("… has no full-text indexed property …"); `options.AdditionalProperty`, when set, must name that property. Empty keywords → plain vector search. Otherwise:
  - FTS query: each keyword → `"` + keyword with `"` doubled + `"`, joined with ` OR ` (every keyword is a quoted phrase, so FTS5 syntax in input is plain text).
  - Keyword ranking: `SELECT m.*, bm25(f) AS b FROM "<table>_fts" f JOIN "<table>" m ON m.rowid = f.rowid WHERE f MATCH @q [AND <filter>] ORDER BY b LIMIT @n`.
  - Vector ranking: as Task 4 with the same filter and `@n`.
  - `n = top + skip`. Fuse with reciprocal rank fusion, k = 60 (score = Σ 1/(60 + rank), rank from 1), order by fused score then key, apply `Skip`, take `top`; `Score` = the fused score. `ScoreThreshold` → `NotSupportedException` (fused scores are not similarities).

- [ ] **Step 1: Write the failing tests**

| Test | Assertions |
|---|---|
| `Exact_term_wins_over_an_identical_vector` | two records with the same vector, text `"zx81 filler"` and `"other filler"`; keywords `["zx81"]` → the zx81 record first |
| `Hybrid_applies_the_filter_to_both_rankings` | the exact-term record excluded by filter → not returned |
| `Hybrid_keywords_with_fts_syntax_are_plain_words` | keywords `["\"quoted\"", "a-b", "x*", "col:val", "(", "NEAR", "AND", "OR"]` → no exception; a record containing `a-b` text is found |
| `Updated_and_deleted_text_leaves_keyword_search` | upsert text "alpha", update to "beta" → keyword alpha finds nothing by keyword (the record ranks only by vector); delete → keyword beta finds nothing |
| `Hybrid_works_after_reopen` | upsert, dispose, new store on the file → keyword search finds it |
| `No_full_text_property_is_not_supported` | definition without `IsFullTextIndexed` → `HybridSearchAsync` throws `NotSupportedException` |

- [ ] **Step 2–4:** fail → implement → pass. No `NotImplementedException` may remain in the package (grep it).

- [ ] **Step 5: Commit** — `feat(sqlite): FTS5 keyword search fused with vector search (IKeywordHybridSearchable)`

---

### Task 6: The shared store suite on SQLite

**Files:**
- Create: `tests/RagDotNet.StoreTests/Stores/Sqlite.cs`
- Modify: `tests/RagDotNet.StoreTests/RagDotNet.StoreTests.csproj` (reference the package project)

**Interfaces:**
- Consumes: `StoreFixture`, `StoreSuite<TFixture>` (slice 1).
- Produces:

```csharp
public sealed class SqliteFixture : StoreFixture
{
    Name => "SQLite"; NativeHybrid => true;
    // A temp directory per fixture; CreateStore() → new SqliteVectorStore($"Data Source={dir}/{Guid.NewGuid():N}.db").
    // DisposeAsync: SqliteConnection.ClearAllPools(), then delete the directory.
}
public sealed class SqliteStringKeyTests(SqliteFixture f) : StoreSuite<SqliteFixture>(f, RagKeyType.String);
public sealed class SqliteGuidKeyTests(SqliteFixture f) : StoreSuite<SqliteFixture>(f, RagKeyType.Guid);
```

SQLite needs no external service, so it always runs (like InMemory).

- [ ] **Step 1: Add the fixture and bindings; run the store suite.** Expected: every SQLite test passes. Each failure is a bug in the connector (fix it in the connector with a failing connector test first) or in Core (failing Core unit test first, spec §8), never a change to the shared suite unless the suite itself assumed something InMemory-specific (then a ledgered ruling, and the change must hold for every binding).

- [ ] **Step 2: Run the whole store-tests project; record the count** (expected 198 per framework: 3 + 39 × 5 bindings).

- [ ] **Step 3: Commit** — `test(stores): run the shared suite against SQLite`

---

### Task 7: CI on Linux, Windows and macOS; docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `docs/vector-stores.md`, `README.md`

- [ ] **Step 1: CI**
  - `build-and-test` (ubuntu): add `Test SQLite connector (net8.0)` — `dotnet test --project tests/RagDotNet.VectorData.Sqlite.Tests --no-build --configuration Release --framework net8.0`.
  - `store-tests`: floor `198`; comment updated to "3 gate tests + 39 suite tests × 5 bindings (InMemory String/Guid, NativeHybridFake String, SQLite String/Guid)".
  - `windows`: add the connector test step, and `Test store suite on SQLite (net8.0)` with `RAGDOTNET_STORES_REQUIRED: 1`, `--filter-class "RagDotNet.StoreTests.Stores.Sqlite*" --minimum-expected-tests 78`.
  - New job `macos`: `runs-on: macos-latest` (Apple Silicon), timeout 20, checkout + setup-dotnet as the others; `dotnet build tests/RagDotNet.VectorData.Sqlite.Tests` and `tests/RagDotNet.StoreTests` (Release); run the connector tests and the store suite filtered to SQLite exactly as on Windows. Comment: "the only job that loads the osx-arm64 sqlite-vec native".
  - Verify the filter + floor combination locally first: `--filter-class "RagDotNet.StoreTests.Stores.Sqlite*" --minimum-expected-tests 78` → exit 0; with 79 → non-zero.

- [ ] **Step 2: Pack check.** `dotnet pack src/RagDotNet.VectorData.Sqlite -c Release -o <scratch>`; list the `.nupkg`: it contains `runtimes/win-x64/native/vec0.dll`, `runtimes/linux-x64/native/vec0.so`, `runtimes/osx-arm64/native/vec0.dylib`, `THIRD-PARTY-NOTICES.md`, `README.md`, and `lib/net8.0` + `lib/net10.0`. Record the listing in the ledger.

- [ ] **Step 3: Docs.** `docs/vector-stores.md`: the SQLite row (package `RagDotNet.VectorData.Sqlite`, keys String and Guid, hybrid "Native (FTS5)", status "Tested on Linux, Windows and macOS") and a "SQLite" section:
  - setup: `new SqliteVectorStore("Data Source=rag.db")`, passed to `UseVectorStore(...)`; use a file (each pooled connection to `:memory:` is its own empty database);
  - natives shipped for win-x64, linux-x64, osx-arm64, `VecExtensionPath` for others;
  - no per-store settings required; `CosineSimilarity` and `CosineDistance` both work;
  - known differences: search is an exact scan (linear in the number of chunks; fine for a local or single-node app, use a server store for millions of chunks); `DateTimeOffset` values come back in UTC (same instant); keyword search is FTS5 with the `unicode61` tokenizer, whose word splitting can differ from the in-memory index's.
  README: the store sentence mentions SQLite; the package list (if any) gains `RagDotNet.VectorData.Sqlite`.

- [ ] **Step 4: Run the whole solution in Docker.** Expected: 0 failed; total = 1,826 + new connector tests × 2 + 78 × 2.

- [ ] **Step 5: Commit, push, open the PR** — `ci: SQLite connector and store suite on Linux, Windows and macOS; SQLite docs`; PR "Stage 8 slice 2: RagDotNet.VectorData.Sqlite". Done only when all four CI jobs pass and the macOS and Windows SQLite steps report 78 suite tests.
