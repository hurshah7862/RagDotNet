# Vector stores

RagDotNet works with any [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) connector that supports dynamic collections. The stores below are the ones it tests: each one runs the same shared suite on every pull request.

| Store | Package | Key types | Hybrid search | Status |
|---|---|---|---|---|
| In-memory | `CommunityToolkit.VectorData.InMemory` | String, Guid | In-memory keyword index | Tested. For development and tests, not production |
| SQLite (sqlite-vec) | `RagDotNet.VectorData.Sqlite` | String, Guid | Native (FTS5) | Tested on Linux, Windows and macOS |
| PostgreSQL (pgvector) | `CommunityToolkit.VectorData.PgVector` | String, Guid | Native (PostgreSQL full-text search) | Tested |
| Qdrant | `CommunityToolkit.VectorData.Qdrant` | | | Coming in stage 8 |
| Redis (Redis Stack) | `CommunityToolkit.VectorData.Redis` | | | Coming in stage 8 |
| SQL Server 2025 | `CommunityToolkit.VectorData.SqlServer` | | | Coming in stage 8 |

Other connectors (Weaviate, Azure AI Search, Cosmos DB and more) may work but are not tested.

## Per-store settings

Some stores need settings different from the defaults. Qdrant, for example, needs `RagOptions.KeyType = RagKeyType.Guid`. Each store's settings are listed in its section on this page as the store is added. Set them when you pick the store. If a setting is wrong, RagDotNet fails at startup, or at the latest on the first call to the store, with an error that names the setting to change. It does not detect the store or change its defaults for you.

`RagOptions.DistanceFunction` applies to both of RagDotNet's collections: the chunks and the document list.

Metadata dates (`DateTimeOffset`) are stored in UTC on every store: a date comes back as the same instant with a zero offset, and filters compare instants. Some stores (PostgreSQL) accept no other offset.

## SQLite

`RagDotNet.VectorData.Sqlite` is RagDotNet's own connector for SQLite with [sqlite-vec](https://github.com/asg017/sqlite-vec). Everything lives in one database file, which suits desktop apps, local tools and single-node services.

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new SqliteVectorStore("Data Source=rag.db"), "docs", dimensions: 768)
    .Build();
```

- Use a database file. Each pooled connection to `:memory:` opens its own empty database, so an in-memory connection string does not keep data between calls.
- sqlite-vec is included for win-x64, linux-x64 and osx-arm64. On any other platform, set `SqliteVectorStoreOptions.VecExtensionPath` to a sqlite-vec loadable extension; without it the store fails at construction with an error naming that option.
- No settings beyond the defaults are needed. Both key types work, and so do `CosineSimilarity` and `CosineDistance`.
- Several threads, and processes on the same machine, can use the file at once: the store turns on write-ahead logging (WAL) and writes in short transactions. Keep the file on a local disk; WAL does not work over network file systems.

Known differences:

- Vector search is an exact scan of every chunk, so a filter never cuts the results short. Its cost grows with the number of chunks: it is fine for a local or single-node app, and a server store is the better choice for millions of chunks.
- `DateTimeOffset` metadata comes back in UTC: the same instant, with a zero offset.
- Hybrid search is native, on SQLite full-text search (FTS5) with the `unicode61` tokenizer. Its word splitting can differ from the in-memory keyword index's, so the order of keyword matches can differ slightly between the two.

## PostgreSQL (pgvector)

Use `CommunityToolkit.VectorData.PgVector` with PostgreSQL and the [pgvector](https://github.com/pgvector/pgvector) extension.

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new PostgresVectorStore("Host=localhost;Database=rag;Username=rag;Password=..."), "docs", dimensions: 768)
    .Build();
```

- The database needs the extension: run `CREATE EXTENSION IF NOT EXISTS vector;` once, as a user allowed to create extensions. The `pgvector/pgvector` Docker images include it.
- No settings beyond the defaults are needed. Both key types work.
- RagDotNet creates its two tables (the collection name and `<name>_documents`) on first use.

Known differences:

- Hybrid search is native, on PostgreSQL full-text search. Word splitting and stemming follow PostgreSQL's text search rules, so the order of keyword matches can differ slightly from the in-memory keyword index.
- There is no separate keyword index, so `IRagAdmin.RebuildKeywordIndexAsync` throws `InvalidOperationException` saying keyword search is native.
- Dates are kept to the microsecond (PostgreSQL's `timestamptz` precision). A value with finer ticks, such as `DateTimeOffset.UtcNow`, can come back changed in its last digit, so compare such values with a tolerance of one microsecond.

## How stores are tested

`tests/RagDotNet.StoreTests` holds one shared suite, written against RagDotNet's public API with a deterministic fake embedder, so it needs no model. Every store runs it once for each key type it supports. A test-only binding, the in-memory store with a fake native hybrid search, keeps the native hybrid path covered; it is not a supported store. Settings a store needs are applied in one place, its test fixture, and must match the settings documented here. The suite covers:

- Ingest, unchanged re-ingest, updates with fewer and more chunks (no chunks left behind), delete and stats.
- Document listing across more than one page.
- Every filter on every metadata type, including documents without the field, and metadata coming back with its type.
- Access scopes: partition isolation, the common area, full access and per-partition stats.
- Hybrid search: an exact term is found, through the store's native hybrid search where it has one and the keyword index otherwise.
- Reindex to a new model, including reads while it runs.
- External content mode.

Run it with:

```
dotnet test --project tests/RagDotNet.StoreTests
```

The in-memory and SQLite stores need nothing and always run. Stores that need Docker (PostgreSQL so far) run only when `RAGDOTNET_INTEGRATION=1` is set, and are skipped with the reason when their dependency is missing. Each starts one container per test run through Testcontainers, from a pinned image (`pgvector/pgvector:0.8.7-pg17`). With the repository's Docker build image, `./docker/sdk/run.ps1` mounts the Docker socket so these containers can start. In CI, `RAGDOTNET_STORES_REQUIRED=1` turns a store that cannot start into a failure, and a minimum test count fails the run if tests go missing.
