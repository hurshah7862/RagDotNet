# Vector stores

RagDotNet works with any [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) connector that supports dynamic collections. The stores below are the ones it tests: each one runs the same shared suite on every pull request.

| Store | Package | Key types | Hybrid search | Status |
|---|---|---|---|---|
| In-memory | `CommunityToolkit.VectorData.InMemory` | String, Guid | In-memory keyword index | Tested. For development and tests, not production |
| SQLite (sqlite-vec) | `RagDotNet.VectorData.Sqlite` | String, Guid | Native (FTS5) | Tested on Linux, Windows and macOS |
| PostgreSQL (pgvector) | `CommunityToolkit.VectorData.PgVector` | String, Guid | Native (PostgreSQL full-text search) | Tested |
| Qdrant | `CommunityToolkit.VectorData.Qdrant` | Guid only | Native (Qdrant full-text index) | Tested |
| Redis (Redis Stack) | `CommunityToolkit.VectorData.Redis` | String, Guid | In-memory keyword index | Tested |
| SQL Server 2025 | `CommunityToolkit.VectorData.SqlServer` | Guid only | Native (SQL Server full-text search) | Tested |

Other connectors (Weaviate, Azure AI Search, Cosmos DB and more) may work but are not tested.

## Per-store settings

Some stores need settings different from the defaults. Each store's settings are listed in its section on this page:

| Store | Setting |
|---|---|
| Qdrant | `RagOptions.KeyType = RagKeyType.Guid` (required) |
| SQL Server | `RagOptions.KeyType = RagKeyType.Guid` and `RagOptions.DistanceFunction = DistanceFunction.CosineDistance` (both required) |

The other tested stores work with the defaults. Set them when you pick the store. It does not detect the store or change its defaults for you.

If a setting is wrong, RagDotNet fails when the pipeline is built, or at the latest on the first call to the store, before anything is written. The error is an `InvalidOperationException` that starts with "The vector store rejected RagDotNet's '<collection>' collection (key type ..., distance ..., ... dimensions)", carries the store's own error, and names `RagOptions.KeyType` and `RagOptions.DistanceFunction`. On that first call RagDotNet checks that both collections exist, creates the missing ones and runs a one-result search on the document list, which is where a store rejects an unsupported distance function. If the store rejects the settings, RagDotNet deletes the collections it created in that call, so nothing is left behind; collections that already existed are never touched. A store that cannot be reached fails with its own error, unchanged.

`RagOptions.DistanceFunction` applies to both of RagDotNet's collections: the chunks and the document list.

Metadata dates (`DateTimeOffset`) are stored as microseconds since 1970 (UTC) on every store, and `bool` metadata as the numbers 0 and 1, because some stores (Redis) can only index strings and numbers. RagDotNet converts them back, so you always see `DateTimeOffset` and `bool`:

- A date comes back as the same instant with a zero offset, and filters compare instants.
- Dates are kept to the microsecond. A value with finer ticks, such as `DateTimeOffset.UtcNow`, comes back rounded down to the microsecond, so compare such values with a tolerance of one microsecond. A filter with the original value still matches it.

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

## Qdrant

Use `CommunityToolkit.VectorData.Qdrant` with a [Qdrant](https://qdrant.tech/) server. The connector talks to Qdrant over gRPC (port 6334 by default).

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new QdrantVectorStore(new QdrantClient("localhost"), ownsClient: true), "docs", dimensions: 768)
    .Configure(o => o.KeyType = RagKeyType.Guid) // required on Qdrant
    .Build();
```

- **`RagOptions.KeyType = RagKeyType.Guid` is required.** Qdrant point ids are unsigned integers or GUIDs, so the default string keys cannot be stored. Without the setting, building the pipeline fails with an `InvalidOperationException` that starts with "The vector store rejected RagDotNet's 'docs' collection (key type String, ...)" and names `RagOptions.KeyType`.
- RagDotNet creates its two collections (the collection name and `<name>_documents`) and their payload indexes on first use.

Known differences:

- Hybrid search is native: Qdrant runs the vector query and a keyword query on its full-text payload index and fuses the two result lists. Its word splitting follows Qdrant's full-text tokenizer, so the order of keyword matches can differ slightly from the in-memory keyword index.
- There is no separate keyword index, so `IRagAdmin.RebuildKeywordIndexAsync` throws `InvalidOperationException` saying keyword search is native.
- Creating a collection takes a few seconds, because Qdrant builds a payload index for every filterable field.

## Redis

Use `CommunityToolkit.VectorData.Redis` with [Redis Stack](https://redis.io/docs/latest/operate/oss_and_stack/), or Redis 8 with its query engine. The connector needs the JSON and search modules.

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new RedisVectorStore(ConnectionMultiplexer.Connect("localhost").GetDatabase()), "docs", dimensions: 768)
    .Build();
```

- Use JSON storage, the connector's default. Hash storage (`RedisStorageType.HashSet`) is not supported.
- No settings beyond the defaults are needed. Both key types work.
- RagDotNet creates its two indexes (the collection name and `<name>_documents`) on first use.

Known differences:

- Redis has no native hybrid search, so hybrid search runs through RagDotNet's in-memory keyword index.
- Redis compares numbers as doubles, so `long` metadata beyond 2^53 (about 9 × 10^15) can compare inexactly in filters.
- A filter on an empty string (`RagFilter.Eq("tenant", "")`) fails with Redis's own error, which mentions `INDEXEMPTY`: Redis cannot query empty strings in an indexed field.

## SQL Server

Use `CommunityToolkit.VectorData.SqlServer` with SQL Server 2025, or Azure SQL Database, which have the `vector` type.

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new SqlServerVectorStore(connectionString), "docs", dimensions: 768)
    .Configure(o =>
    {
        o.KeyType = RagKeyType.Guid;                             // required on SQL Server
        o.DistanceFunction = DistanceFunction.CosineDistance;    // required on SQL Server
    })
    .Build();
```

Prerequisites:

- **Full-Text Search must be installed.** The connector creates a full-text index on the chunk text and runs native hybrid search with `FREETEXTTABLE`. The official Linux container images leave it out; on Linux, install the `mssql-server-fts` package at the server's exact version. Without it, the first call fails with the "rejected RagDotNet's collection" error carrying SQL Server's "Full-Text Search is not installed". External content mode (`ContentStorageMode.External`) stores no text, so it needs no full-text index.
- **Use a user database, not `master`.** Full-text indexes are not allowed in `master`, so set `Initial Catalog` (or `Database`) in the connection string.

Settings:

- **`RagOptions.KeyType = RagKeyType.Guid` is required.** The connector stores string keys as `NVARCHAR(4000)`, and SQL Server cannot use a key that long for the full-text index (900 bytes at most). With string keys, the first call fails with the "rejected RagDotNet's 'docs' collection (key type String, ...)" error and leaves no table behind.
- **`RagOptions.DistanceFunction = DistanceFunction.CosineDistance` is required.** SQL Server does not support RagDotNet's default, `CosineSimilarity`. With the default, the first call fails with the "rejected RagDotNet's 'docs_documents' collection (..., distance CosineSimilarity, ...)" error, before anything is written.
- RagDotNet creates its two tables (the collection name and `<name>_documents`) and the full-text index on first use.

Known differences:

- Hybrid search is native: SQL Server runs the vector query and a `FREETEXTTABLE` keyword query and fuses the two result lists. Word splitting and stemming follow SQL Server's full-text rules, so the order of keyword matches can differ slightly from the in-memory keyword index.
- SQL Server fills the full-text index in the background. For a few seconds after an ingest, the keyword half of hybrid search can miss a new chunk; the vector half still finds it.
- There is no separate keyword index, so `IRagAdmin.RebuildKeywordIndexAsync` throws `InvalidOperationException` saying keyword search is native.

## Collections from earlier builds

RagDotNet's stored layout changed during stage 8, and collections created by an earlier build are not upgraded in place:

- **`ListOrder`.** The document list (`<name>_documents`) has a numeric `ListOrder` field, which the document listing pages by, because some stores (Qdrant) cannot order by the record key.
  - SQLite adds the column, but the existing documents have no value in it, so the calls that list documents (`IRagAdmin.GetStatsAsync`, `IRagAdmin.ReindexAsync`) fail with an `InvalidOperationException` that names `ListOrder` and this page.
  - PostgreSQL does not add the column, so ingesting and listing fail with PostgreSQL's `column "ListOrder" does not exist`.
- **Bool and date metadata** are now stored as numbers (see [Per-store settings](#per-store-settings)). A store whose columns were created with the old types rejects the new values or never matches them in filters.
- **The common area** (documents ingested without a partition) is now stored with the partition value `~common` instead of an empty string, because Redis cannot query empty strings. Common-area documents written by an earlier build are no longer found.

The in-memory store keeps nothing between runs, so it is not affected. For every other store, delete both collections and ingest the documents again.

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

The in-memory and SQLite stores need nothing and always run. Stores that need Docker (PostgreSQL, Qdrant, Redis and SQL Server) run only when `RAGDOTNET_INTEGRATION=1` is set, and are skipped with the reason when their dependency is missing. Each starts one container per test run through Testcontainers, from a pinned image (`pgvector/pgvector:0.8.7-pg17`, `qdrant/qdrant:v1.20.1`, `redis/redis-stack-server:7.4.0-v8`). SQL Server's image is built by the test run from the pinned `mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04` plus Full-Text Search (`tests/RagDotNet.StoreTests/Infrastructure/SqlServer/Dockerfile`); the first build takes a few minutes, and later local runs reuse Docker's cache. With the repository's Docker build image, `./docker/sdk/run.ps1` mounts the Docker socket so these containers can start. In CI, `RAGDOTNET_STORES_REQUIRED=1` turns a store that cannot start into a failure, and a minimum test count fails the run if tests go missing.
