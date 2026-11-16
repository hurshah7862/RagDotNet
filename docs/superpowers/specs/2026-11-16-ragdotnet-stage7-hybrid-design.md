# RagDotNet stage 7: hybrid search and reranking

Refines the main spec (`2026-10-18-ragdotnet-design.md`) §5.5 (retrieval), §5.9 (keyword index) and §5.10 (reranking) for build stage 7. Where this document and the main spec disagree, this document wins for stage 7. The owner asked for this design to be written from the main spec without a question round, so every decision not already in the main spec is marked *(builder's call)*.

## 1. Why

Vector search finds passages that mean the same as the question, but it misses exact terms: version numbers, error codes, product and function names, part numbers. Keyword search finds those, and reciprocal rank fusion (RRF) combines the two lists without having to compare their scores. A reranker then reorders the best candidates by reading them against the question.

The stage 6 scanned check gives one measured reason: OCR text has no headings, so its chunks are large and mixed, and the scanned set scores MRR 0.710 against 0.836 for Markdown. Keyword matching inside large chunks may close part of that gap. Stage 7 measures it (see 8).

## 2. Goals

- Hybrid retrieval: vector search plus keyword search, fused with RRF.
- Store-native hybrid search where the vector collection offers it (`IKeywordHybridSearchable<TRecord>`), and an `IKeywordIndex` fallback where it does not.
- Two keyword indexes: `InMemoryKeywordIndex` in Core, and `LuceneKeywordIndex` in a new package, `RagDotNet.Keyword.Lucene`.
- `IReranker` in Core, and `LlmReranker` built on the app's `IChatClient`.
- The keyword index stays in step with the vector store through ingest, update, delete, admin deletes and restarts.
- The access scope and caller filter apply inside keyword searches exactly as they do inside vector searches.
- The evaluation harness compares vector, hybrid, and hybrid with reranking.

## 3. Non-goals and anti-goals

- **Non-goals:**
  - A cross-encoder or cloud reranker. The interface allows one; v1 ships only `LlmReranker`.
  - Tuning the analyzer per language. Both indexes use one English-oriented analyzer (below).
  - Query syntax for users (phrases, `AND`/`OR`, wildcards). The query is plain text.
  - Sharing one in-memory index across processes.
- **Anti-goals** (failure even if it technically works):
  - Vector-only retrieval that changes. With `Hybrid = Off`, retrieval output is identical to stage 6.
  - A choice the app cannot make. Hybrid is the default *(owner's decision)*, but an app can always choose vector-only, hybrid, or hybrid with reranking, for the whole app or for one call.
  - A keyword search that returns a chunk the access scope would hide, or a chunk from another tenant.
  - A keyword index that silently drifts from the vector store, so deleted documents keep appearing or new ones never do.
  - Core taking a dependency on Lucene.

## 4. Public API

### 4.1 Options

`RagOptions` gains *(names from the main spec; defaults are builder's calls unless the main spec gives them)*:

| Option | Default | Meaning |
|---|---|---|
| `Hybrid` | `HybridMode.Auto` | `Auto`: hybrid when available, else vector-only with a one-time warning log. `On`: `HybridSearchUnavailableException` when neither native hybrid nor a keyword index is available. `Off`: vector-only. |
| `RerankCandidates` | `20` | How many fused candidates the reranker sees, from 1 to 100. |
| `HybridCandidates` | `20` | How many results each search path (vector, keyword) returns per query before fusion, from 1 to 100; at least `TopK` is always fetched *(builder's call)*. |

`RetrievalOptions` gains `Hybrid` (`HybridMode?`, null uses `RagOptions.Hybrid`) and `UseReranker` (`bool?`; null means "use the registered reranker if there is one", false skips it for this call, true throws `InvalidOperationException` when none is registered) *(builder's call: the main spec names the options, not their nullability)*.

`AskOptions` carries the same two through its retrieval options, as it does for `TopK` today.

### 4.2 Keyword index

```csharp
public interface IKeywordIndex
{
    string Name { get; }                       // e.g. "in-memory", "lucene"; used in logs and telemetry
    Task UpsertAsync(IReadOnlyList<KeywordChunk> chunks, CancellationToken ct);
    Task DeleteDocumentAsync(string documentId, string? partition, CancellationToken ct);
    Task<IReadOnlyList<KeywordHit>> SearchAsync(string query, KeywordFilter filter, int top, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
    Task RebuildAsync(IAsyncEnumerable<KeywordChunk> source, CancellationToken ct);
}
```

- `KeywordChunk`: `ChunkKey`, `DocumentId`, `Partition` (null for the common area), `ModelId`, `Text`, and declared metadata values.
- `KeywordHit`: `ChunkKey` and `Score` (BM25, higher is better).
- `KeywordFilter`: the same constraint a vector search gets: the partition rule (own partition, common area, or both), `AccessScope.Filter`, the caller's `RagFilter`, and, during a reindex, the required model id. Core builds it; each index translates it (main spec §5.5 "each backend gets a translator").
- The index returns keys only. Core fetches the chunk records for the hits from the vector collection in one `GetAsync` call, which supplies pages, heading path, metadata and, in-vector mode, text *(builder's call: one source of truth for chunk data; the index stores only what it needs to search and filter)*. A key the collection no longer has is dropped and counted as drift (5.3).
- The main spec's `DeleteDocumentAsync(id)` gains the partition, because the same `DocumentId` can exist in two partitions (main spec §5.8) *(builder's call)*. `CountAsync` is added for the startup consistency check *(builder's call)*.

### 4.3 Reranker

```csharp
public interface IReranker
{
    Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken ct);
}
public sealed record RerankResult(int Index, double Score); // index into candidates; score 0 to 1, higher is more relevant
```

Returning indexes rather than chunks keeps a reranker from altering or inventing chunks *(builder's call)*. Core validates the result: indexes in range and distinct, scores finite and from 0 to 1; anything else throws `InvalidOperationException` naming the reranker.

### 4.4 Registration

- DI: `.AddInMemoryKeywordIndex()`, `.AddLuceneKeywordIndex(o => o.Directory = "./kw-index")`, `.AddLlmReranker(o => ...)`, `.AddReranker<T>()`, `.AddKeywordIndex<T>()`. Each replaces an earlier registration of the same kind: there is at most one keyword index and one reranker.
- Pipeline builder: `UseKeywordIndex(IKeywordIndex)`, `UseReranker(IReranker)`, and `AddLuceneKeywordIndex(...)` / `AddLlmReranker(...)` extensions.
- **Hybrid by default** *(owner's decision)*: `AddRag` and the pipeline builder register `InMemoryKeywordIndex` unless the app registers another keyword index, so hybrid search works with no setup on any store. It is built only when the keyword path is `index` (5.1): with a store that has native hybrid, or with `Hybrid = Off` in `RagOptions`, it stays empty and costs nothing *(builder's call)*.
- **Choosing per app:** `RagOptions.Hybrid = HybridMode.Off` for vector-only; register a reranker for hybrid with reranking; `.AddLuceneKeywordIndex(...)` instead of the in-memory index for large corpora or a persisted index.
- **Choosing per call:** `RetrievalOptions.Hybrid` (`Off` for vector-only on this call) and `RetrievalOptions.UseReranker` (`false` to skip the reranker on this call).

## 5. Behaviour

### 5.1 Which keyword path runs

Decided once, when the engine is first ready, and logged at information level *(builder's call)*:

1. **Native:** in-vector content mode, and the chunk collection implements `IKeywordHybridSearchable<Dictionary<string, object?>>`. The `Text` property is already full-text indexed. A registered keyword index is then not used or written to, and a one-time information log says so (main spec §5.9: the index is a fallback only).
2. **Index:** otherwise, a registered `IKeywordIndex`.
3. **None:** vector-only (`Auto` warns once; `On` throws on each retrieve).

External content mode never uses native hybrid (the vector database holds no text). `Hybrid = On` with external content mode and no keyword index fails at startup with `OptionsValidationException` (main spec §7).

If the native call throws `NotSupportedException` (a connector that implements the interface but cannot serve dynamic records), the engine switches to the index path, or to none, for the rest of the process, and logs a warning once *(builder's call)*.

### 5.2 Retrieval steps

For each planned query (after query transformers):

1. **Vector search**, as today, but fetching `max(TopK, HybridCandidates)` results when hybrid runs (`TopK` when it does not, so vector-only output is unchanged).
2. **Keyword search:**
   - Native: one `HybridSearchAsync` call with the query vector, the query's keywords, the same filter as vector search, and `AdditionalProperty = Text`. Its output is already fused; it replaces step 1's vector call rather than adding to it. Keywords are the query's terms from the same tokenizer the in-memory index uses (lowercased, stop words removed) *(builder's call)*.
   - Index: `SearchAsync` with the `KeywordFilter`, `top = max(TopK, HybridCandidates)`; records fetched by key; then RRF (k = 60) of the vector and keyword lists.
3. Lists from several queries are fused with RRF and de-duplicated by key, as today.
4. **Reranking**, when a reranker runs: the top `RerankCandidates` fused chunks are passed with the original question (not a rewritten query) *(builder's call)*; the result is the reranker's order and scores, `ScoreKind.Rerank`.
5. External content mode: text is fetched for the final candidates in one batch, before reranking (main spec §5.15), so the reranker reads text.
6. `MinScore`, then `TopK`.

**Scores and `MinScore`** (main spec §5.5): vector-only results keep `Similarity`; fused results `Fused`; reranked results `Rerank`. `MinScore` applies to rerank scores and to similarity in vector-only mode. In hybrid mode without a reranker it does not apply and logs a one-time warning. Native hybrid results are `Fused`, whatever scale the store uses.

**Reindex in progress:** reads are limited to chunks already on the new model (main spec §5.8). The keyword filter includes the model id; the in-memory and Lucene indexes store it per chunk, and the reindex updates it document by document under the existing per-document lock *(builder's call)*.

### 5.3 Keeping the index in step

- **Ingest:** keyword upsert happens after the chunks are written and before the manifest is finalised, so a crash or failure in between leaves the manifest in progress and a retry rewrites both (main spec §5.3 write sequence) *(builder's call)*. Unchanged documents do not touch the index.
- **Update:** stale chunk keys deleted from the vector store are deleted from the index in the same step.
- **Delete** (`DeleteDocumentAsync`, admin partition deletes): the index is cleared for those documents after the vector store.
- **Reindex:** updates each document's model id in the index (text does not change).
- **`Hybrid = Off` in `RagOptions`:** the index is neither written nor built. Turning hybrid on later triggers the startup check below, which rebuilds it.
- **Startup check** (first `EnsureReadyAsync`): the index's `CountAsync` is compared with the sum of manifest `ChunkCount`s. On a mismatch, the index is rebuilt from the vector store (keys from the manifests, text from records or the content store), and a warning logs the two counts. The in-memory index is always empty at start, so it is always built here (main spec §5.9 "lazily rebuilt at startup").
- **At query time:** a hit whose record is gone counts as drift; it is dropped, logged once per process, and counted in a `rag.keyword.drift` counter. No rebuild is triggered from a query *(builder's call: a query should not pay for a rebuild)*.
- **Manual rebuild:** `IRagAdmin.RebuildKeywordIndexAsync(ct)` *(builder's call)*.
- A failure writing the index fails the ingest call, as a vector write failure does.

### 5.4 `InMemoryKeywordIndex` (Core)

- BM25, k1 = 1.2, b = 0.75 (main spec).
- Tokenizer *(builder's call)*: Unicode letters and digits form terms; lowercase (invariant); terms joined by `.`, `-` or `_` inside a word are indexed both whole and as parts, so `v2.3.1`, `ERR-404` and `max_tokens` match exactly and by part; a short English stop-word list; no stemming.
- Filters evaluated in memory against stored metadata, with the same missing-value rule as the stores: a chunk without a value never matches a condition on that field.
- Thread-safe for concurrent searches and writes.
- Being the default, its startup build reads every chunk once (text from records, or from the content store in external content mode). The startup log gives the chunk count and build time, and the docs say when to switch to Lucene or `Hybrid = Off`.
- One process only: another process's writes are not seen until this one restarts. Searches stay correct, only less complete: a document another instance added is still found by vector search, and one it deleted is dropped as drift (5.3). The same holds for Lucene (one writer per directory). The docs say so, and point apps that ingest from several instances to a vector store with native hybrid search.
- Recommended for small to medium corpora; the docs give the measured memory per 10,000 chunks.

### 5.5 `LuceneKeywordIndex` (`RagDotNet.Keyword.Lucene`)

- Lucene.NET 4.8 (prerelease), so the package version always carries `-preview` (main spec §6).
- Options: `Directory` (required), validated at startup.
- `StandardAnalyzer` (main spec). Text is indexed, not stored (main spec §5.15). Stored fields: chunk key, document id, partition, model id, and declared metadata as indexed, untokenized fields for filters. `string[]` metadata is one field value per element.
- The filter translator builds a Lucene `BooleanQuery`; missing values never match.
- One `IndexWriter` per process; commits after each ingest call; a searcher refreshed after each commit (near-real-time reader). A second process opening the same directory fails at startup with a clear message naming the directory (Lucene's write lock) *(builder's call)*.
- The query is parsed with the same analyzer and terms are combined as `SHOULD` clauses, so user text never hits Lucene query syntax errors *(builder's call)*.
- Disposed with the DI container; the pipeline builder path documents disposal as Tesseract's does.

### 5.6 `LlmReranker`

- Uses the registered `IChatClient`.
- Candidates go in groups of `BatchSize` (default 10) per chat call (main spec: batched, one call per candidate group), at most `MaxConcurrency` (default 4) calls at once.
- Each candidate is numbered and truncated to `MaxCandidateTokens` (default 512, counted with the pipeline's tokenizer).
- The prompt asks for JSON, one integer 0 to 10 per numbered passage, judging how well the passage answers the question. Scores are divided by 10. Ties keep fused order.
- A response that cannot be parsed is retried once. If it still fails, the reranker gives up for that call: retrieval returns the fused order and scores (`ScoreKind.Fused`) and logs a warning; it does not throw *(builder's call: a weak local model should degrade to hybrid, not break answers)*. `OperationCanceledException` and chat client errors propagate.
- Options: `BatchSize`, `MaxConcurrency`, `MaxCandidateTokens`, and `Instructions` (replaces the scoring instruction, for domain wording).

### 5.7 Telemetry

- `rag.retrieve` gains `rag.hybrid.path` (`native`, `index`, `none`) and `rag.keyword.count`.
- New span `rag.rerank` (main spec §5.13), with `rag.rerank.candidates`, `rag.rerank.calls`, and `rag.rerank.fallback` when it gave up.
- New counter `rag.keyword.drift`.

## 6. Packages

- Core: hybrid modes, `IKeywordIndex`, `KeywordChunk`/`KeywordHit`/`KeywordFilter`, `InMemoryKeywordIndex`, `IReranker`, `RerankResult`, `LlmReranker`, `HybridSearchUnavailableException`, retrieval changes, admin rebuild. No new dependencies.
- New: `RagDotNet.Keyword.Lucene` (Lucene.Net and Lucene.Net.Analysis.Common 4.8.0-beta00017), in `RagDotNet.slnx`, with its own test project.

## 7. Errors

| Situation | Result |
|---|---|
| `Hybrid = Auto`, no keyword path (the app removed the default index) | vector-only, one-time warning |
| `Hybrid = On`, no keyword path | `HybridSearchUnavailableException` on retrieve |
| `Hybrid = On`, external content mode, no keyword index | `OptionsValidationException` at startup |
| `UseReranker = true`, no reranker | `InvalidOperationException` |
| Reranker returns invalid indexes or scores | `InvalidOperationException` naming the reranker |
| `LlmReranker` cannot parse the model's reply twice | fused order, warning |
| Index count differs from manifests at startup | rebuild, warning with both counts |
| Lucene directory locked by another process | `OptionsValidationException` / startup failure naming the directory |
| `RerankCandidates` or `HybridCandidates` outside 1–100 | `OptionsValidationException` |

## 8. Evaluation

- The harness gains `--retrieval vector,hybrid,hybrid-rerank` (default `vector`, so existing reports are unchanged) *(builder's call)*. `hybrid` uses `InMemoryKeywordIndex` (the harness's in-memory store has no native hybrid). `hybrid-rerank` adds `LlmReranker` and needs a chat model, so in fake mode it uses a deterministic fake reranker that only proves the wiring.
- A real-mode run (Ollama) on the Markdown set and the scanned set, library default chunking, reports MRR, recall and hit rate for each retrieval mode, recorded in `docs/evaluation-results.md` as "Hybrid check (stage 7)".
- The dataset gains about 8 questions whose answer hinges on an exact term (a version, an error code, a setting name) that already appears in the corpus, tagged `exact-term`, reported separately so the check shows whether keyword search helps where it should *(builder's call)*. The existing 40 questions stay as they are, so earlier results remain comparable.
- No result is a gate, and the default stays hybrid whatever the numbers *(owner's decision)*. If hybrid scores worse than vector on a set, the results and `docs/retrieval.md` say so, so apps can choose `Off` knowingly.

## 9. Tests

- Default: a pipeline built with no keyword or reranker setup runs hybrid through the in-memory index; `Hybrid = Off` in options or per call gives stage 6's output and leaves the index unbuilt.
- Unit: RRF of vector and keyword lists; path selection (native, index, none) for each mode; `On`/`Auto`/`Off`; the `NotSupportedException` fallback; scope and filter inside keyword search (partition isolation, common area, `IncludeCommon`, read filters, missing values, same `DocumentId` in two partitions); reindex model-id restriction; index kept in step through ingest, update, delete, admin delete, reindex, crash between writes; startup count check and rebuild; drift handling; `MinScore` rules per score kind; reranker validation; `LlmReranker` batching, parsing, retry and fallback with a scripted chat client; vector-only output identical to stage 6 (parity test, as stage 6 did for readers).
- Tokenizer: exact terms (`v2.3.1`, `ERR-404`, `max_tokens`), case, Unicode letters, stop words.
- Native path: a test collection implementing `IKeywordHybridSearchable` (the real stores with native hybrid are covered by stage 8's shared suite).
- Lucene: persistence across restarts (main spec §9), the write lock, filters, analyzer, rebuild, disposal.
- Docs examples compile (as the README's do today).

## 10. Docs

- New `docs/retrieval.md`: vector, hybrid and reranked retrieval; choosing a keyword index; options; score kinds and `MinScore`; the in-memory index's limits; Lucene's one-writer rule; costs of `LlmReranker` (chat calls per retrieve).
- README: status row "Hybrid search and reranking | Works", a short "Hybrid search" section, the package table.
- `docs/evaluation.md`: `--retrieval` row and a "Hybrid check" section; `docs/evaluation-results.md`: the results.

## 11. Left to the builder

Internal type layout, the stop-word list, the exact rerank prompt wording, test fixture shapes, and the order of tasks in the plan.
