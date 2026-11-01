# RagDotNet Stage 2 — Chat and Query Transformation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let apps ask and retrieve from a whole conversation, and improve retrieval with query transformers:
- Condense runs automatically whenever there is chat history.
- MultiQuery and HyDE are opt-in.
- The results of several queries are merged with reciprocal rank fusion.

**Architecture:**
- A new internal `QueryPlanner` turns (question, history, transformers) into a list of search queries.
  - Condense always runs first when there is history; the configured or per-call transformers follow in order.
  - Every transformer gets the pipeline's `IChatClient` through `QueryContext`.
- `Retriever` accepts several queries. It embeds them in one call, searches each one with the same access, model and caller filters, and fuses the rankings with RRF (k = 60).
- Ask builds its prompt from the original question plus the conversation, never from the rewritten query.

**Tech Stack:** .NET 10 SDK, `net8.0;net10.0`, Microsoft.Extensions.AI 10.11.0 (`IChatClient`, `ChatMessage`), Microsoft.Extensions.VectorData.Abstractions 10.8.2, xUnit v3, CommunityToolkit.VectorData.InMemory 1.0.1.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`, revision 2.1. This plan covers §10 stage 2 and argues from §5.5 (steps 1 and 3, `RetrievalOptions.QueryTransformers`, the `IList<ChatMessage>` retrieve overload), §5.6 (conversation overloads of `AskAsync` and `AskStreamingAsync`), §5.7 and §5.13 (the `rag.transform` span).

## Global Constraints

- Target frameworks: `net8.0;net10.0`; warnings are errors.
- Core gets **no new package references**.
- Every public API change is reflected in `src/RagDotNet.Core/PublicAPI.Unshipped.txt`, and every public member has XML docs with `<exception>` tags.
- Unit tests never need a model, network access or Docker. Use `FakeChatClient` (a scripted `Replies` queue; it records `Requests`), `FakeEmbeddingGenerator` and `InMemoryVectorStore`.
- Test command: `dotnet test --project tests/RagDotNet.Core.Tests` (both TFMs). Build: `dotnet build` (0 warnings).
- Access, reindex and caller filters apply inside every store query, for every generated query. They are never applied after the store query.
- No document text and no question text appears in telemetry unless `EnableSensitiveData` is set. Exception and log messages never contain document text.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A conversation whose last message is not a user message with text (assistant last, an empty list, whitespace text) throws a clear `ArgumentException`. It is never silently answered. Test: Task 3.
2. The model returns messy MultiQuery output: numbering, bullets, blank lines, quotes, duplicates, or more or fewer lines than asked. The parser still yields clean, distinct alternatives, at most N of them. Test: Task 2.
3. A Condense reply that is empty, only whitespace, or wrapped in quotes falls back to the original question or has the quotes stripped. A garbage rewrite never becomes an empty search. Test: Task 3.
4. A chunk found by several queries appears once in the results, ranked above chunks found by only one query. Test: Task 1.
5. A retrieval-only app with no chat client can still retrieve from a one-message conversation. When the conversation has history, it gets a clear error saying a chat client is needed for Condense. Test: Task 3.

---

## Decisions this plan makes *(builder's calls, beyond the spec)*

**Interfaces**
- **`IQueryTransformer`:** `Task<IReadOnlyList<string>> TransformAsync(QueryContext context, CancellationToken cancellationToken)`. The spec's signature plus a cancellation token.
- **`QueryContext`:** `QueryContext(string Question, IReadOnlyList<ChatMessage> History, IReadOnlyList<string> Queries, IChatClient? ChatClient)`.
  - `Queries` is the output of the previous step. Transformers chain by transforming `Queries`.
  - Built-in transformers use `ChatClient`. When it is null they throw `InvalidOperationException($"{GetType().Name} needs a chat client. Call UseChatClient().")`.

**Configuration**
- **Where transformers are configured:**
  - `RagOptions.QueryTransformers` (`IList<IQueryTransformer>`, default empty) holds the app default.
  - `RetrievalOptions.QueryTransformers` (`IReadOnlyList<IQueryTransformer>?`) overrides it per call: null uses the default, and an empty list runs none.
  - Condense is never affected by either setting; it always runs first when there is history.
- **Chat options:** transformer chat calls pass no `ChatOptions`.

**Query planning**
- **Query list normalization**, applied after every step:
  - trim each query;
  - drop empty ones;
  - remove duplicates (ordinal, ignoring case, keeping the first);
  - cap the list at **10** queries;
  - if a step leaves nothing, keep the previous step's list.

**Built-in transformers**
- **MultiQuery:**
  - `MultiQueryTransformer(int count = 3)`; `count` must be in 1..10 (`ArgumentOutOfRangeException`).
  - It expands only the first query and returns `[first, alt1..altN, rest of the input queries…]`.
  - Alternatives that equal an existing query, ignoring case, are dropped.
- **HyDE:**
  - `HydeQueryTransformer()` replaces each query with a generated passage, one chat call per query.
  - An empty reply keeps the original query.
- **Condense:**
  - `CondenseQueryTransformer` is internal and runs only when the windowed history is non-empty.
  - It returns `[standalone question]`.
  - An empty reply falls back to the original question. Surrounding quotes (`"…"`, `'…'`, `“…”`) are stripped.

**History**
- **History window:** the last `RagOptions.MaxHistoryMessages` (default **10**, validated ≥ 0) messages before the question that have role User or Assistant and non-empty `Text`. Other roles and contents are dropped.
- **Same window everywhere:** Condense input and the answer prompt use the same window.
- **Context budget:** the history does not count against `MaxContextTokens`, and the docs say so.

**Fusion**
- **Multi-query fusion:**
  - `MinScore` applies to each query's similarity score before fusion.
  - Each query searches `TopK`.
  - RRF: `score = Σ 1/(60 + rank)`, with rank starting at 1. Duplicates are removed by `ChunkKey`, keeping the first record seen.
  - Ordering is by score descending, then `DocumentId` ordinal, then `ChunkIndex`. The top `TopK` are kept, with `ScoreKind.Fused`.
  - A single query keeps today's behaviour (`ScoreKind.Similarity`).
- **External content:** text is fetched once, for the final `TopK` only, after fusion.

**Telemetry**
- **`rag.transform` span:** one span per transformer run, including Condense.
  - Tags: `rag.transformer` (the type name) and `rag.query.count` (output count).
  - `rag.queries` (joined with `\n`) only when `EnableSensitiveData` is set.
  - `rag.retrieve` gets a `rag.query.count` tag.

**Exact prompt copy**, owned by the transformer classes:
- **Condense:**
  - System: `Rewrite the user's last question as a standalone question that can be understood without the conversation. Keep the user's language. Reply with the question only.`
  - User: `Conversation:\n` + one line per history message (`User: …` / `Assistant: …`) + `\n\nLast question: ` + question.
- **MultiQuery:**
  - System: `Write {count} different ways to ask the question below, for searching documents. Keep the meaning and the user's language. Put each on its own line, with no numbering or extra text.`
  - User: the query.
- **HyDE:**
  - System: `Write a short passage (2 to 4 sentences) that would answer the question. It is used only to search documents, so it may contain guesses. Reply with the passage only.`
  - User: the query.

## File map

| File | Change | Task |
|---|---|---|
| `src/RagDotNet.Core/Retrieval/RankFusion.cs` | new: RRF | 1 |
| `src/RagDotNet.Core/Retrieval/Retriever.cs` | several queries; batch embed; fusion; content fetch after fusion | 1 |
| `src/RagDotNet.Core/Retrieval/IQueryTransformer.cs`, `QueryContext.cs` | new public types | 2 |
| `src/RagDotNet.Core/Retrieval/QueryPlanner.cs` | new: chain, normalization, spans | 2, 3 |
| `src/RagDotNet.Core/Retrieval/MultiQueryTransformer.cs`, `HydeQueryTransformer.cs` | new public transformers | 2 |
| `src/RagDotNet.Core/Retrieval/RetrievalOptions.cs`, `Configuration/RagOptions.cs`, `RagOptionsValidator.cs` | `QueryTransformers`, `MaxHistoryMessages` | 2, 3 |
| `src/RagDotNet.Core/Pipeline/RagPipeline.cs`, `IRagPipeline.cs` | plan queries before retrieval; conversation overloads | 2, 3 |
| `src/RagDotNet.Core/Retrieval/CondenseQueryTransformer.cs` | new internal | 3 |
| `src/RagDotNet.Core/Generation/RagPromptContext.cs`, `DefaultRagPromptBuilder.cs` | `History`; history messages in the prompt | 3 |
| `README.md` | "Conversations and query rewriting" section | 3 |

---

### Task 1: Multi-query retrieval with reciprocal rank fusion

**Files:**
- Create: `src/RagDotNet.Core/Retrieval/RankFusion.cs`
- Modify: `src/RagDotNet.Core/Retrieval/Retriever.cs`, `src/RagDotNet.Core/Pipeline/RagPipeline.cs` (call sites pass `[query]`)
- Test: `tests/RagDotNet.Core.Tests/RetrievalTests.cs`

**Interfaces:**
- Produces:
  - `internal Task<IReadOnlyList<RetrievedChunk>> Retriever.RetrieveAsync(IReadOnlyList<string> queries, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)`. It replaces the single-string signature.
    - `queries` must be non-empty, with no blank entries (`ArgumentException`).
  - `internal static class RankFusion` with `public const int K = 60;` and `IReadOnlyList<RetrievedChunk> Reciprocal(IReadOnlyList<IReadOnlyList<RetrievedChunk>> rankings, int topK)`. Stage 7 reuses it for hybrid search.
- How it works:
  - Validation, the Read permission, filter building and the reindex guard happen once per call.
  - All queries are embedded in one `GenerateAsync` call; a count mismatch throws `InvalidOperationException`.
  - Each vector is searched with the same filter.

- [ ] **Step 1: Write the failing tests.** Reach the internal retriever through `((RagPipeline)rag).Engine.Retriever` with `ResolvedScope.FullAccess`.

```csharp
[Fact]
public async Task Two_queries_find_documents_each_query_alone_would_miss()
{
    // ingest d1 "alpha beta gamma", d2 "zulu yankee xray"; TopK 1
    var hits = await retriever.RetrieveAsync(["alpha beta gamma", "zulu yankee xray"], ResolvedScope.FullAccess, new() { TopK = 2 }, Ct);
    Assert.Equal(["d1", "d2"], hits.Select(h => h.DocumentId).Order());
    Assert.All(hits, h => Assert.Equal(ScoreKind.Fused, h.ScoreKind));
}

[Fact]
public async Task Chunk_found_by_several_queries_appears_once_and_ranks_first()
{
    // Review Focus 4: d1 "alpha beta", d2 "alpha zulu", d3 "zulu yankee"; queries ["alpha beta", "alpha", "beta alpha"]
    // → d1 appears exactly once and is first; no duplicate ChunkKeys in the result.
}

[Fact]
public void Reciprocal_rank_fusion_scores_and_orders_exactly()
{
    // rankings [[a, b], [b, c]] → b = 1/62 + 1/61, a = 1/61, c = 1/62; order b, a, c; topK 2 → b, a.
}

[Fact]
public async Task Single_query_keeps_similarity_scores() =>
    // RetrieveAsync(["alpha beta gamma"]) → ScoreKind.Similarity, same results as the public RetrieveAsync("alpha beta gamma").
    ...;

[Fact]
public async Task Every_query_is_access_filtered()
{
    // tenant a doc "alpha", tenant b doc "zulu"; scoped tenant a retriever with queries ["alpha", "zulu"] → only tenant a's doc.
}

[Fact]
public async Task External_content_is_fetched_once_for_the_final_results()
{
    // external content store counting GetAsync calls; 3 queries, TopK 2 → exactly one GetAsync call, with 2 keys.
}
```

- [ ] **Step 2: Run the tests and confirm they fail.** Command: `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class "*RetrievalTests"`. Expected: compile failure (no list overload, no `RankFusion`).

- [ ] **Step 3: Implement** `RankFusion` and the multi-query retriever as described in Decisions ("Multi-query fusion" and "External content"). Add the `rag.query.count` tag on `rag.retrieve`. `RagPipeline` call sites pass `[question]`.

- [ ] **Step 4: Run the full suite and confirm it passes.**

- [ ] **Step 5: Commit** with `git commit -m "feat(core): multi-query retrieval fused with reciprocal rank fusion"`.

---

### Task 2: Query transformers — chain, MultiQuery, HyDE

**Files:**
- Create: `src/RagDotNet.Core/Retrieval/IQueryTransformer.cs`, `QueryContext.cs`, `QueryPlanner.cs`, `MultiQueryTransformer.cs`, `HydeQueryTransformer.cs`
- Modify: `src/RagDotNet.Core/Retrieval/RetrievalOptions.cs`, `src/RagDotNet.Core/Configuration/RagOptions.cs`, `src/RagDotNet.Core/Pipeline/RagPipeline.cs`, `src/RagDotNet.Core/Pipeline/RagEngine.cs` (only if the planner lives there), `PublicAPI.Unshipped.txt`
- Test: `tests/RagDotNet.Core.Tests/QueryTransformerTests.cs` (new)

**Interfaces:**
- Consumes: `Retriever.RetrieveAsync(IReadOnlyList<string> queries, …)` from Task 1.
- Produces (public):
  - `IQueryTransformer`, `QueryContext`, `MultiQueryTransformer(int count = 3)`, `HydeQueryTransformer()`, as defined in Decisions.
  - `RagOptions.QueryTransformers` (`IList<IQueryTransformer>`, default empty).
  - `RetrievalOptions.QueryTransformers` (`IReadOnlyList<IQueryTransformer>?`, default null).
- Produces (internal):
  - `QueryPlanner.PlanAsync(string question, IReadOnlyList<ChatMessage> history, RetrievalOptions options, IChatClient? chat, CancellationToken ct) → Task<IReadOnlyList<string>>`. It runs Condense (added in Task 3; in this task history is always empty), then `options.QueryTransformers ?? RagOptions.QueryTransformers`, normalizing after each step. Each step runs inside a `rag.transform` span.
  - `RagPipeline.RetrieveAsync(string)`, `AskAsync(string)` and `AskStreamingAsync(string)` call `PlanAsync(question, [], …)` and pass the result to the retriever. The prompt still uses the original question.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task MultiQuery_adds_alternatives_and_searches_all_of_them()
{
    // chat.Replies: "zulu yankee xray\nsomething else"; RagOptions.QueryTransformers = [new MultiQueryTransformer(2)]
    // ingest d1 "alpha beta", d2 "zulu yankee xray"; RetrieveAsync("alpha beta", TopK 2) returns d1 and d2;
    // chat.Requests[0] system text contains "Write 2 different ways".
}

[Fact]
public async Task MultiQuery_parses_messy_model_output()
{
    // Review Focus 2: reply "1. alpha one\n\n- \"alpha two\"\n* alpha one\n2) ALPHA BETA\nalpha three\nalpha four", count 3,
    // input query "alpha beta" → planner output exactly ["alpha beta", "alpha one", "alpha two", "alpha three"].
}

[Fact]
public async Task HyDE_searches_with_the_generated_passage_instead_of_the_question()
{
    // chat reply "zulu yankee xray"; transformers [new HydeQueryTransformer()]; RetrieveAsync("what is it?") → d2 first.
}

[Fact]
public async Task Transformers_chain_in_order() =>
    // [MultiQuery(1), HyDE] → 1 MultiQuery call + 2 HyDE calls (one per query); planner output has 2 passages.
    ...;

[Fact]
public async Task Per_call_override_replaces_configured_transformers()
{
    // RagOptions has MultiQuery; RetrievalOptions.QueryTransformers = [] → no chat call at all.
}

[Fact]
public async Task Empty_transformer_output_keeps_the_previous_queries() =>
    // a custom transformer returning ["  ", ""] → the search still runs with the original question.
    ...;

[Fact]
public async Task Query_list_is_capped_at_10() =>
    // a custom transformer returning 15 distinct queries → the planner returns exactly 10.
    ...;

[Fact]
public async Task Built_in_transformer_without_chat_client_throws() =>
    // pipeline without UseChatClient, MultiQuery configured → InvalidOperationException containing "needs a chat client".
    ...;

[Fact]
public void MultiQuery_count_out_of_range_throws() =>
    Assert.Throws<ArgumentOutOfRangeException>(() => new MultiQueryTransformer(0)); // and 11

[Fact]
public async Task Ask_prompt_uses_the_original_question()
{
    // MultiQuery configured; AskAsync("alpha beta") → the answer request's last user message contains "Question: alpha beta"
    // and none of the alternatives.
}

[Fact]
public async Task Transform_span_is_emitted_without_query_text_by_default() =>
    // ActivityListener on "RagDotNet": one rag.transform activity with tag rag.transformer = "MultiQueryTransformer",
    // rag.query.count set, and no rag.queries tag.
    ...;
```

- [ ] **Step 2: Run the tests and confirm they fail.** Command: focused `-- --filter-class "*QueryTransformerTests"`. Expected: compile failure.

- [ ] **Step 3: Implement** the types, planner, normalization, prompts (exact copy in Decisions) and spans. Then wire `RagPipeline`. The MultiQuery parser does the following, in order:
  1. Split the reply into lines and trim each.
  2. Strip a leading `-`, `*`, `•`, or `\d+[.)]`, then trim again.
  3. Strip surrounding quotes.
  4. Drop empty lines and lines equal (ignoring case) to an existing query.
  5. Remove duplicates and take the first `count`.

- [ ] **Step 4: Run the full suite and confirm it passes.**

- [ ] **Step 5: Commit** with `git commit -m "feat(core): query transformers with MultiQuery and HyDE"`.

---

### Task 3: Conversations — Condense, conversation overloads, history in the prompt, docs

**Files:**
- Create: `src/RagDotNet.Core/Retrieval/CondenseQueryTransformer.cs`
- Modify: `src/RagDotNet.Core/Retrieval/QueryPlanner.cs`, `src/RagDotNet.Core/Pipeline/IRagPipeline.cs`, `RagPipeline.cs`, `src/RagDotNet.Core/Generation/RagPromptContext.cs`, `DefaultRagPromptBuilder.cs`, `src/RagDotNet.Core/Configuration/RagOptions.cs`, `RagOptionsValidator.cs`, `PublicAPI.Unshipped.txt`, `README.md`
- Test: `tests/RagDotNet.Core.Tests/ConversationTests.cs` (new); also extend `GenerationTests.cs` for the prompt builder

**Interfaces:**
- Consumes: `QueryPlanner.PlanAsync` and `QueryContext` (Task 2).
- Produces (public):
  - `IRagPipeline.RetrieveAsync(IList<ChatMessage> conversation, RetrievalOptions? options = null, CancellationToken cancellationToken = default)`
  - `IRagPipeline.AskAsync(IList<ChatMessage> conversation, AskOptions? options = null, CancellationToken cancellationToken = default)`
  - `IRagPipeline.AskStreamingAsync(IList<ChatMessage> conversation, AskOptions? options = null, CancellationToken cancellationToken = default)`
  - `RagOptions.MaxHistoryMessages` (`int`, default 10). A negative value fails validation with "MaxHistoryMessages must not be negative."
  - `RagPromptContext.History` (`IReadOnlyList<ChatMessage>`, an init property, default empty).
- How it works:
  - **Conversation validation:** the list must be non-null and non-empty, and its last message must have `Role == ChatRole.User` with non-whitespace `Text`. Otherwise it throws `ArgumentException("The conversation must end with a user message that has text.", "conversation")`. The question is the last message's `Text`.
  - **History:** the window from Decisions is applied over the messages before the question.
  - **No chat client:** when the windowed history is non-empty and there is no chat client, it throws `InvalidOperationException("A conversation with history needs a chat client to condense the question. Call UseChatClient().")`.
  - **Prompt:** `DefaultRagPromptBuilder` emits the system message, then each history message (a new `ChatMessage` with the same role and text only), then the existing user message with the documents, question and reminder.
  - **Unchanged:** the string overloads behave exactly as before (empty history).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Follow_up_question_is_condensed_before_retrieval()
{
    // chat.Replies: "How do I install quill on Linux?" (condense), then "Use apt [1]." (answer)
    // ingest install-guide doc; AskAsync([User "Tell me about quill", Assistant "It is a notebook app.", User "How do I install it on Linux?"])
    // → chat.Requests[0] system text starts with "Rewrite the user's last question"; its user text contains
    //   "User: Tell me about quill", "Assistant: It is a notebook app." and "Last question: How do I install it on Linux?";
    //   the answer request contains the two history messages before the documents message, and its last user message contains
    //   "Question: How do I install it on Linux?" (the original question, not the condensed one).
}

[Theory]
[InlineData("")]
[InlineData("   ")]
public async Task Empty_condense_reply_falls_back_to_the_original_question(string reply) => ...; // Review Focus 3

[Fact]
public async Task Quoted_condense_reply_is_unquoted() => ...; // reply "\"How do I install quill?\"" → planner query without quotes

[Theory]
[MemberData(nameof(BadConversations))] // empty list; assistant last; user last with whitespace text
public async Task Conversation_must_end_with_a_user_question(IList<ChatMessage> conversation) => ...; // Review Focus 1: ArgumentException

[Fact]
public async Task Retrieval_without_chat_client_works_for_a_single_message_and_needs_one_for_history()
{
    // Review Focus 5: no UseChatClient; RetrieveAsync([User "alpha"]) works; RetrieveAsync([User "x", Assistant "y", User "alpha"])
    // → InvalidOperationException containing "needs a chat client".
}

[Fact]
public async Task History_window_keeps_the_last_MaxHistoryMessages_user_and_assistant_texts()
{
    // MaxHistoryMessages 2; history [System, User u1, Assistant a1, Tool, User u2] + question → condense prompt and answer prompt
    // contain only "a1" and "u2" in order; no system or tool text.
}

[Fact]
public async Task Zero_history_window_skips_condense() => ...; // MaxHistoryMessages 0 → no condense call; question used as is

[Fact]
public async Task Streaming_conversation_overload_streams_with_condensed_retrieval() => ...;

[Fact]
public async Task Condense_runs_before_configured_transformers()
{
    // MultiQuery(1) configured; conversation with history → chat.Requests[0] is condense; Requests[1] is MultiQuery, and its
    // user text equals the condensed question.
}

[Fact]
public void Negative_MaxHistoryMessages_fails_validation() => ...;
```

- [ ] **Step 2: Run the tests and confirm they fail.** Command: focused `-- --filter-class "*ConversationTests"`. Expected: compile failure.

- [ ] **Step 3: Implement** Condense (exact prompt copy, quote stripping, empty fallback), the windowing, the planner's Condense step (in its own `rag.transform` span), the three overloads, prompt history and the validation.

- [ ] **Step 4: Update `README.md`** with a "Conversations and query rewriting" section. It covers:
  - passing the whole conversation to `AskAsync`, `AskStreamingAsync` or `RetrieveAsync`;
  - Condense running automatically when there is history, and why (follow-up questions);
  - that the original question and history go into the answer prompt;
  - `MaxHistoryMessages`, and that history isn't counted in `MaxContextTokens`;
  - opting into `MultiQueryTransformer` and `HydeQueryTransformer` (each adds chat calls per question), and the per-call override;
  - writing a custom `IQueryTransformer`.

- [ ] **Step 5: Run the full suite and the build** (0 warnings), and confirm the console sample still builds.

- [ ] **Step 6: Commit** with `git commit -m "feat(core): conversation overloads with automatic Condense; history in the answer prompt"`.

---

## Self-review notes

- **Spec coverage:**
  - §5.7 table: Condense is Task 3; MultiQuery and HyDE are Task 2.
  - Chaining with Condense first and the override replacing only what follows it: Tasks 2 and 3.
  - Original question in the final prompt: Task 2 test and Task 3 test.
  - §5.5 step 1 (transformer chain) and step 3 (RRF merge, de-duplicated by key): Tasks 2 and 1.
  - `RetrievalOptions.QueryTransformers`: Task 2. The `IList<ChatMessage>` retrieve overload: Task 3.
  - §5.6 conversation overloads, including streaming: Task 3.
  - §5.13 `rag.transform`: Tasks 2 and 3.
  - §9.1 "query transformer chaining": Task 2.
- **Out of scope here:**
  - hybrid search and keyword RRF (stage 7), which reuses `RankFusion`;
  - reranking (stage 7);
  - evaluating MultiQuery and HyDE quality (stage 3 harness).
