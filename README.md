# RagDotNet

RagDotNet is a .NET library for retrieval-augmented generation (RAG). It ingests documents, chunks and embeds them into a vector store, and answers questions with cited sources, built on Microsoft.Extensions.AI and Microsoft.Extensions.VectorData.

## Install

```
dotnet add package RagDotNet.Core
```

RagDotNet.Core targets net8.0 and net10.0. You bring your own `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI) and `VectorStore` (any Microsoft.Extensions.VectorData connector). The samples and tests use the in-memory store from `CommunityToolkit.VectorData.InMemory`, which is not meant for production.

## Quick start with dependency injection

```csharp
services.AddRag(o =>
{
    o.CollectionName = "docs";
    o.EmbeddingDimensions = 768;
    o.Metadata.Add<string>("tenant");
})
.AddReader<MyReader>()
.AddAccessScope<TenantAccessScope>();
// The app separately registers IChatClient, IEmbeddingGenerator and VectorStore.
```

Options bind through `IOptions<RagOptions>` and are validated at startup. `IRagPipeline` is registered scoped so it can resolve a per-request access scope.

## Quick start without dependency injection

```csharp
var rag = new RagPipelineBuilder()
    .UseChatClient(chat)
    .UseEmbeddingGenerator(embedder)
    .UseVectorStore(store, "docs", dimensions: 768)
    .Configure(o => o.Metadata.Add<string>("category"))
    .AddReader(new MyReader())
    .Build();           // or .BuildAdmin() for IRagAdmin

await rag.IngestAsync(DocumentSource.FromFile("guide.md"));
RagAnswer answer = await rag.AskAsync("How do I install on Linux?");
foreach (var c in answer.Citations) Console.WriteLine($"[{c.Number}] {c.Chunk.SourceName} - {c.Location}");
```

`IRagPipeline` exposes `IngestAsync`, `IngestManyAsync`, `DeleteDocumentAsync`, `RetrieveAsync`, `AskAsync` and `AskStreamingAsync`. Text and Markdown readers are built in.

## Access scope

Register an `IAccessScope` (`.AddAccessScope<T>()` or `.UseAccessScope(scope)`) to partition data per tenant. Its partition values are stamped on every chunk and become part of the chunk key; its filter is ANDed with the caller's filter inside every query. Partition keys must be declared in `o.Metadata`. Admin operations live on the separate `IRagAdmin` and bypass the scope.

## External content mode

By default chunk text is stored in the vector record. To keep it in your own database, implement `IChunkContentStore` and call `.UseExternalContent<MyStore>()` (DI) or `.UseContentStore(store)` (builder). The vector store then holds only vectors, keys and metadata.

## Sample app

```
dotnet run --project samples/RagDotNet.Samples.Console -- tests/TestAssets/markdown
```

Ingests every `.md`/`.txt` in the folder, then answers questions with streamed text and a numbered citation list; an empty line exits. The provider is set in `appsettings.json`: `Ollama` (default; `llama3.2:3b` and `nomic-embed-text` at `http://127.0.0.1:11434`), `GitHubModels` (needs `GITHUB_TOKEN`) or `OpenAI` (needs `OPENAI_API_KEY`).

## Running the tests

```
dotnet test
```

Unit tests never need a model or network. The Ollama end-to-end test in `tests/RagDotNet.IntegrationTests` is skipped unless `RAGDOTNET_INTEGRATION=1` is set and Ollama is running with the two models pulled:

```
ollama pull llama3.2:3b
ollama pull nomic-embed-text
RAGDOTNET_INTEGRATION=1 dotnet test --project tests/RagDotNet.IntegrationTests
```

## License

MIT
