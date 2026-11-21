# RagDotNet.Keyword.Lucene

A persisted keyword index for [RagDotNet](https://github.com/hurshah7862/RagDotNet) hybrid search, built on Lucene.NET 4.8.

RagDotNet runs hybrid search (vector plus keyword, fused with reciprocal rank fusion) by default, with an in-memory keyword index that is rebuilt from the vector store each time the app starts. Use this package instead when the corpus is large, or when the startup build takes too long: the index lives on disk and survives restarts.

```csharp
services.AddRag(o => { /* ... */ })
    .AddLuceneKeywordIndex(o => o.Directory = "./kw-index");
```

With the pipeline builder:

```csharp
var rag = new RagPipelineBuilder()
    // ...
    .AddLuceneKeywordIndex(o => o.Directory = "./kw-index")
    .Build();
```

- Only one process can write to a directory. A second process (or a second index on the same folder) fails at startup with a message naming the directory.
- With dependency injection the container disposes the index. With the pipeline builder it holds the directory for the life of the process; to release it earlier, create a `LuceneKeywordIndex` yourself, pass it to `UseKeywordIndex`, and dispose it.
- Lucene.NET 4.8 is a prerelease, so this package is a preview.

See [docs/retrieval.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/retrieval.md) for how hybrid search works and how to choose an index.
