# RagDotNet.VectorData.Sqlite

A SQLite vector store for [RagDotNet](https://github.com/hurshah7862/RagDotNet): a Microsoft.Extensions.VectorData connector on SQLite with [sqlite-vec](https://github.com/asg017/sqlite-vec). Everything lives in one database file, so it suits desktop apps, local tools and small services.

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .UseVectorStore(new SqliteVectorStore("Data Source=rag.db"), "docs", dimensions: 768)
    .Build();
```

- Vector search is an exact scan, so filters never cut results short. It suits up to a few hundred thousand chunks; for more, use a server store.
- Hybrid search runs natively on SQLite full-text search (FTS5).
- sqlite-vec is included for win-x64, linux-x64 and osx-arm64. On other platforms set `SqliteVectorStoreOptions.VecExtensionPath` to a sqlite-vec loadable extension.
- Metadata dates are stored in UTC: they come back as the same moment with a zero offset.

See [docs/vector-stores.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/vector-stores.md) for the supported stores and their settings.
