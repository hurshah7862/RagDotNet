# RagDotNet Stage 7: Hybrid Search and Reranking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make retrieval hybrid by default (vector + keyword, fused with RRF), with optional reranking. Apps can choose vector-only, hybrid or hybrid with reranking, for the whole app or per call.

**Architecture:**
- **Keyword path.** Core decides once per engine which keyword path runs:
  - `native`: the chunk collection implements `IKeywordHybridSearchable`;
  - `index`: an `IKeywordIndex`, by default the auto-registered `InMemoryKeywordIndex`;
  - `none`.
- **Index sync.** Ingest and delete keep the index in step by chunk key. A startup count check rebuilds the index from the vector store.
- **Retrieval.** The `Retriever` runs:
  1. vector search;
  2. keyword search, with its hits resolved to chunk records by key;
  3. RRF fusion;
  4. an optional `IReranker`, applied to the top candidates.
- **New package.** `RagDotNet.Keyword.Lucene` adds a persisted index.

**Tech Stack:**
- .NET 10 SDK; Core targets net8.0 and net10.0.
- Microsoft.Extensions.VectorData.Abstractions 10.8.2; Microsoft.Extensions.AI.
- New: Lucene.Net and Lucene.Net.Analysis.Common 4.8.0-beta00017.
- xUnit v3; Docker (`ragdotnet-sdk:ocr`) for every build and test.

**Spec:** `docs/superpowers/specs/2026-11-16-ragdotnet-stage7-hybrid-design.md`. It refines §5.5, §5.9 and §5.10 of `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`; where the two differ, the stage 7 spec wins.

## Global Constraints

**Dependencies**
- `RagDotNet.Core` gains no package dependency and still targets net8.0 and net10.0.
- New `src/RagDotNet.Keyword.Lucene`:
  - references Core, Lucene.Net 4.8.0-beta00017 and Lucene.Net.Analysis.Common 4.8.0-beta00017;
  - targets net8.0 and net10.0, has PublicAPI files, treats warnings as errors and uses central package versions;
  - its version always carries `-preview`.
- Registration follows stages 5 and 6: one `*Extensions.cs` file holding
  - `Microsoft.Extensions.DependencyInjection.<Name>Extensions` with `Add…(this IRagBuilder)`;
  - `RagDotNet.Configuration.<Name>BuilderExtensions` with `Add…(this RagPipelineBuilder)`.

**Values from the spec**
- `HybridMode { Auto, On, Off }`, default `Auto`.
- `RerankCandidates` = 20 and `HybridCandidates` = 20, each from 1 to 100.
- RRF k = 60; reuse `RankFusion.Reciprocal`.
- BM25 k1 = 1.2, b = 0.75.
- `LlmReranker`: `BatchSize` 10, `MaxConcurrency` 4, `MaxCandidateTokens` 512; scores 0–10 are divided by 10; one retry, then fall back to fused order.
- Score kinds:
  - vector-only results are `Similarity`;
  - fused and native-hybrid results are `Fused`;
  - reranked results are `Rerank`.
- `MinScore` applies to `Rerank` and `Similarity` only. With `Fused` it is ignored, with one warning.

**Parity**
- `Hybrid = Off` (in options or per call) gives retrieval output identical to stage 6, and the keyword index is neither written nor built.

**Environment**
- Every `dotnet build`/`test`/`run` runs in Docker.
- From PowerShell: `$a = @('test','--project','<proj>','-f','net10.0','--','--filter-class','<Full.Class.Name>'); & ./docker/sdk/run.ps1 @a`.
- "Run Core tests" below means that command with `tests/RagDotNet.Core.Tests`.

**PublicAPI**
- After adding public API, build, collect the RS0016 symbols from the output, and append them to `PublicAPI.Unshipped.txt`.

## Review Focus

1. **A query that is only stop words or punctuation** (`"the"`, `"?"`) gives no keyword terms. Hybrid then returns the vector list fused alone; nothing throws. Test in Task 6.
2. **A long query** (a HyDE passage, 2,000 words) on Lucene must not throw `TooManyClauses`. Terms are de-duplicated and capped at 1,024. Test in Task 9.
3. **Ingest running while the in-memory index is searched** must not throw or return torn results. Test in Task 2.
4. **Documents with zero chunks**, unchanged re-ingests, and a shrinking update keep the index count equal to the sum of manifest `ChunkCount`s, so a restart does not rebuild every time. Test in Task 5.
5. **Lucene filters on `bool`, `long`, `DateTimeOffset` and `string[]` metadata, and on missing values,** match exactly what the vector store matches. Test in Task 9.

---

### Task 1: Options and public contracts

**Files:**
- Create in `src/RagDotNet.Core/Retrieval/`: `HybridMode.cs`, `HybridSearchUnavailableException.cs`, `IReranker.cs` (with `RerankResult`).
- Create in `src/RagDotNet.Core/Keyword/`: `IKeywordIndex.cs`, `KeywordChunk.cs`, `KeywordHit.cs`, `KeywordFilter.cs`.
- Modify: `Configuration/RagOptions.cs`, `Configuration/RagOptionsValidator.cs`, `Retrieval/RetrievalOptions.cs`, `PublicAPI.Unshipped.txt`.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/HybridOptionsTests.cs`.

**Interfaces:**
- Produces:
  - `enum HybridMode { Auto, On, Off }`.
  - `RagOptions`:
    - `HybridMode Hybrid { get; set; } = Auto`;
    - `int RerankCandidates { get; set; } = 20`;
    - `int HybridCandidates { get; set; } = 20`.
  - `RetrievalOptions`:
    - `HybridMode? Hybrid { get; init; }`;
    - `bool? UseReranker { get; init; }`.
  - `HybridSearchUnavailableException : InvalidOperationException`, with `(string message)`.
  - `interface IKeywordIndex`, exactly as spec §4.2, with `DeleteAsync(IReadOnlyCollection<string> chunkKeys, CancellationToken)`.
  - `sealed record KeywordChunk(string ChunkKey, string DocumentId, string Partition, string Text, IReadOnlyDictionary<string, object?> Metadata)`.
  - `sealed record KeywordHit(string ChunkKey, double Score)`.
  - `sealed record KeywordFilter(IReadOnlyList<string>? Partitions, bool ExcludeCommon, RagFilter? Metadata)`, with `static KeywordFilter All`.
  - `interface IReranker { Task<IReadOnlyList<RerankResult>> RerankAsync(string query, IReadOnlyList<RetrievedChunk> candidates, int top, CancellationToken ct); }`.
  - `sealed record RerankResult(int Index, double Score)`.
  - All public types sit in the namespaces `RagDotNet.Retrieval` and `RagDotNet.Keyword`.

- [ ] **Step 1: Spike the native hybrid API.** In a scratch test, assert at compile time that `IKeywordHybridSearchable<Dictionary<string, object?>>.HybridSearchAsync<ReadOnlyMemory<float>>(vector, ICollection<string> keywords, int top, HybridSearchOptions<Dictionary<string, object?>>, CancellationToken)` exists in 10.8.2. Also check whether `CommunityToolkit.VectorData.InMemory` 1.0.1's dynamic collection implements it.
  - Record both facts in the ledger and delete the scratch test.
  - If the InMemory collection does implement it, Task 5's tests that need the index path use the `NoHybridVectorStore` wrapper (Task 5).
- [ ] **Step 2: Write failing tests** in `HybridOptionsTests`:
  - `Defaults_are_Auto_and_20_candidates`;
  - `Candidates_outside_1_to_100_fail_validation`: a `[Theory]` over `RerankCandidates` and `HybridCandidates` with 0 and 101, asserting `OptionsValidationException` from `AddRag` host start and `ArgumentException` from `RagPipelineBuilder.Build()`, as existing option validation does;
  - `Hybrid_On_with_external_content_and_no_keyword_index_fails_at_startup`. Leave this test skipped until Task 4 adds registration, then turn it on there.
- [ ] **Step 3: Run Core tests** for `HybridOptionsTests`. Expected: compile failure, because the types are missing.
- [ ] **Step 4: Implement** the types and validator rules. Messages:
  - `"RerankCandidates must be between 1 and 100."`
  - `"HybridCandidates must be between 1 and 100."`
- [ ] **Step 5: Run Core tests.** Expected: PASS (one skipped). Update PublicAPI.
- [ ] **Step 6: Commit** `feat(hybrid): options and contracts for keyword search and reranking`.

### Task 2: Tokenizer and `InMemoryKeywordIndex`

**Files:**
- Create: `src/RagDotNet.Core/Keyword/KeywordTokenizer.cs` (internal), `src/RagDotNet.Core/Keyword/InMemoryKeywordIndex.cs` (public sealed).
- Test: `tests/RagDotNet.Core.Tests/Hybrid/KeywordTokenizerTests.cs`, `tests/RagDotNet.Core.Tests/Hybrid/InMemoryKeywordIndexTests.cs`.

**Interfaces:**
- Produces:
  - `internal static class KeywordTokenizer { IReadOnlyList<string> Terms(string text); }`. These are the index terms, also used for native-hybrid keywords.
  - `public sealed class InMemoryKeywordIndex : IKeywordIndex`, with `Name == "in-memory"`.

- [ ] **Step 1: Write failing tokenizer tests:**
  - `Terms("Upgrade to v2.3.1 now")` contains `"v2.3.1"`, `"v2"`, `"3"`, `"1"`, `"upgrade"`;
  - `Terms("ERR-404 max_tokens")` contains `"err-404"`, `"err"`, `"404"`, `"max_tokens"`, `"max"`, `"tokens"`;
  - `Terms("The and of")` is empty (stop words);
  - `Terms("?!")` is empty;
  - `Terms("Größe Ünïcode")` gives `"größe"`, `"ünïcode"` (invariant lowercase, Unicode letters);
  - no stemming: `Terms("notes")` is `["notes"]`.
- [ ] **Step 2: Write failing index tests:**
  - `Exact_term_ranks_its_chunk_first`: three chunks; searching `"ERR-404"` puts the chunk containing `ERR-404` at rank 1.
  - `Bm25_prefers_rarer_terms_and_shorter_chunks`: two chunks contain the query term, one 5 words long and one 50; the short one ranks first.
  - `Filter_partitions_and_common_area`: chunks in partitions `"a"`, `"b"` and `""`.
    - `KeywordFilter(["a", ""], false, null)` returns a and common only;
    - `KeywordFilter(null, true, null)` excludes common.
  - `Metadata_filter_and_missing_values`: a `RagFilter.Eq("category", "x")` matches only chunks whose `category` is `"x"`; a chunk without the field never matches. The evaluator is `RagFilterEvaluator.Matches` over the stored metadata.
  - `Delete_by_keys_removes_only_those_chunks` and `Upsert_same_key_replaces_text`.
  - `Count_and_rebuild`: `RebuildAsync` replaces all content; `CountAsync` equals the chunk count.
  - Review Focus 3, `Concurrent_upserts_and_searches_do_not_throw`: 4 writers × 200 upserts while 4 readers search in a loop for 2 s; no exception, and every hit's key exists at the end or was deleted.
- [ ] **Step 3: Run Core tests** for both classes. Expected: compile failure.
- [ ] **Step 4: Implement.**
  - Tokenizer rules are in spec §5.4; the stop-word list is the 33-word Lucene `EnglishAnalyzer` default set.
  - The index keeps per-term postings `Dictionary<string, Dictionary<string, int>>` (term → key → tf), document lengths, and the stored `KeywordChunk`, all guarded by a `ReaderWriterLockSlim`.
  - `KeywordFilter.Metadata` values arrive already converted, so the index evaluates them with an internal overload of `RagFilterEvaluator` that skips schema conversion. Name it `RagFilterEvaluator.MatchesConverted(RagFilter, string documentId, IReadOnlyDictionary<string, object?>)`.
- [ ] **Step 5: Run Core tests.** Expected: PASS. Update PublicAPI.
- [ ] **Step 6: Commit** `feat(hybrid): keyword tokenizer and in-memory BM25 index`.

### Task 3: Keyword filters from the access scope

**Files:**
- Create: `src/RagDotNet.Core/Access/KeywordFilters.cs` (internal).
- Test: `tests/RagDotNet.Core.Tests/Hybrid/KeywordFilterBuildTests.cs`.

**Interfaces:**
- Consumes: `ReadFilters.Build` semantics, `RagFilterTranslator.Simplify` and `ConvertValue`.
- Produces: `internal static KeywordFilter? KeywordFilters.Build(ResolvedScope scope, bool includeCommon, bool partitioned, RagFilter? callerFilter, MetadataSchema schema, out bool matchesNothing)`.
  - The result mirrors `ReadFilters.Build` clause for clause.
  - `Metadata` is the simplified And of the user and caller filters, with every value converted to its declared type.

- [ ] **Step 1: Write failing tests**, one per row of `ReadFilters.Build`'s summary:
  - scoped tenant with common → `Partitions = [tenant, ""]`;
  - scoped tenant without common → `[tenant]`;
  - single-tenant scoped app (empty partition key) → `[""]`;
  - full access → `Partitions = null, ExcludeCommon = false`;
  - full access without common → `ExcludeCommon = true`;
  - not partitioned ignores `includeCommon`;
  - a filter that can never match sets `matchesNothing`;
  - an `int` field given `5L` converts to `5` (or throws exactly as the translator does).
- [ ] **Step 2: Run them.** Expected: compile failure.
- [ ] **Step 3: Implement**, sharing the user/caller combination code with `ReadFilters` rather than copying it.
- [ ] **Step 4: Run them.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(hybrid): keyword filters built from the access scope`.

### Task 4: Registration and keeping the index in step

**Files:**
- Modify:
  - `Pipeline/RagEngine.cs`: `IKeywordIndex? KeywordIndex { get; init; }` and `IReranker? Reranker { get; init; }`;
  - `Configuration/RagEngineFactory.cs`: new optional parameters `keywordIndex` and `reranker`;
  - `Configuration/RagPipelineBuilder.cs`: `UseKeywordIndex(IKeywordIndex)` and `UseReranker(IReranker)`; `Build` defaults the index to `new InMemoryKeywordIndex()`;
  - `Configuration/RagServiceCollectionExtensions.cs`: `TryAddSingleton<IKeywordIndex, InMemoryKeywordIndex>()`; the engine resolves `IKeywordIndex` and `IReranker?`;
  - new `Configuration/RagHybridBuilderExtensions.cs`: `AddKeywordIndex<T>()`, `AddInMemoryKeywordIndex()` and `AddReranker<T>()`, all using `Replace`;
  - `Ingestion/Ingestor.cs`.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/KeywordIndexSyncTests.cs`.

**Interfaces:**
- Consumes: `IKeywordIndex`, `KeywordChunk` (Task 1).
- Produces:
  - `RagEngine.KeywordPath` (internal enum `KeywordPath { None, Native, Index }`), set by Task 5. Until Task 5 it is computed simply as `Hybrid != Off && KeywordIndex != null ? Index : None`.
  - `internal bool RagEngine.WritesKeywordIndex => KeywordPath == KeywordPath.Index`.

- [ ] **Step 1: Write failing tests** using a recording `IKeywordIndex` fake (`RecordingKeywordIndex` in `Fakes/`):
  - `Ingest_upserts_chunks_before_the_manifest_commit`: the fake's `UpsertAsync` checks that the manifest still has an empty `ContentHash`. Use a fake that reads it through `IRagAdmin.GetStatsAsync`, or a store hook; choose the hook in `FailingVectorStore`'s style.
  - `Unchanged_reingest_does_not_touch_the_index`.
  - `Shrinking_update_deletes_the_stale_keys` (5 chunks → 2 deletes keys 2..4).
  - `Delete_removes_the_document_keys_before_the_manifest`.
  - `Admin_delete_removes_keys`.
  - `Same_document_id_in_two_partitions_stays_apart`: deleting `doc` in tenant a leaves tenant b's keys.
  - `Hybrid_Off_never_writes_the_index`.
  - `Failure_in_keyword_upsert_fails_ingest_and_retry_converges`: the fake throws once; ingest throws; a retry ingest gives `Updated` or `Created`, and the index holds every key.
  - `Default_builder_and_AddRag_register_InMemoryKeywordIndex`.
  - `AddKeywordIndex_replaces_the_default`.
  - Turn on Task 1's skipped startup test: with `Hybrid = On`, external content, and the index removed (`services.RemoveAll<IKeywordIndex>()`, or `UseKeywordIndex(null!)` throwing), the host fails with `OptionsValidationException`.
    - Ruling: the pipeline builder has no way to remove the default index, so this case exists only in DI.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** In `Ingestor`, upsert a `KeywordChunk` per record:
  - Text comes from the chunk, even in external content mode.
  - Metadata is the document's declared metadata dictionary.
  - The upsert goes after `engine.Chunks.UpsertAsync` and before the committed manifest.
  - `DeleteRangeAsync` also calls `KeywordIndex.DeleteAsync(keys)` when `WritesKeywordIndex`.
  - The engine factory throws `ArgumentException` for `Hybrid = On` + external content + no index, wording it like the existing content-store check.
- [ ] **Step 4: Run Core tests (whole project).** Expected: PASS. Existing tests are unaffected, because retrieval does not read the index yet.
- [ ] **Step 5: Commit** `feat(hybrid): default keyword index, kept in step through ingest and delete`.

### Task 5: Keyword path selection, startup check and rebuild

**Files:**
- Modify: `Pipeline/RagEngine.cs` (`EnsureReadyAsync`), `Storage/ChunkStore.cs` (expose `IKeywordHybridSearchable<…>? Hybrid`), `Pipeline/IRagAdmin.cs` and `Pipeline/RagAdmin.cs` (`Task RebuildKeywordIndexAsync(CancellationToken cancellationToken = default)`), `Diagnostics/RagTelemetry.cs` (`rag.keyword.drift` counter).
- Create: `tests/RagDotNet.Core.Tests/Fakes/HybridVectorStore.cs` and `NoHybridVectorStore.cs`. These wrap `InMemoryVectorStore`:
  - the first returns a dynamic collection that also implements `IKeywordHybridSearchable` (vector search re-ordered by keyword term count, results `Fused`-ready);
  - the second hides the interface.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/KeywordPathTests.cs`.

**Interfaces:**
- Produces:
  - `RagEngine.KeywordPath`, decided in `EnsureReadyAsync` per spec §5.1 and logged at information level: `"Keyword search: {Path} ({Detail})"`.
  - `internal Task RagEngine.RebuildKeywordIndexAsync(CancellationToken)`: enumerates manifests (`ManifestStore.ListAsync`) and fetches records by computed key in batches of 100. Text comes from the record, or from the content store in external mode.
  - `internal void RagEngine.SwitchFromNative(Exception)`, used by Task 6.

- [ ] **Step 1: Write failing tests:**
  - `Native_store_uses_native_and_logs_that_the_index_is_unused` (HybridVectorStore).
  - `External_content_never_uses_native`.
  - `Store_without_native_uses_the_index` (NoHybridVectorStore).
  - `Off_is_None_and_builds_nothing`.
  - `Startup_builds_the_in_memory_index_from_the_store`: ingest with one engine, then build a second engine over the same store; its first retrieve finds keyword hits, and the info log names the chunk count.
  - `Startup_rebuilds_when_counts_differ`: an index fake reports the wrong count; `RebuildAsync` is called and a warning logs both counts.
  - `No_rebuild_when_counts_match`. This is Review Focus 4: it includes an empty document (0 chunks), an unchanged re-ingest, and a shrinking update, then a second engine over a persistent index fake that reports the same count; `RebuildAsync` is not called.
  - `Admin_RebuildKeywordIndexAsync_rebuilds`.
  - `Rebuild_in_external_content_mode_reads_text_from_the_content_store`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - The index count is compared with the sum of manifest `ChunkCount`s.
  - The rebuild streams `KeywordChunk`s into `RebuildAsync`.
  - `EnsureReadyAsync` keeps its retry-on-failure behaviour.
- [ ] **Step 4: Run Core tests (whole project).** Expected: PASS.
- [ ] **Step 5: Commit** `feat(hybrid): keyword path selection, startup check and index rebuild`.

### Task 6: Hybrid retrieval

**Files:**
- Modify: `Retrieval/Retriever.cs` (`Prepared` gains the resolved `HybridMode`, the `KeywordFilter` and the keyword path), `Diagnostics/RagTelemetry.cs`.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/HybridRetrievalTests.cs`, plus existing tests (below).

**Interfaces:**
- Consumes: `KeywordFilters.Build` (Task 3), `RagEngine.KeywordPath` and `SwitchFromNative` (Task 5), `KeywordTokenizer.Terms` (Task 2), `RankFusion.Reciprocal`.
- Produces: retrieval per spec §5.2. Task 7 relies on `Retriever` producing a fused candidate list of `max(TopK, RerankCandidates)` when a reranker will run, and on reranking happening before `MinScore`/`TopK`.

- [ ] **Step 1: Write failing tests:**
  - `Default_pipeline_is_hybrid_and_finds_an_exact_term_vector_search_misses`: with the fake embedder, a query `"ERR-404"` whose vector is far from the chunk is still returned, with `ScoreKind.Fused`.
  - `Off_in_options_and_per_call_gives_stage6_output`: parity. The same ingest and queries give identical results (keys, scores, kinds) with `RagOptions.Hybrid = Off` and with `RetrievalOptions.Hybrid = Off`, compared to a run with no keyword index at all.
  - `On_without_a_keyword_path_throws_HybridSearchUnavailable` (DI with the index removed).
  - `Auto_without_a_keyword_path_warns_once`.
  - `Scope_applies_inside_keyword_search`: tenant b's chunk that matches the keyword is never returned to tenant a; the common area is returned unless `IncludeCommon = false`.
  - `Reindex_in_progress_drops_keyword_hits_not_yet_reembedded`.
  - `Drifted_key_is_dropped_logged_once_and_counted`: the index holds a key the store lacks.
  - `MinScore_is_ignored_for_fused_results_with_one_warning`.
  - `Native_path_makes_one_hybrid_call_and_no_index_call`.
  - `Native_NotSupported_switches_to_index_once`.
  - `Multi_query_fuses_each_query_hybrid_then_across_queries`.
  - `External_content_hybrid_fetches_text_once`.
  - Review Focus 1, `Stop_word_query_returns_the_vector_results`.
  - `Telemetry_tags_hybrid_path_and_keyword_count`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Per-path top: `Math.Max(TopK, HybridCandidates)` when hybrid runs; `TopK` otherwise.
  - Keyword hits are resolved with `ChunkStore.GetAsync(keys)` (one call per query). Hits are dropped when the record is missing (drift), or when a reindex is in progress and the record's `EmbeddingModelId` is not the configured one.
  - Native path: `HybridSearchOptions { Filter = prepared.Filter, AdditionalProperty = r => r["Text"] }`.
  - Warnings, exactly:
    - `"Hybrid search is unavailable: the vector store has no native hybrid search and no keyword index is registered. Retrieval is vector-only. Register one with AddInMemoryKeywordIndex() or AddLuceneKeywordIndex()."`
    - `"MinScore is ignored for hybrid results without a reranker, because fused scores are not similarities."`
    - `"The keyword index returned chunk {ChunkKey}, which the vector store does not have; the index is out of step. Run IRagAdmin.RebuildKeywordIndexAsync to fix it."`
- [ ] **Step 4: Run the whole Core project.** Existing tests that assert vector-only behaviour (`ScoreKind.Similarity`, `MinScore` on similarity, exact rankings from the fake embedder) now fail, because hybrid is the default.
  - For each one, set `o.Hybrid = HybridMode.Off` in that test only. Do not change it in `TestRag.Create`, so the default path stays covered.
  - Ledger the list of tests changed as one ruling.
  - Any other failure is a bug: debug it.
- [ ] **Step 5: Run every test project** (`run.ps1 test RagDotNet.slnx`). Expected: PASS. The evaluation harness tests must still pass: its `vector` mode sets `Hybrid = Off` (Task 10 adds the other modes; make `vector` explicit now if a harness test fails).
- [ ] **Step 6: Commit** `feat(hybrid): hybrid retrieval with RRF, native hybrid and per-call mode`.

### Task 7: Reranking in retrieval

**Files:**
- Modify: `Retrieval/Retriever.cs`, `Diagnostics/RagTelemetry.cs`.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/RerankingTests.cs` (scripted `IReranker` fakes).

**Interfaces:**
- Consumes: `IReranker`, `RerankResult` (Task 1), `RagEngine.Reranker` (Task 4).
- Produces: an internal `RerankFallbackException : Exception`. `LlmReranker` (Task 8) throws it to ask for the fused order; Core catches it, returns the fused order, and logs.

- [ ] **Step 1: Write failing tests:**
  - `Registered_reranker_reorders_candidates_with_Rerank_scores`.
  - `Reranker_sees_RerankCandidates_and_the_original_question`: with multi-query, the reranker gets the user's question, not a rewrite.
  - `UseReranker_false_skips_it`.
  - `UseReranker_true_without_one_throws`: `"UseReranker is true but no reranker is registered. Register one with AddLlmReranker() or AddReranker<T>()."`
  - `Invalid_indexes_or_scores_throw_naming_the_reranker`: out-of-range, duplicate, NaN, 1.5.
  - `MinScore_applies_to_rerank_scores`.
  - `Reranker_with_vector_only_retrieval_still_runs`.
  - `Fallback_exception_returns_fused_order_and_warns`.
  - `Cancellation_propagates`.
  - `Rerank_span_has_candidates_count_and_fallback_tag`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The candidate pool is `Math.Max(TopK, RerankCandidates)`. In external content mode, text is fetched before reranking.
- [ ] **Step 4: Run Core tests.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(hybrid): optional reranking of fused candidates`.

### Task 8: `LlmReranker`

**Files:**
- Create: `src/RagDotNet.Core/Retrieval/LlmReranker.cs` (public sealed), `LlmRerankerOptions.cs`; extension methods `AddLlmReranker(this IRagBuilder, Action<LlmRerankerOptions>?)` and `AddLlmReranker(this RagPipelineBuilder, Action<LlmRerankerOptions>?)` in `RagHybridBuilderExtensions.cs`.
- Test: `tests/RagDotNet.Core.Tests/Hybrid/LlmRerankerTests.cs` (a scripted `FakeChatClient`).

**Interfaces:**
- Produces:
  - `public LlmReranker(IChatClient chat, LlmRerankerOptions? options = null, Tokenizer? tokenizer = null)`.
  - `LlmRerankerOptions { int BatchSize = 10; int MaxConcurrency = 4; int MaxCandidateTokens = 512; string? Instructions; }`, validated: each ≥ 1.
  - The pipeline builder's `AddLlmReranker` uses the builder's chat client at `Build()` and throws `InvalidOperationException("AddLlmReranker needs a chat client. Call UseChatClient() first.")` without one. The DI version resolves `IChatClient` and fails host start with `OptionsValidationException` when none is registered.

- [ ] **Step 1: Write failing tests:**
  - `Sends_candidates_in_groups_of_BatchSize`: 25 candidates make 3 calls.
  - `At_most_MaxConcurrency_calls_at_once`.
  - `Parses_scores_and_divides_by_10`; ties keep the input order.
  - `Truncates_candidates_to_MaxCandidateTokens`.
  - `Unparseable_reply_is_retried_once_then_falls_back`: it throws `RerankFallbackException`, and the pipeline returns fused results.
  - `Wrong_number_of_scores_counts_as_unparseable`.
  - `Custom_Instructions_replace_the_scoring_instruction`.
  - `Chat_errors_and_cancellation_propagate`.
  - `Builder_without_chat_client_throws`.
  - `Di_without_chat_client_fails_at_start`.
- [ ] **Step 2: Run them.** Expected: FAIL.
- [ ] **Step 3: Implement.** The prompt is the builder's wording.
  - The reply format is a JSON array of integers, e.g. `[7, 0, 10]`, one per passage in order.
  - The parser extracts the first `[...]` in the reply.
- [ ] **Step 4: Run Core tests.** Expected: PASS. Update PublicAPI.
- [ ] **Step 5: Commit** `feat(hybrid): LlmReranker`.

### Task 9: `RagDotNet.Keyword.Lucene`

**Files:**
- Create in `src/RagDotNet.Keyword.Lucene/`: `RagDotNet.Keyword.Lucene.csproj` (with `<VersionSuffix>`/MinVer settings so the version is `-preview`), `LuceneKeywordIndex.cs`, `LuceneKeywordIndexOptions.cs`, `LuceneFilterTranslator.cs` (internal), `LuceneKeywordIndexExtensions.cs`, PublicAPI files, `README.md`.
- Create: `tests/RagDotNet.Keyword.Lucene.Tests/` (project plus tests).
- Modify: `Directory.Packages.props`, `RagDotNet.slnx`, `.github/workflows/ci.yml` (a "Test Lucene (net8.0)" step in both jobs).

**Interfaces:**
- Consumes: `IKeywordIndex`, `KeywordChunk`, `KeywordHit`, `KeywordFilter` (Task 1).
- Produces:
  - `public sealed class LuceneKeywordIndex : IKeywordIndex, IDisposable`, with `Name == "lucene"`, constructed by `LuceneKeywordIndex(LuceneKeywordIndexOptions options)`.
  - `LuceneKeywordIndexOptions { string Directory }`, which is required.
  - `AddLuceneKeywordIndex(Action<LuceneKeywordIndexOptions>)` on both builders, replacing the default index.

- [ ] **Step 1: Write failing tests:**
  - `Exact_term_and_bm25_ranking`.
  - `Persists_across_restarts`: write, dispose, reopen; `CountAsync` and search agree.
  - `Delete_by_keys`.
  - `Rebuild_replaces_all`.
  - `Partition_rules`, as Task 2's test of the same name.
  - Review Focus 5, `Metadata_filters_match_the_vector_store`: `Eq` on string/int/long/bool/DateTimeOffset/double, `In`, `Contains`/`ContainsAny` on `string[]`, `And`/`Or`, missing values never match. Run the same filters against the in-memory vector store and assert equal key sets.
  - Review Focus 2, `Long_query_does_not_throw`: a 2,000-word query.
  - `Query_with_Lucene_syntax_characters_is_plain_text`: `"title:(foo AND bar)*"`.
  - `Second_process_on_the_same_directory_fails_clearly`: simulate it with a second instance in the same process. The message names the directory.
  - `Pipeline_with_AddLuceneKeywordIndex_finds_exact_terms` (end to end).
  - `Di_disposes_the_index`.
- [ ] **Step 2: Run** the Lucene test project. Expected: compile failure.
- [ ] **Step 3: Implement.**
  - Field mapping:
    - `ChunkKey` → `StringField`, stored;
    - `DocumentId` and `Partition` → `StringField`;
    - text → `TextField`, not stored;
    - metadata → one `StringField` per value. Values are normalised to invariant strings: `DateTimeOffset` as UTC ticks, numbers with `"R"`/invariant format, `bool` as `"true"`/`"false"`. `string[]` gives one field per element.
  - Queries:
    - the query text is analysed with `StandardAnalyzer(LuceneVersion.LUCENE_48)`;
    - distinct terms become `SHOULD` `TermQuery`s, capped at 1,024;
    - filters become `MUST`/`FILTER` clauses.
  - One `IndexWriter`, `Commit()` after each `UpsertAsync`/`DeleteAsync`/`RebuildAsync`, and a `SearcherManager` refreshed after each commit.
  - A `LockObtainFailedException` at open becomes `InvalidOperationException($"The Lucene keyword index directory '{dir}' is in use by another process or index instance.")`. DI turns it into `OptionsValidationException` through a validator.
- [ ] **Step 4: Run** the Lucene tests and the whole solution. Expected: PASS. Update PublicAPI.
- [ ] **Step 5: Commit** `feat(hybrid): RagDotNet.Keyword.Lucene`.

### Task 10: Evaluation harness

**Files:**
- Modify:
  - `tests/RagDotNet.Evaluation/Cli/Options.cs` (`--retrieval`);
  - `Running/EvaluationConfig.cs` (a `Retrieval` member and its matrix level; constants `vector`, `hybrid`, `hybrid-rerank`);
  - `Running/EvaluationRunner.cs`;
  - `Running/ReportWriter.cs`: a retrieval column, shown only when more than one retrieval mode ran, so default reports are byte-identical; plus an `exact-term` subset line.
- Modify: `Dataset/Question.cs`, which gains an optional `tags` (`string[]`, JSON `"tags"`).
- Modify: `Dataset/base/questions.json`, which gains 8 questions tagged `exact-term`, with ids `exact-1`…`exact-8`. Their evidence must be a version, an error code, a setting name or a command that already appears once in the corpus. The existing 40 are unchanged.
- Create: `Providers/FakeReranker.cs`, a deterministic reranker that reverses ties by key. It only proves the wiring.
- Test: `tests/RagDotNet.Evaluation.Tests/HybridModeTests.cs`.

**Interfaces:**
- Consumes: the public API from Tasks 1–8.

- [ ] **Step 1: Write failing tests:**
  - `Default_report_is_unchanged`: the default run's report equals the committed fake-mode baseline, after normalising the set hash line for the new questions (see the ruling below).
  - `Vector_mode_sets_Hybrid_Off`.
  - `Hybrid_mode_uses_the_in_memory_index`.
  - `Hybrid_rerank_uses_FakeReranker_in_fake_mode_and_LlmReranker_with_ollama`.
  - `Exact_term_subset_is_reported_per_mode`.
  - `Unknown_retrieval_mode_exits_1`.
- [ ] **Step 2: Run** the Evaluation tests. Expected: FAIL.
- [ ] **Step 3: Implement.**
  - Ruling to ledger: adding questions changes the base set's hash, so earlier results for `base` are no longer hash-comparable. `docs/evaluation-results.md` notes this, and the stage 7 run re-runs the vector baseline on the new hash.
- [ ] **Step 4: Run** the Evaluation tests, then the fake-mode CLI: `run.ps1 run --project tests/RagDotNet.Evaluation -- --retrieval vector,hybrid,hybrid-rerank --chunkers structure-aware --max-tokens 512 --overlap 0.1`. Expected: PASS, and a report with three retrieval rows.
- [ ] **Step 5: Real run.** Ollama with `nomic-embed-text` and `llama3.2:3b`, on the Markdown and scanned sets, at library default chunking: `--embedder ollama --formats md,scanned --retrieval vector,hybrid,hybrid-rerank --chunkers structure-aware --max-tokens 512 --overlap 0.1 --tessdata /usr/share/tesseract-ocr/5/tessdata`.
  - It is memory-heavy; run it in the background and watch the output file.
  - Record MRR, recall and hit rate per mode and the exact-term subset in `docs/evaluation-results.md` under "## Hybrid check (stage 7)".
- [ ] **Step 6: Commit** `feat(eval): retrieval modes and exact-term questions; hybrid check results`.

### Task 11: Docs

**Files:**
- Create: `docs/retrieval.md`.
- Modify: `README.md`, `docs/evaluation.md`.

- [ ] **Step 1: Write `docs/retrieval.md`** per spec §10. It covers:
  - the three modes and how to choose them per app and per call;
  - the keyword paths;
  - the in-memory index's startup build cost, with the measured memory and build time per 10,000 chunks (measure them with a throwaway test in Docker and quote the numbers);
  - the single-process limit;
  - Lucene's one-writer rule and disposal;
  - `LlmReranker` costs: `ceil(RerankCandidates / BatchSize)` chat calls per retrieve;
  - score kinds and `MinScore`;
  - `RebuildKeywordIndexAsync`;
  - the hybrid check results, including any set where hybrid scored worse than vector.
- [ ] **Step 2: Update `README.md`:**
  - the status row reads "Hybrid search and reranking | Works";
  - a "## Hybrid search" section with the default, the `Off` switch, Lucene and the reranker;
  - the package table row for `RagDotNet.Keyword.Lucene` (preview).
- [ ] **Step 3: Update `docs/evaluation.md`:** the `--retrieval` option row, a "## Hybrid check" section with the command, and the `tags` field in the dataset description.
- [ ] **Step 4: Run the whole solution's tests**, which include the README snippet compile tests if present. Expected: PASS.
- [ ] **Step 5: Commit** `docs: retrieval guide, hybrid check, README`.
