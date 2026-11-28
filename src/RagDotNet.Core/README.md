# RagDotNet.Core

Retrieval-augmented generation for .NET: ingest your documents, chunk and embed them into a vector store, and answer questions with cited sources. Built on Microsoft.Extensions.AI and Microsoft.Extensions.VectorData, so you bring your own chat model, embedding model and vector store.

```
dotnet add package RagDotNet.Core
```

## With dependency injection

```csharp
services.AddRag(o =>
{
    o.CollectionName = "docs";
    o.EmbeddingDimensions = 768;
    o.Metadata.Add<string>("tenant");
    o.PartitionKeys.Add("tenant"); // each tenant gets its own partition
})
.AddAccessScope<TenantAccessScope>(); // optional: your own IAccessScope
// The app separately registers IChatClient, IEmbeddingGenerator and VectorStore.
```

`IRagPipeline` is registered scoped, so it can resolve a per-request access scope.

## Without dependency injection

```csharp
var rag = new RagPipelineBuilder()
    .UseChatClient(chat)              // IChatClient
    .UseEmbeddingGenerator(embedder)  // IEmbeddingGenerator<string, Embedding<float>>
    .UseVectorStore(vectorStore, "docs", dimensions: 768)
    .Build();

await rag.IngestAsync(DocumentSource.FromFile("guide.md"));
RagAnswer answer = await rag.AskAsync("How do I install on Linux?");
Console.WriteLine(answer.Text);
foreach (var c in answer.Citations) Console.WriteLine($"[{c.Number}] {c.Chunk.SourceName} — {c.Location}");
```

## What is included

- Text and Markdown readers; PDF, DOCX and HTML come as separate `RagDotNet.Readers.*` packages.
- Structure-aware, fixed-size and semantic chunkers, and your own through `AddChunker`.
- Vector and hybrid retrieval (with an in-memory BM25 keyword index), optional reranking, cited answers and streaming.
- Conversations and query rewriting (Condense, multi-query, HyDE).
- Search as a tool for agents: `rag.AsSearchTool()` returns a Microsoft.Extensions.AI `AIFunction` that searches under the pipeline's access scope and maps the model's `[n]` citations back to passages.
- Change detection on re-ingest, update and delete; tenant partitions and access filters applied inside every query; a tracked reindex when you switch embedding models.
- OpenTelemetry-compatible tracing and metrics, with no OpenTelemetry package dependency.

Targets net8.0 and net10.0. See the [README on GitHub](https://github.com/hurshah7862/RagDotNet#readme) for the full guide, and [vector stores](https://github.com/hurshah7862/RagDotNet/blob/main/docs/vector-stores.md) for the stores RagDotNet is tested with.
