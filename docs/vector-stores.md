# Vector stores

RagDotNet works with any [Microsoft.Extensions.VectorData](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) connector that supports dynamic collections. The stores below are the ones it tests: each one runs the same shared suite on every pull request.

| Store | Package | Key types | Hybrid search | Status |
|---|---|---|---|---|
| In-memory | `CommunityToolkit.VectorData.InMemory` | String, Guid | In-memory keyword index | Tested. For development and tests, not production |
| SQLite (sqlite-vec) | `RagDotNet.VectorData.Sqlite` | | | Coming in stage 8 |
| PostgreSQL (pgvector) | `CommunityToolkit.VectorData.PgVector` | | | Coming in stage 8 |
| Qdrant | `CommunityToolkit.VectorData.Qdrant` | | | Coming in stage 8 |
| Redis (Redis Stack) | `CommunityToolkit.VectorData.Redis` | | | Coming in stage 8 |
| SQL Server 2025 | `CommunityToolkit.VectorData.SqlServer` | | | Coming in stage 8 |

Other connectors (Weaviate, Azure AI Search, Cosmos DB and more) may work but are not tested.

## Per-store settings

Some stores need settings different from the defaults. Qdrant, for example, needs `RagOptions.KeyType = RagKeyType.Guid`. Each store's settings are listed in its section on this page as the store is added. Set them when you pick the store. If a setting is wrong, RagDotNet fails at startup, or at the latest on the first call to the store, with an error that names the setting to change. It does not detect the store or change its defaults for you.

`RagOptions.DistanceFunction` applies to both of RagDotNet's collections: the chunks and the document list.

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

The in-memory store needs nothing and always runs. Stores that need Docker run only when `RAGDOTNET_INTEGRATION=1` is set, and are skipped with the reason when their dependency is missing. In CI, `RAGDOTNET_STORES_REQUIRED=1` turns a store that cannot start into a failure, and a minimum test count fails the run if tests go missing.
