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
foreach (var c in answer.Citations) Console.WriteLine($"[{c.Number}] {c.Chunk.SourceName} — {c.Location}");
```

`IRagPipeline` exposes `IngestAsync`, `IngestManyAsync`, `DeleteDocumentAsync`, `RetrieveAsync`, `AskAsync` and `AskStreamingAsync`. Text and Markdown readers are built in.

The first `Build()` or `BuildAdmin()` freezes the builder: later `Use*`, `Configure` or `AddReader` calls throw `InvalidOperationException`, so configure everything first (or create a new builder). Repeated `Build()`/`BuildAdmin()` calls share one engine.

## Concurrency

Ingest and delete of the same document (same partition and document id) are serialized within a process, across every pipeline and admin built from one builder or one DI container. Writers in different processes must not ingest or delete the same document concurrently: nothing coordinates them, and interleaved writes can leave chunks the document's manifest does not cover.

## Access control

**Full access.** With no `IAccessScope`, or a scope that returns `null`, the caller has full access. A write goes to the tenant named by the document's metadata (every partition key set), or to the common area when it names none.

**Single-tenant and multi-tenant.** Without `PartitionKeys` the app is single-tenant: there is no tenant to name, and the one area is the common area. `AccessScope.Unpartitioned(filter, permissions)` gives read filters and permissions without a partition. With `PartitionKeys` (declared as string fields in `o.Metadata`) each tenant gets its own partition, stamped on every chunk and part of the chunk key.

**`AccessScope`.** A scope carries a partition, an optional filter (ANDed with the caller's filter inside every query) and `RagPermissions`:

```csharp
// A tenant user who may search but not change anything.
new AccessScope(
    new Dictionary<string, string> { ["tenant"] = "acme" },
    RagFilter.ContainsAny("groups", userGroups),
    RagPermissions.Read);
```

**Common documents.** Tenants see their own documents plus the common ones. `RetrievalOptions.IncludeCommon = false` limits a tenant to its own documents (it has no effect without `PartitionKeys`). In a multi-tenant app the common area is read-only to tenants: a scoped write or delete never changes a common document, and ingesting an id that exists only in the common area creates a separate tenant document. In a single-tenant app the one area is the common area, so scoped users (including `AccessScope.Unpartitioned`) read, write and delete there according to their permissions and filter.

**Write rules for scoped users.**

- Ingest needs `RagPermissions.Write` and delete needs `RagPermissions.Delete` (the example above grants only `Read`).
- You can't change what you can't read: ingesting over an existing document that the scope filter hides throws `RagAccessDeniedException`, and deleting a hidden document returns `false`.
- A new document's own metadata must satisfy `scope.Filter`.
- A full-access pipeline's `DeleteDocumentAsync` acts on the common area. To delete a tenant's document with full access, use `IRagAdmin.DeleteDocumentAsync(id, partition)` or `IRagAdmin.ForScope`.
- A full-access ingest that sets only some partition keys, or an empty value, throws `ArgumentException`.

**Writes with a tenant user's rights.** `IRagAdmin.ForScope(scope)` returns a pipeline bound to an explicit scope, ignoring any registered one, so a back-office job can write on behalf of a tenant user and still have the user's read filter and permissions applied. An invalid scope throws `ArgumentException` immediately. Everything else on `IRagAdmin` bypasses scopes; keep it away from end users. `DeleteDocumentAsync(id, null)` deletes from the common area.

**Known leak.** Because of the "can't change what you can't read" rule, a scoped write refused over a hidden document reveals that the id exists. To avoid this, give each group its own document id prefix so users never write ids outside their own group.

## Caching

Wrap the app's `IChatClient` and `IEmbeddingGenerator` with the Microsoft.Extensions.AI caching wrappers before handing them to RagDotNet, for example `new ChatClientBuilder(inner).UseDistributedCache(cache).Build()` and `new EmbeddingGeneratorBuilder<string, Embedding<float>>(inner).UseDistributedCache(cache).Build()`. This is safe with access control: the prompt contains the retrieved documents, so new documents or a different access scope produce a different prompt and miss the cache. Semantic caching is not provided.

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
