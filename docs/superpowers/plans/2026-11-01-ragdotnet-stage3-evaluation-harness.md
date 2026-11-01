# RagDotNet Stage 3 — Evaluation Harness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A repeatable harness that measures how well RagDotNet retrieval finds the passage answering each question.
- It reports recall@1/3/5 and MRR for each chunker, chunk size, overlap and retrieval mode.
- It runs on fake embeddings in CI and with Ollama for real numbers.
- The real-mode results go into `docs/evaluation.md`.

**Architecture:**
- **Harness:** a console app, `tests/RagDotNet.Evaluation`, not packed. It does the following:
  - loads a fixed Markdown corpus and a labelled question set;
  - validates them;
  - for each configuration, builds a fresh in-memory pipeline, ingests the corpus, and runs every question through the public `RetrieveAsync`;
  - scores each result by whether a retrieved chunk contains the question's evidence text;
  - writes a Markdown report.
- **Unit and smoke tests:** a separate test project, `tests/RagDotNet.Evaluation.Tests`, covers metrics, relevance and validation, and runs the whole harness in fake mode.

**Tech Stack:** .NET 10 (`net10.0` only; these projects are not shipped), RagDotNet.Core, CommunityToolkit.VectorData.InMemory 1.0.1, OllamaSharp 5.5.0 (real mode), xUnit v3.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`, revision 2.1. This plan covers §9.3 and §10 stage 3. Repository layout is §7: `tests/RagDotNet.Evaluation/ # recall@k harness (not packed)`. Local models are §3: `nomic-embed-text` (768 dimensions) and `llama3.2:3b` via Ollama.

## Global Constraints

- Harness and test projects target `net10.0` only, are `IsPackable=false`, and add no package that isn't already in `Directory.Packages.props`.
- Warnings are errors.
- Unit tests and CI never need a model, network access or Docker. Real mode runs only when explicitly requested from the command line.
- The harness uses only RagDotNet's public API, except that it may reference Core's `MarkdownReader`, which is public.
- Fake mode is deterministic: two runs over the same inputs produce byte-identical reports.
- Corpus and question content is original (fictional product docs). No copied third-party text.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. An evidence string that appears in more than one place in the corpus, or nowhere, is rejected by validation. The error names the question id and how many matches it found. It never silently gets an inflated or impossible score. Test: Task 1.
2. A question for which no relevant chunk is retrieved gets no rank and a reciprocal rank of 0. The report still renders, with no exception and no NaN. Test: Task 1.
3. Two fake-mode runs over the same inputs produce identical reports: no timestamps, no unordered dictionary output. Test: Task 2.
4. Real mode with Ollama not running fails fast with a clear message and exit code 2. It never hangs, and never prints a stack trace as its only output. Test: Task 4, against a closed localhost port.
5. If the corpus is edited so a question's evidence disappears, the harness refuses to run and names the question. Test: Task 3, in the dataset validation test over the real corpus.

---

## Decisions this plan makes *(builder's calls; spec §9.3 and §12 leave them open)*

**Labels and relevance**
- **Question labels:** each question has `{ "id", "question", "documentId", "evidence" }`.
  - `evidence` is a short verbatim fragment of the answering passage, 20–80 characters.
  - It must occur exactly once in the whole corpus, compared after normalization against the text `MarkdownReader` produces, not the raw Markdown.
- **Relevance:** a retrieved chunk is relevant when its `DocumentId` equals the label's `documentId` and its normalized text contains the normalized evidence.
  - Normalization: replace `*`, `_` and backtick characters with nothing, collapse whitespace runs to one space, trim, and lower-case (invariant).
  - Labelling by evidence text, not chunk index, keeps labels valid at every chunk size. Short evidence keeps splits across chunks rare, and `docs/evaluation.md` states this limitation.

**Metrics**
- **Retrieval depth:** each question retrieves `TopK = 10`.
  - `rank` is the 1-based position of the first relevant chunk, or none.
  - `recall@k` is (number of questions with rank ≤ k) / N, for k = 1, 3, 5.
  - `MRR` is the mean of 1/rank, counting 0 when there is no rank.
  - Values print with 3 decimals, using invariant culture.

**Configuration matrix**
- **Default matrix:** chunker `structure-aware` × MaxTokens {128, 256, 512, 1024} × overlap {0, 0.1, 0.2} × mode {`vector`}.
- **Opt-in modes:** `multi-query` (`MultiQueryTransformer(3)`) and `hyde` (`HydeQueryTransformer`), enabled with `--modes`.
- **Chunkers in this stage:** only `structure-aware` exists today. The chunker list is a dimension of the matrix, so stage 4 adds `fixed-size` and `semantic` without changing the harness.
- **Isolation:** each (chunker, MaxTokens, overlap) gets a fresh `InMemoryVectorStore` and is ingested once. All modes for that configuration share the ingestion.

**Providers**
- **Fake mode** (the default):
  - **Embeddings:** a hashed bag-of-words embedder with 256 dimensions, lower-cased word tokens and L2-normalized. It is defined in the harness itself.
  - **Chat** (for `multi-query` and `hyde`): a deterministic echo client that replies with the user message text.
  - **Model id:** `fake-embed`.
- **Real mode:**
  - `--embedder ollama` uses `OllamaApiClient(url, embedModel)` for embeddings and `OllamaApiClient(url, chatModel)` for chat.
  - Defaults: URL `http://localhost:11434`, embed model `nomic-embed-text`, 768 dimensions, chat model `llama3.2:3b`.
  - Reachability check: before ingesting, the harness embeds the text `"ping"` with a 10-second timeout. Any failure prints `Ollama is not reachable at {url}: {message}` to stderr and exits with code 2.

**Command line**
- **Options:** `--embedder fake|ollama`, `--ollama-url`, `--embed-model`, `--chat-model`, `--dimensions`, `--modes vector,multi-query,hyde`, `--max-tokens 128,256,512,1024`, `--overlap 0,0.1,0.2`, `--output <path>`.
- **Output:** the report always goes to stdout, and is also written to the file when `--output` is given.
- **Exit codes:** 0 on success, 1 on dataset validation failure or bad arguments (message on stderr), 2 when the provider is unreachable.

**Report format**
- Title: `# RagDotNet retrieval evaluation`.
- A metadata list containing:
  - embedder and model id;
  - chat model, or `none`;
  - corpus document count;
  - question count;
  - dataset hash: the first 12 hex chars of SHA-256 over the corpus files and `questions.json` in ordinal path order.
- The date line appears only in real mode.
- A table with the header `| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |`, with rows in matrix order (chunker, MaxTokens, overlap, mode).
- In fake mode, a closing line: `Scores from fake embeddings only check that the harness works; they are not meaningful.`

**Dataset**
- **Location:** `tests/RagDotNet.Evaluation/Dataset/corpus/*.md` and `tests/RagDotNet.Evaluation/Dataset/questions.json`, copied to output.
- **Corpus:** 8 documents of 600–1500 words each, for a fictional note-taking app called "Quill". Topics: install guide, configuration reference (with a settings table), sync and storage, troubleshooting FAQ, keyboard shortcuts (table), plugin API (with code blocks), security and privacy, release notes. Each document has nested headings.
- **Questions:** 40 in total.
  - About half reuse the document's wording; about half are paraphrased with few shared keywords.
  - At least 4 are answered from tables and 3 from code blocks.
  - Each document gets at least 3.
- **DocumentId:** the file name without extension, for example `install-guide`.

## File map

| File | Responsibility | Task |
|---|---|---|
| `tests/RagDotNet.Evaluation/RagDotNet.Evaluation.csproj` | console app, net10.0, references Core, InMemory and OllamaSharp; copies Dataset to output | 1 |
| `tests/RagDotNet.Evaluation/Dataset/Question.cs`, `DatasetLoader.cs` | load and validate corpus and questions | 1 |
| `tests/RagDotNet.Evaluation/Scoring/TextNormalizer.cs`, `Relevance.cs`, `Metrics.cs` | normalization, relevance, recall@k and MRR | 1 |
| `tests/RagDotNet.Evaluation/Providers/FakeEmbeddingGenerator.cs`, `EchoChatClient.cs` | fake providers | 2 |
| `tests/RagDotNet.Evaluation/Running/EvaluationConfig.cs`, `EvaluationRunner.cs`, `ReportWriter.cs` | the matrix, running and the Markdown report | 2 |
| `tests/RagDotNet.Evaluation/Program.cs`, `Cli/Options.cs` | argument parsing and exit codes | 2, 4 |
| `tests/RagDotNet.Evaluation/Providers/OllamaProviders.cs` | real mode and the reachability check | 4 |
| `tests/RagDotNet.Evaluation/Dataset/corpus/*.md`, `questions.json` | the dataset | 3 |
| `tests/RagDotNet.Evaluation.Tests/` | unit tests and the fake-mode smoke test | 1–4 |
| `RagDotNet.slnx` | add both projects under `/tests/` | 1 |
| `docs/evaluation.md`, `README.md` | method, how to run, baseline results | 4 |

---

### Task 1: Project, dataset loading and validation, relevance and metrics

**Files:**
- Create: the Evaluation project, `Dataset/Question.cs`, `Dataset/DatasetLoader.cs`, `Scoring/*.cs`, the Evaluation.Tests project (net10.0, xUnit v3, referencing the Evaluation project), and `tests/RagDotNet.Evaluation.Tests/ScoringTests.cs` and `DatasetTests.cs`
- Modify: `RagDotNet.slnx`

**Interfaces (produced):**
- `public sealed record Question(string Id, string Text, string DocumentId, string Evidence)`. JSON property names are `id`, `question`, `documentId` and `evidence`.
- `public sealed record CorpusDocument(string DocumentId, string FilePath, string NormalizedText)`, where the normalized text is built from the `MarkdownReader` blocks joined with a single space.
- `public sealed record Dataset(IReadOnlyList<CorpusDocument> Documents, IReadOnlyList<Question> Questions, string Hash)`
- `public static class DatasetLoader`, with `Task<Dataset> LoadAsync(string datasetDirectory, CancellationToken ct)`. It throws `DatasetValidationException`, a public exception carrying `IReadOnlyList<string> Errors`, which lists every problem rather than only the first. The rules:
  - ids are unique and non-empty;
  - `documentId` names an existing document;
  - evidence is 20–80 characters after normalization;
  - evidence occurs exactly once across all documents, and in the labelled document. Error format: `"{id}: evidence found {n} times"` or `"{id}: evidence not found in {documentId}"`.
- `public static class TextNormalizer`, with `string Normalize(string text)` following the rule in Decisions.
- `public static class Relevance`, with `bool IsRelevant(RetrievedChunk chunk, Question q)`.
- `public static class Metrics`:
  - `int? RankOfFirstRelevant(IReadOnlyList<RetrievedChunk> results, Question q)`
  - `public sealed record ScoreSummary(double RecallAt1, double RecallAt3, double RecallAt5, double Mrr)`
  - `ScoreSummary Summarize(IReadOnlyList<int?> ranks)`, which throws `ArgumentException` for an empty list.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Normalize_strips_markup_collapses_whitespace_and_lowercases() =>
    Assert.Equal("run quill init now", TextNormalizer.Normalize("  Run `quill   init`\n**now** "));

[Fact] public void Summarize_computes_recall_and_mrr()
{
    var s = Metrics.Summarize([1, 3, null, 6]);
    Assert.Equal(0.25, s.RecallAt1); Assert.Equal(0.5, s.RecallAt3); Assert.Equal(0.5, s.RecallAt5);
    Assert.Equal((1 + 1.0 / 3 + 0 + 1.0 / 6) / 4, s.Mrr, 10);
}

[Fact] public void No_relevant_result_has_no_rank_and_zero_reciprocal() // Review Focus 2
{
    Assert.Null(Metrics.RankOfFirstRelevant([Chunk("doc-a", "unrelated text")], Q("doc-a", "the answer is here")));
    Assert.Equal(0, Metrics.Summarize([null]).Mrr);
}

[Fact] public void Relevance_needs_same_document_and_contained_evidence() => ...;
// same text in another document → false; evidence split by markup/whitespace differences → true

[Fact] public async Task Duplicate_or_missing_evidence_is_rejected_with_question_ids() // Review Focus 1
{
    // temp dataset dir: doc a contains "alpha beta gamma delta" twice; questions q1 (that evidence), q2 (evidence absent),
    // q3 (unknown documentId), q4 (evidence 10 chars), q1 duplicated id → DatasetValidationException whose Errors contain
    // "q1: evidence found 2 times", "q2: evidence not found in a", a q3 unknown-document message, a q4 length message,
    // and a duplicate-id message.
}

[Fact] public async Task Valid_dataset_loads_with_stable_hash() => ...; // load twice → same Hash, 12 hex chars
```

- [ ] **Step 2: Run the tests and confirm they fail.** Command: `dotnet test --project tests/RagDotNet.Evaluation.Tests`. Expected: compile failure.

- [ ] **Step 3: Implement** the types above. `DatasetLoader` reads each `corpus/*.md` file (in ordinal path order) through `MarkdownReader`. The project copies `Dataset/**` to the output directory. Add both projects to `RagDotNet.slnx` under `/tests/`.

- [ ] **Step 4: Run the tests and confirm they pass; run `dotnet build` (0 warnings).**

- [ ] **Step 5: Commit** with `git commit -m "feat(eval): evaluation project with dataset validation, relevance and metrics"`.

---

### Task 2: Runner, configuration matrix, fake providers, report and CLI (fake mode)

**Files:**
- Create: `Providers/FakeEmbeddingGenerator.cs`, `Providers/EchoChatClient.cs`, `Running/EvaluationConfig.cs`, `Running/EvaluationRunner.cs`, `Running/ReportWriter.cs`, `Cli/Options.cs`, `Program.cs`
- Test: `tests/RagDotNet.Evaluation.Tests/RunnerTests.cs`, `CliTests.cs`

**Interfaces:**
- Consumes: Task 1's `Dataset`, `Metrics`, `Relevance`.
- Produces:
  - `public sealed record EvaluationConfig(string Chunker, int MaxTokens, double Overlap, string Mode)`
  - `public static IReadOnlyList<EvaluationConfig> EvaluationConfig.Matrix(IReadOnlyList<string> chunkers, IReadOnlyList<int> maxTokens, IReadOnlyList<double> overlaps, IReadOnlyList<string> modes)`, in nested order chunker → MaxTokens → overlap → mode.
  - `public sealed record ConfigResult(EvaluationConfig Config, int ChunkCount, ScoreSummary Scores, IReadOnlyList<int?> Ranks)`
  - `public sealed class EvaluationRunner(IEmbeddingGenerator<string, Embedding<float>> embedder, int dimensions, IChatClient? chat)`, with `Task<IReadOnlyList<ConfigResult>> RunAsync(Dataset dataset, IReadOnlyList<EvaluationConfig> configs, CancellationToken ct)`.
    - Per (chunker, MaxTokens, overlap) it builds a new `RagPipelineBuilder` with: a new `InMemoryVectorStore`; collection `eval`; `Chunking = new() { Chunker, MaxTokens, OverlapRatio }`; `EmbeddingModelId` from the embedder.
    - It ingests every document with `DocumentId` set to the file stem.
    - `ChunkCount` is the sum of `IngestResult.ChunkCount`.
    - Then, per mode, it retrieves each question with `TopK = 10` and `QueryTransformers` set to `[]`, `[new MultiQueryTransformer(3)]` or `[new HydeQueryTransformer()]`. Modes other than `vector` need a non-null chat client, or it throws `InvalidOperationException`.
  - `public static class ReportWriter`, with `string Write(ReportMetadata meta, Dataset dataset, IReadOnlyList<ConfigResult> results)`. The exact format is in Decisions. `ReportMetadata(string Embedder, string EmbedModel, string ChatModel, DateOnly? Date, bool Fake)`.
  - `Program.Main(string[] args) → Task<int>`. Parsing lives in `Options.Parse(string[] args)`, which returns `Options` or throws `OptionsException`.
    - `--embedder ollama` returns exit code 1 with "not available until Task 4" only in this task. Task 4 implements it.
    - The dataset directory is `AppContext.BaseDirectory/Dataset`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Matrix_is_in_nested_order() =>
    // Matrix(["structure-aware"], [128, 256], [0, 0.1], ["vector"]) → (128,0), (128,0.1), (256,0), (256,0.1)
    ...;

[Fact] public async Task Runner_scores_a_small_dataset_with_fake_embeddings()
{
    // temp dataset (2 docs, 3 questions whose wording overlaps their evidence) → one ConfigResult per config,
    // ChunkCount > 0, every rank null or in 1..10, scores in [0,1], and at least one question ranked 1.
}

[Fact] public async Task Fake_mode_report_is_byte_identical_across_runs() // Review Focus 3
{
    // run Program.Main(["--max-tokens", "128,256", "--overlap", "0"]) twice capturing stdout → identical strings;
    // the report contains the header row, 2 data rows, the fake-scores note, and no date line.
}

[Fact] public async Task Chat_modes_run_in_fake_mode_with_the_echo_client() =>
    // --modes vector,multi-query,hyde --max-tokens 256 --overlap 0 → 3 rows; exit code 0
    ...;

[Theory]
[InlineData("--max-tokens", "abc")]
[InlineData("--modes", "bogus")]
[InlineData("--unknown", "x")]
public async Task Bad_arguments_exit_1_with_a_message(string flag, string value) => ...;

[Fact] public async Task Output_option_writes_the_report_file() => ...; // --output temp path → file content equals stdout report
```

- [ ] **Step 2: Run the tests and confirm they fail** (compile failure).

- [ ] **Step 3: Implement** the providers, runner, report writer, CLI and exit codes as specified. The fake embedder: tokens are `[a-z0-9]+` from the lower-cased text. Each token goes to the bucket given by an FNV-1a hash mod dimensions, adding +1, and the vector is then L2-normalized. The zero vector stays zero.

- [ ] **Step 4: Run the tests and confirm they pass; build with 0 warnings.**

- [ ] **Step 5: Commit** with `git commit -m "feat(eval): runner, configuration matrix, Markdown report and CLI in fake mode"`.

---

### Task 3: The evaluation corpus and question set

**Files:**
- Create: `tests/RagDotNet.Evaluation/Dataset/corpus/{install-guide,configuration,sync-and-storage,troubleshooting,keyboard-shortcuts,plugin-api,security-and-privacy,release-notes}.md` and `tests/RagDotNet.Evaluation/Dataset/questions.json`
- Test: `tests/RagDotNet.Evaluation.Tests/DatasetTests.cs` (add a real-dataset test)

**Interfaces:**
- Consumes: `DatasetLoader` (Task 1) and the runner (Task 2).
- Produces: the dataset described in Decisions. It is original fictional content about "Quill", consistent across documents (the same product facts everywhere).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Shipped_dataset_is_valid_and_balanced() // Review Focus 5
{
    var d = await DatasetLoader.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Dataset"), Ct);
    Assert.Equal(8, d.Documents.Count);
    Assert.InRange(d.Questions.Count, 40, 40);
    Assert.All(d.Documents, doc => Assert.True(d.Questions.Count(q => q.DocumentId == doc.DocumentId) >= 3));
    // each corpus file has 600–1500 words (count \w+ tokens of the raw file)
}

[Fact] public async Task Shipped_dataset_runs_end_to_end_in_fake_mode() =>
    // Program.Main(["--max-tokens", "256", "--overlap", "0"]) → exit 0, one row, R@5 > 0
    ...;
```

- [ ] **Step 2: Run the tests and confirm they fail** (the dataset files are missing).

- [ ] **Step 3: Write the corpus and questions** to the Decisions rules: nested headings, one settings table in `configuration`, one shortcuts table, at least 2 code blocks in `plugin-api`, 20–80 character evidence occurring exactly once, roughly half paraphrased questions, at least 4 answered from tables and 3 from code blocks. Fix whatever validation reports until the loader accepts the dataset.

- [ ] **Step 4: Run the tests and confirm they pass.**

- [ ] **Step 5: Commit** with `git commit -m "feat(eval): Quill evaluation corpus and 40 labelled questions"`.

---

### Task 4: Real mode with Ollama, docs, and the baseline run

**Files:**
- Create: `tests/RagDotNet.Evaluation/Providers/OllamaProviders.cs`, `docs/evaluation.md`
- Modify: `Program.cs`, `Cli/Options.cs`, `README.md`
- Test: `tests/RagDotNet.Evaluation.Tests/CliTests.cs`

**Interfaces:**
- Consumes: the CLI and runner (Task 2).
- Produces:
  - `--embedder ollama` builds `OllamaApiClient` embedder and chat clients from the options (Decisions defaults), runs the reachability check, and on success runs the matrix. The report date is today's UTC date.
  - The chat client is created only when a selected mode needs it. The report's chat model is otherwise `none`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public async Task Unreachable_ollama_fails_fast_with_exit_code_2() // Review Focus 4
{
    // pick a free localhost port (bind a TcpListener to port 0, read the port, stop it);
    // Program.Main(["--embedder", "ollama", "--ollama-url", $"http://127.0.0.1:{port}"]) completes within 15 s,
    // returns 2, and stderr contains "Ollama is not reachable at".
}
```

- [ ] **Step 2: Run the test and confirm it fails.**

- [ ] **Step 3: Implement** real mode and the reachability check.

- [ ] **Step 4: Write `docs/evaluation.md`.** It covers:
  - **Purpose:** the harness sets StructureAware's MaxTokens and overlap defaults in stage 4.
  - **The dataset:** 8 fictional Quill documents and 40 questions.
  - **The metric definitions.**
  - **The relevance rule and its limitation:** evidence split across chunks counts as a miss.
  - **How to run:** fake mode `dotnet run --project tests/RagDotNet.Evaluation`; real mode `dotnet run --project tests/RagDotNet.Evaluation -- --embedder ollama --output docs/evaluation-results.md`, with optional `--modes vector,multi-query,hyde`.
  - **A "Baseline results" section** containing the real-mode report.

  Add a short "Evaluation" pointer to `README.md`.

- [ ] **Step 5: Run the real-mode baseline.** Check Ollama with `ollama list`.
  - **If it's reachable:** run the default matrix (vector mode) with `--output docs/evaluation-results.md`, paste the table into `docs/evaluation.md` under "Baseline results", and commit both files.
  - **If it isn't reachable:** do not start or install anything. Leave the section saying "Not yet run", and report it as a concern so the controller can ask the owner.

- [ ] **Step 6: Run the full test suites** (`tests/RagDotNet.Core.Tests` and `tests/RagDotNet.Evaluation.Tests`) and build with 0 warnings.

- [ ] **Step 7: Commit** with `git commit -m "feat(eval): Ollama real mode, evaluation docs and baseline results"`.

---

## Self-review notes

- **Spec §9.3 coverage:**

  | Requirement | Task |
  |---|---|
  | Fixed corpus | 3 |
  | 30–50 labelled questions (40) | 3 |
  | Recall@1/3/5 and MRR | 1 |
  | Per chunker, size and mode (matrix; overlap added) | 2 |
  | CI mode on fake embeddings, smoke tests in `Evaluation.Tests` | 2, 3 |
  | Real mode with Ollama | 4 |
  | Results in `docs/evaluation.md` | 4 |

- **Out of scope here:** tuning the StructureAware defaults (stage 4 uses these results), the fixed-size and semantic chunkers (stage 4; the matrix already accepts more chunker names), hybrid mode (stage 7), and wiring the smoke test into CI (stage 10).
