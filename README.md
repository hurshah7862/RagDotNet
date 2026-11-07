# RagDotNet

RagDotNet is a .NET library for retrieval-augmented generation (RAG). It ingests documents, chunks and embeds them into a vector store, and answers questions with cited sources, built on Microsoft.Extensions.AI and Microsoft.Extensions.VectorData.

> **Under development.** RagDotNet is not released yet. The public API can still change between commits, stored data may need re-ingesting after an update, and no package is published to NuGet. Core ingestion, retrieval, cited answers, chat and query rewriting work today; fixed-size and semantic chunkers, PDF/DOCX/HTML readers, OCR, hybrid search and reranking, more vector stores, and hosting adapters are still to come. Feedback and issues are welcome.

## Install

No package is on NuGet yet. Until then, clone the repository and reference the project directly:

```
dotnet add reference path/to/RagDotNet/src/RagDotNet.Core/RagDotNet.Core.csproj
```

Once published, it will be `dotnet add package RagDotNet.Core`.

RagDotNet.Core targets net8.0 and net10.0. You bring your own `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI) and `VectorStore` (any Microsoft.Extensions.VectorData connector). The samples and tests use the in-memory store from `CommunityToolkit.VectorData.InMemory`, which is not meant for production.

## Quick start with dependency injection

```csharp
services.AddRag(o =>
{
    o.CollectionName = "docs";
    o.EmbeddingDimensions = 768;
    o.Metadata.Add<string>("tenant");
    o.PartitionKeys.Add("tenant"); // each tenant gets its own partition
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

The first `Build()` or `BuildAdmin()` freezes the builder: later `Use*`, `Configure`, `AddReader` or `AddChunker` calls throw `InvalidOperationException`, so configure everything first (or create a new builder). Repeated `Build()`/`BuildAdmin()` calls share one engine.

## Concurrency

Ingest, delete and reindex of the same document (same partition and document id) are serialized within a process, across every pipeline and admin built from one builder or one DI container. Writers in different processes must not ingest or delete the same document concurrently: nothing coordinates them, and interleaved writes can leave chunks the document's manifest does not cover.

## Switching embedding models

Each document records the embedding model it was embedded with. Once you configure a different `EmbeddingModelId` (or a generator that reports a different model), ingest, delete and questions throw `EmbeddingModelMismatchException` until you call `IRagAdmin.ReindexAsync()`. It re-embeds every document not yet on the configured model, across all partitions. The embedding dimensions can't change: it probes the generator first and throws `InvalidOperationException` before writing anything if they differ.

**The reindex marker.** Reindex writes a marker record to the documents collection. It names the target model and the instance running the reindex, and carries a heartbeat. The heartbeat is refreshed once 60 seconds have passed since the last refresh, checked after the initial wait, between embedding batches, after each document and every 60 seconds while waiting for a document that is being ingested or deleted in this process, so a single slow embedding call delays it. The marker is deleted when the reindex finishes.

**During a reindex.** Questions keep working, but they search only documents already re-embedded, so answers are incomplete until it finishes; run it off-peak. Each process logs a warning once per reindex. Ingest and delete keep working too: a document being re-embedded waits for its reindex to finish (within a process), and a document that changes during reindex is not overwritten. Instances configured with any other model keep throwing `EmbeddingModelMismatchException`.

**Resuming after an interruption.** If reindex fails or is cancelled, the marker stays and questions keep using the re-embedded documents. Call `ReindexAsync()` again: in the same process it resumes at once and re-embeds only what is left. Only one reindex runs per process; a second concurrent call throws `InvalidOperationException`.

**Takeover after 5 minutes.** A reindex started from another process (or another engine) while a marker with a fresh heartbeat exists throws `InvalidOperationException`. A marker whose heartbeat is more than 5 minutes old is considered abandoned, and the next `ReindexAsync()` takes it over. A heartbeat in the future (clock skew between machines) counts as fresh, so the marker is treated as live.

**Running several instances.**

- Pause other writers during reindex. If a document changes in another process during reindex, that document is skipped; if it ends up on a model other than the configured one, reindex keeps the marker and throws `InvalidOperationException` asking you to run it again.
- Restart every instance after switching models, so they all run with the new model. Instances still configured with the old model keep writing old-model data until they restart.
- Each instance notices a marker, or the end of a reindex, within `RagOptions.ModelCheckInterval` (default 5 seconds, at most 1 minute; `TimeSpan.Zero` checks on every call). An instance already configured with the new model starts answering from re-embedded data within that interval, without a restart. Reindex waits one `ModelCheckInterval` after writing the marker before it writes any new vector, so instances that cached "no reindex" start filtering first.

## Access control

**Full access.** With no `IAccessScope`, or a scope that returns `null`, the caller has full access. A write goes to the tenant named by the document's metadata (every partition key set), or to the common area when it names none.

**Single-tenant and multi-tenant.** Without `PartitionKeys` the app is single-tenant: there is no tenant to name, and the one area is the common area. `AccessScope.Unpartitioned(filter, permissions)` gives read filters and permissions without a partition; a single-tenant scope's partition must be empty, and a scope that sets partition values without `PartitionKeys` configured throws. With `PartitionKeys` (declared as string fields in `o.Metadata`) each tenant gets its own partition, stamped on every chunk and part of the chunk key.

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
- You can't change what you can't read: ingesting over an existing document in your own partition that the scope filter hides throws `RagAccessDeniedException`, and deleting a hidden document returns `false`.
- A new document's own metadata must satisfy `scope.Filter`.
- A full-access pipeline's `DeleteDocumentAsync` acts on the common area. To delete a tenant's document with full access, use `IRagAdmin.DeleteDocumentAsync(id, partition)` or `IRagAdmin.ForScope`.
- A full-access ingest that sets only some partition keys, or an empty value, throws `ArgumentException`.

**Writes with a tenant user's rights.** `IRagAdmin.ForScope(scope)` returns a pipeline bound to an explicit scope, ignoring any registered one, so a back-office job can write on behalf of a tenant user and still have the user's read filter and permissions applied. An invalid scope throws `ArgumentException` immediately. Everything else on `IRagAdmin` bypasses scopes; keep it away from end users. `DeleteDocumentAsync(id, null)` deletes from the common area.

**Known leak.** Because of the "can't change what you can't read" rule, a scoped write refused over a hidden document reveals that the id exists. To avoid this, give each group its own document id prefix so users never write ids outside their own group.

## Conversations and query rewriting

**Passing a conversation.** `AskAsync`, `AskStreamingAsync` and `RetrieveAsync` each have an overload that takes the whole conversation as an `IList<ChatMessage>`, for example `rag.AskAsync(messages)`. The last message must be a user message with text, and it is the question; otherwise the call throws `ArgumentException`. The string overloads are unchanged and behave as a conversation with no history. The overloads differ only in the type of the first argument, so a literal `null` there is ambiguous and must be cast, for example `(string)null!`.

**Condense.** When the conversation has history, the question is first rewritten into a standalone question with one chat call, because a follow-up such as "How do I install it on Linux?" cannot be searched on its own. Condense runs automatically, before any configured query transformer, and the search uses its result. If the model returns nothing, the original question is searched; surrounding quotes are removed. Because it needs a chat client, `RetrieveAsync` with history throws `InvalidOperationException` when none is configured. A single-message conversation (or a string call) needs one for retrieval only when a configured query transformer needs it, such as `MultiQueryTransformer` or `HydeQueryTransformer`; then it throws `InvalidOperationException` too.

**What the model sees.** The answer prompt contains the system message, then the history messages, then the documents with the original question (not the rewritten one) and the citation reminder. History is passed to the model as-is, so send only history you trust, such as turns your app stored itself, not arbitrary assistant messages supplied by a client. `[n]` citation markers in earlier answers refer to the document numbering of those earlier answers, not the current one.

**History window.** `RagOptions.MaxHistoryMessages` (default 10, must not be negative) keeps the last N earlier messages that are from the user or the assistant and have text; other roles and contents are dropped. Condense and the answer prompt use the same window. `0` ignores history and skips Condense. The history does **not** count against `MaxContextTokens`, which budgets only the retrieved documents, so keep the window small enough for your model's context size.

**More query rewriting.** Two transformers are available and off by default. Each adds chat calls to every question. `MultiQueryTransformer(count)` asks for alternative phrasings of the question and searches them all, merging the results with reciprocal rank fusion. `HydeQueryTransformer` searches with a generated passage that would answer the question, one chat call per query. Add them to `o.QueryTransformers` for all calls, or set `RetrievalOptions.QueryTransformers` per call (`null` uses the default, an empty list runs none). The per-call list never switches Condense off. Transformers run in order, each one working on the previous one's queries; at most 10 queries are searched, and each `rag.transform` span reports the transformer and the number of queries.

**Latency and cost.** Condense and the transformers run before retrieval, so they add to the time before the first streamed token. HyDE makes one chat call per query: with history, `MultiQueryTransformer(3)` and then `HydeQueryTransformer`, a question costs 6 chat calls before the answer (1 Condense, 1 MultiQuery, 4 HyDE for the original and 3 alternatives). A call that is refused (no Read permission, invalid options) or whose scope and filter can match nothing makes no chat calls.

**Custom transformers.** Implement `IQueryTransformer.TransformAsync(QueryContext, CancellationToken)`. `QueryContext` has the `Question`, the windowed `History`, the current `Queries` (the previous step's output) and the `ChatClient`, which is null when none is configured. Return the new list of queries; empty and duplicate entries are dropped, and an empty result keeps the previous queries.

## Chunking

The default chunker is `structure-aware` (`Chunkers.StructureAware`). It splits along headings, paragraphs, sentences and lines, never across headings, and merges small blocks. Its settings are `ChunkingOptions.MaxTokens` (default 512, at least 16) and `OverlapRatio` (default 0.10, in [0, 0.5)).

**Choosing a chunker.** `ChunkingOptions.Chunker` names the chunker. Set it as the default with `o.Chunking = new() { Chunker = "my-chunker" }`, per content type with `o.ChunkingByContentType["text/markdown"] = new() { Chunker = "my-chunker" }`, or per call with `new IngestOptions { Chunking = new() { Chunker = "my-chunker" } }`. Names are matched ordinally and are case-sensitive. An unregistered name in the options fails at startup (or `Build`); an unregistered per-call name throws `ArgumentException` before anything is written.

**Writing a chunker.** Implement `IChunker`: a unique `Name`, a `Version`, and `ChunkAsync`. The name and version go into each document's chunking hash, so **bump `Version` whenever the chunker's output changes** and every document is re-chunked on its next ingest. Changing `MaxTokens`, `OverlapRatio` or the tokenizer does the same.

**What the library guarantees.** Whatever a chunker returns, the library drops chunks whose text is blank, splits chunks longer than `MaxTokens` (keeping the heading path and pages), renumbers `Index` from 0 with no gaps or duplicates, and recomputes `TokenCount` with its own tokenizer. A chunker that returns `null`, or a list containing `null`, fails the ingest with an `InvalidOperationException` naming the chunker.

**Registration.** With dependency injection call `.AddChunker<MyChunker>()` (a singleton); without it call `builder.AddChunker(new MyChunker())`. Two chunkers with the same name, including `structure-aware`, fail validation.

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

## Evaluation

`tests/RagDotNet.Evaluation` measures how well retrieval finds the passage that answers each of 40 labelled questions, reporting recall@1/3/5 and MRR for each chunk size, overlap and retrieval mode. `dotnet run --project tests/RagDotNet.Evaluation` runs it on fake embeddings; add `-- --embedder ollama` for real numbers. See [docs/evaluation.md](docs/evaluation.md) for the method and the baseline results.

## License

MIT
