# RagDotNet.DataIngestion

Use [Microsoft.Extensions.DataIngestion](https://www.nuget.org/packages/Microsoft.Extensions.DataIngestion) readers and chunkers in [RagDotNet](https://github.com/hurshah7862/RagDotNet). **A preview**, because Microsoft.Extensions.DataIngestion is one.

```csharp
services.AddRag(o => o.Chunking = o.Chunking with { Chunker = "ms-header" })
    .AddDataIngestionReader(_ => new MarkdownReader(), [".md"], ["text/markdown"])
    .AddDataIngestionChunker("ms-header", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o)));
```

- Readers: headers become the heading path, tables table blocks, images with bytes OCR candidates; errors become `DocumentReadException`.
- Chunkers: the chunk size follows RagDotNet's `ChunkingOptions`; `HeaderChunker`'s chunks keep their heading path; chunks have no page numbers.

See [docs/dataingestion.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/dataingestion.md).
