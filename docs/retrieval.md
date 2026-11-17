# Retrieval

`RetrieveAsync` and `AskAsync` find the chunks that answer a question in one of three ways:

| Mode | What runs | Scores (`ScoreKind`) |
|---|---|---|
| Vector | Vector search only. | `Similarity`, 0 to 1 |
| Hybrid (the default) | Vector search and keyword search, fused with reciprocal rank fusion (RRF). | `Fused` |
| Hybrid with reranking | Hybrid, then a reranker reads the best candidates against the question and reorders them. | `Rerank`, 0 to 1 |

Vector search finds passages that mean the same as the question. It misses exact terms: version numbers, error codes, setting and function names, part numbers. Keyword search finds those, and RRF combines the two ranked lists without comparing their scores. A reranker then reads the top candidates, which helps when the right passage is in the candidate list but not at the top.

## Choosing the mode

Hybrid search works with no setup: RagDotNet registers an in-memory keyword index unless you register another one.

**For the whole app:**

```csharp
services.AddRag(o =>
{
    o.Hybrid = HybridMode.Off;        // vector-only
})
.AddLlmReranker();                    // hybrid with reranking, over the registered IChatClient
```

`RagOptions.Hybrid` is `Auto` by default: hybrid when a keyword search is available, and vector-only with a one-time warning when it is not (for example, when an app removed the keyword index registration). `On` makes it a requirement: retrieval throws `HybridSearchUnavailableException` when no keyword search is available, and an app with external content storage and no keyword index fails at startup. `Off` is vector-only, and the keyword index is then neither written nor built.

**For one call:**

```csharp
var chunks = await rag.RetrieveAsync(question, new RetrievalOptions
{
    Hybrid = HybridMode.Off,   // vector-only for this call
    UseReranker = false,       // skip the registered reranker for this call
});
```

`RetrievalOptions.Hybrid` null uses the app's setting. `UseReranker` null runs the registered reranker if there is one; `true` throws `InvalidOperationException` when none is registered. Both pass through `AskOptions.Retrieval`. A call can turn hybrid search off but not on when the app has `Hybrid = Off`, since no keyword index is kept then; asking for `On` in that case throws `HybridSearchUnavailableException`.

With `Hybrid = Off`, results are exactly what vector-only retrieval returned before hybrid search existed.

## Where keyword search comes from

Chosen once, when the pipeline first runs, and written to the log (`Keyword search: native (...)`, `index (...)` or `none (...)`):

1. **Native.** The vector store's own hybrid search, when its collection implements `IKeywordHybridSearchable` and chunk text is stored in the vector store. One call does both searches and fuses them. A registered keyword index is then not used or written to. If the store's connector throws `NotSupportedException` for it, RagDotNet switches to the keyword index (building it first) for the rest of the process and logs a warning once.
2. **Keyword index.** An `IKeywordIndex` that RagDotNet keeps in step with the vector store: the in-memory index by default, or the Lucene index.
3. **None.** Vector-only.

External content storage never uses native hybrid search, because the vector store holds no text.

### The in-memory index (default)

Both keyword indexes receive each chunk's heading path along with its text, as the embedding does, so a question that names a section title finds that section's chunks.

`InMemoryKeywordIndex` is part of `RagDotNet.Core`. It ranks with BM25 (k1 = 1.2, b = 0.75). Its tokenizer:

- lowercases Unicode letters and digits;
- keeps `v2.3.1`, `ERR-404` and `max_tokens` whole and also indexes their parts, so both `ERR-404` and `404` find them;
- drops common English stop words ("the", "and", "of" and 30 more);
- does not stem: `notes` does not match `note`.

It lives in the process's memory and starts empty, so it is built from the vector store when the pipeline first runs: every chunk is read once, and the log reports the chunk count and build time.

Measured on synthetic text with a 20,000-word vocabulary (.NET 10, Linux container on a desktop PC):

| 10,000 chunks of | Build | Memory | One search |
|---|---:|---:|---:|
| 100 words | 0.9 s | 69 MB | 5 ms |
| 300 words | 2.6 s | 101 MB | 5 ms |

Both grow in step with the chunk count, so 100,000 chunks of 300 words need about 1 GB and half a minute. The build time is the indexing alone; reading the chunks from the vector store (and the content store, in external content mode) comes on top and depends on the store. The index keeps terms and metadata, not chunk text.

It is one process's index. If several instances of an app ingest into the same vector store, each instance sees only its own writes until it restarts. Searches stay correct but less complete: a document another instance added is still found by vector search, and one it deleted is dropped from keyword results. Apps that ingest from several instances should use a vector store with native hybrid search.

Switch to the Lucene index, or set `Hybrid = Off`, when the startup build takes too long or the memory matters.

### The Lucene index

`RagDotNet.Keyword.Lucene` (a preview package, because Lucene.NET 4.8 is a prerelease) keeps the index on disk, so it survives restarts and is not rebuilt each time:

```csharp
services.AddRag(o => { /* ... */ })
    .AddLuceneKeywordIndex(o => o.Directory = "./kw-index");
```

- It ranks with BM25 (k1 = 1.2, b = 0.75), like the in-memory index; filters select hits without changing their scores. Chunk text is indexed but not stored.
- It splits text with Lucene's `StandardAnalyzer`, which differs from the in-memory tokenizer: `ERR-404` becomes `err` and `404`, and `max_tokens` stays one word with no parts. Switching index can change which exact terms match.
- Query text is never parsed as Lucene syntax: `title:(foo AND bar)` searches for the words title, foo and bar. Long queries, such as a HyDE passage, use their first 1,024 distinct terms.
- **One writer per directory.** Only one process, and one index instance, can open a directory. A second one fails at startup with a message naming the directory.
- **Disposal.** With dependency injection the container disposes the index. With the pipeline builder, `.AddLuceneKeywordIndex(...)` holds the directory for the life of the process; to release it earlier, create the index yourself and dispose it when the pipeline is no longer used:

```csharp
using var index = new LuceneKeywordIndex(new LuceneKeywordIndexOptions { Directory = "./kw-index" });
var rag = builder.UseKeywordIndex(index).Build();
```

### Your own index

Implement `IKeywordIndex` and register it with `.AddKeywordIndex<T>()` or `UseKeywordIndex(...)`. RagDotNet hands it `KeywordChunk`s on ingest, chunk keys to delete, and a `KeywordFilter` with every search. The filter carries the same constraint a vector search gets: the partitions a hit may come from (the common area is `""`), whether the common area is excluded, and the metadata condition with values already converted to their declared types. A chunk without a value for a field never matches a condition on that field. The index returns chunk keys and scores; RagDotNet reads the chunks from the vector store.

## Keeping the index in step

- **Ingest** writes a document's chunks to the index after the vector store and before the document is marked done, so a failure in between leaves the document to be retried, and the retry writes both. Unchanged documents do not touch the index. A failed index write fails the ingest call.
- **Update and delete** remove the same chunk keys from the index that they remove from the vector store, including the tail of a document that got shorter.
- **Reindexing** to a new embedding model does not touch the index: the text does not change. While a reindex is in progress, keyword hits not yet re-embedded are left out, as vector hits are.
- **At startup** the index's chunk count is compared with the documents' chunk counts. When they differ, the index is rebuilt from the vector store (text comes from the content store in external content mode) and a warning gives both counts.
- **At query time** a keyword hit whose chunk the vector store no longer has is dropped, logged once, and counted in the `rag.keyword.drift` metric. Queries never trigger a rebuild.
- **By hand**, `IRagAdmin.RebuildKeywordIndexAsync()` rebuilds the index. Ingests and deletes in this process wait while it runs, so none is lost.

Access control applies inside keyword search exactly as it does inside vector search: a tenant's keyword search never returns another tenant's chunk, and the common area and read filters work the same way.

## Reranking

Register a reranker for the whole app with `.AddLlmReranker()` (dependency injection or pipeline builder), `.AddReranker<T>()` or `UseReranker(...)`. On each retrieval:

1. Retrieval collects `max(TopK, RerankCandidates)` fused candidates (`RerankCandidates` defaults to 20, from 1 to 100). In external content mode their text is fetched first.
2. The reranker reads them against the user's own question, not a rewritten one, and returns the best ones with scores from 0 to 1.
3. `MinScore`, then `TopK`, apply to those scores.

A custom reranker implements `IReranker` and returns candidate indexes with scores. RagDotNet checks the answer: indexes must be distinct and in range, and scores from 0 to 1, or the call throws `InvalidOperationException` naming the reranker.

### LlmReranker

`LlmReranker` asks the app's chat model to score each candidate from 0 to 10 for how well it answers the question:

- Candidates go in groups of `BatchSize` (default 10) per chat call, at most `MaxConcurrency` (default 4) calls at once, each candidate cut to `MaxCandidateTokens` (default 512) tokens.
- **Cost:** `ceil(RerankCandidates / BatchSize)` chat calls per retrieval, 2 with the defaults, on top of the answer itself. They run before the first streamed token.
- A reply that cannot be read as scores is retried once. If it still cannot be read, retrieval returns the fused order and logs a warning instead of failing. Chat errors and cancellation are not caught. This guards against unreadable replies only: a model that returns valid but poor scores is used as is (see [How well it works](#how-well-it-works)).
- **Prompt size:** one call carries up to `BatchSize × MaxCandidateTokens` tokens of passages, about 5,000 with the defaults. Make sure the model's context window holds that. Ollama's default window was 4,096 tokens in our runs, and it cuts longer prompts without an error; lower `BatchSize` or `MaxCandidateTokens`, or raise the window.
- **Speed on a CPU:** `llama3.2:3b` read about 70 tokens a second, so a call took 10 to 30 seconds with short chunks and up to two minutes with 512-token chunks. Raise your HTTP client's timeout accordingly, or use a hosted model.
- `Instructions` replaces the scoring instruction, for domain wording; the reply format is always requested.

## Scores and MinScore

| Results | `ScoreKind` | `MinScore` |
|---|---|---|
| Vector-only | `Similarity` | applies |
| Hybrid, no reranker | `Fused` (RRF, roughly 0.01 to 0.03; native hybrid uses the store's own scale) | ignored, with a one-time warning: fused scores are not similarities |
| Reranked | `Rerank` | applies to the reranker's scores |

With a reranker, `MinScore` does not cut similarity scores before reranking.

## Options

| Option | Default | Meaning |
|---|---|---|
| `RagOptions.Hybrid` | `Auto` | `Auto`, `On` or `Off`, as above. |
| `RagOptions.HybridCandidates` | `20` | Results each search (vector, keyword) returns per query before fusion, from 1 to 100; at least `TopK`. |
| `RagOptions.RerankCandidates` | `20` | Fused candidates the reranker reads, from 1 to 100. |
| `RetrievalOptions.Hybrid` | null | Per call; null uses `RagOptions.Hybrid`. |
| `RetrievalOptions.UseReranker` | null | Per call; null runs the registered reranker, false skips it. |

## Telemetry

The `rag.retrieve` span gains `rag.hybrid.path` (`native`, `index` or `none`) and `rag.keyword.count` (keyword hits after filtering). Reranking adds a `rag.rerank` span with `rag.rerank.candidates`, `rag.rerank.calls` (for `LlmReranker`) and `rag.rerank.fallback` when the reranker gave up. The `rag.keyword.drift` counter counts keyword hits dropped because the vector store no longer had the chunk.

## How well it works

Measured with the evaluation harness on 2026-11-17 (`nomic-embed-text` embeddings, library default chunking). MRR per set:

| Set | Vector | Hybrid | Hybrid, reranked with `llama3.2:3b` |
|---|---:|---:|---:|
| `base` (48 questions, short sections) | 0.806 | 0.773 | 0.273 |
| `base`, the 8 exact-term questions | 0.656 | 0.705 | 0.354 |
| `base` as scanned PDFs | 0.722 | 0.772 | not run |
| scanned, the 8 exact-term questions | 0.781 | 0.938 | not run |
| `long-sections` (20 questions) | 0.783 | 0.823 | not run |
| `all` (68 questions) | 0.800 | 0.788 | not run |

The reranked column was measured before section headings were indexed, when hybrid scored 0.743 on `base`.

- **Hybrid is better** on questions that hinge on an exact term, and on large chunks (scans, long sections).
- **Hybrid is worse on `base` as a whole**, and slightly worse on `all`. Many of its questions are paraphrased to share few words with the answer, where keyword search has little to go on. If your users mostly ask in their own words about short sections, compare `Hybrid = Off` on your own documents.
- **A small local model is a poor reranker.** `llama3.2:3b` returned valid scores, so nothing fell back, but the scores were bad enough to cut MRR from 0.743 to 0.273. Use `LlmReranker` with a capable chat model, and measure it before relying on it. Stronger models were not measured here.

[evaluation.md](evaluation.md#hybrid-check) has the method, the full discussion and the limits.
