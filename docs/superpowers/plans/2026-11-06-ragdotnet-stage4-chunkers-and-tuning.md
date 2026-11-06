# RagDotNet Stage 4 — Fixed-Size and Semantic Chunkers, Defaults Tuned — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the two remaining built-in chunkers, measure all three with the evaluation harness on a corpus that can tell chunk sizes apart, and set StructureAware's default `MaxTokens` and overlap from the measurements.

**Architecture:**
- **Core:** two new built-in chunkers.
  - `FixedSizeChunker`: a sliding token window over the flattened body text.
  - `SemanticChunker`: sentence embeddings, breaking where the distance between neighbouring sentences exceeds a percentile threshold.
  - Both are registered beside `StructureAwareChunker` in the existing `ChunkerRegistry`, and selected through `ChunkingOptions.Chunker` like any chunker.
- **Harness:** the dataset becomes named *sets*.
  - The stage 3 dataset moves unchanged into the set `base`, so its hash stays `cb1f2ea1af73`.
  - A new set, `long-sections`, adds documents whose sections are far longer than 512 tokens.
  - Each set is ingested and scored on its own. The report shows one table per set, plus an `all` table pooling every question.
- **Tuning:** a real-mode run over the full matrix, then a fixed decision rule (below) that either changes `ChunkingOptions`' defaults or records why they stay.

**Tech Stack:** .NET (Core: `net8.0;net10.0`; harness and tests: `net10.0`), Microsoft.ML.Tokenizers 2.0.1, Microsoft.Extensions.AI abstractions, xUnit v3, OllamaSharp 5.5.0 (real mode only).

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`, revision 2.1. This plan covers §10 stage 4, §5.4 (chunking) and §9.3 (harness).
- §5.4 table: FixedSize is a "sliding token window over the flattened text", defaults MaxTokens 512, Overlap 64.
- §5.4 table: Semantic "splits into sentences, embeds them, and breaks where the cosine distance between neighbours exceeds a percentile threshold; respects MaxTokens. **Makes embedding calls during ingest.**" Default: breakpoint percentile 95.
- §5.4: "StructureAware stays the default strategy. The evaluation harness (see 9.3) tunes only its default settings (MaxTokens, overlap) before v1 ships. Results for the other chunkers are documented so apps can choose."
- The owner's stage 4 decision (2026-11-06): add about 4 long-section documents and about 20 questions, keep the existing 8 documents and 40 questions unchanged, and report the sets separately and combined.

## Global Constraints

- Core targets `net8.0;net10.0`; the harness and test projects target `net10.0` only. No new packages.
- Warnings are errors. New public Core API is listed in `src/RagDotNet.Core/PublicAPI.Unshipped.txt`, with XML docs.
- Built-in chunker names are `structure-aware`, `fixed-size` and `semantic`, as constants on `Chunkers` (§5.4).
- The library's output guarantees still apply to the new chunkers (§5.4): blank chunks dropped, indexes renumbered, oversized chunks split by tokens. The new chunkers rely on `ChunkOutputGuard` for these rather than repeating them.
- Unit tests and CI never need a model, network access or Docker. Fake mode stays byte-deterministic.
- The `base` set's files stay byte-identical (`git mv` only), and its hash stays `cb1f2ea1af73`.
- Corpus content is original fictional Quill documentation.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A document with no body text (only headings, or empty) gives zero chunks from FixedSize and Semantic, and Semantic makes no embedding call. Test: Tasks 1 and 2.
2. Semantic on a section of one sentence, or a document whose neighbour distances are all equal, gives no NaN threshold and no exception; it groups by MaxTokens alone. Test: Task 2.
3. An app that already registered a custom chunker named `fixed-size` or `semantic` gets the existing "registered more than once" startup failure, not a silent override. Test: Task 1.
4. A document id or question id that appears in two dataset sets is rejected with both set names, instead of silently mixing their scores. Test: Task 3.
5. `--chunkers semantic --overlap 0.1,0.2` runs semantic once per MaxTokens, labelled `n/a`, rather than producing duplicate rows that look like an overlap measurement. Test: Task 3.

---

## Decisions this plan makes *(builder's calls; the spec leaves them open)*

**Shared options**
- **Overlap for FixedSize** is `ChunkingOptions.OverlapRatio`, the same option StructureAware uses: `overlapTokens = floor(MaxTokens × OverlapRatio)`.
  - §5.4's "Overlap 64" is a per-strategy default, but options are shared across chunkers, and adding a per-chunker overlap field is API this stage doesn't need.
  - At the library default (512, 0.10), FixedSize overlaps 51 tokens, not 64. `docs/chunking.md` says `OverlapRatio = 0.125` gives the spec's 64.
  - Cost if wrong: one new option later.
- **`ChunkingOptions.BreakpointPercentile`**: a `double` with default `95`, valid in `(0, 100]`. Only Semantic reads it.
  - It is part of the chunking hash for every chunker. Every stored document therefore re-chunks once on its next ingest, which is acceptable before v1.
- **Semantic ignores `OverlapRatio`.** Breaking at topic changes makes overlap mostly redundant. The docs say so.

**FixedSize**
- **Text:** the text of all non-heading blocks, joined with `"\n\n"`. Headings are left out, as in StructureAware, because they reach the embedding through `HeadingPath`.
- **Windows:** `stride = MaxTokens − overlapTokens`. Windows start at token 0, `stride`, `2×stride`, …. The last window ends at the final token. No window is emitted that lies entirely inside the previous one.
- **Chunk text:** sliced from the original text using the token offsets (`Tokenizer.EncodeToTokens(text, out _, considerPreTokenization: true, considerNormalization: false)`, `EncodedToken.Offset`), then trimmed.
- **Metadata:** `HeadingPath` comes from the block holding the window's first character. `PageStart` and `PageEnd` are the minimum and maximum `PageNumber` of the blocks the window touches, or null.
- **Version:** `"1"`.

**Semantic**
- **Units:** each Paragraph and List block is split into sentences with StructureAware's sentence regex, moved into a shared `internal static class SentenceSplitter`. A Table or Code block is one unit.
- **Embedding:** every unit is embedded, its own text only, in batches of `RagOptions.EmbeddingBatchSize`, passed in through the constructor. There is one `GenerateAsync` call per batch, and none when there are no units.
- **Distances:** `dᵢ = 1 − cos(eᵢ, eᵢ₊₁)` is taken only between neighbours in the same section (the same `HeadingPath`). A section change is always a break.
  - The threshold is the `BreakpointPercentile`-th percentile of all of the document's within-section distances, with linear interpolation between the two nearest ranks (NumPy's default `linear` method).
  - The chunker breaks after unit i when dᵢ > threshold. With no distances it makes no semantic breaks.
  - A zero vector has cosine 0, so distance 1.
- **Grouping:** between breaks, units are added greedily. A chunk is closed before the unit that would take it over `MaxTokens`. A unit larger than `MaxTokens` on its own goes through unchanged, and `ChunkOutputGuard` splits it.
- **Joining:** sentences from the same block join with `" "`, and different blocks join with `"\n\n"`. `HeadingPath` is the section's; pages are as for FixedSize.
- **Version:** `"1"`.

**Harness**
- **Dataset sets:** a dataset root either contains `corpus/` itself, which makes one set named after the directory, or contains subdirectories that each have `corpus/`; those are sets, in ordinal name order.
  - The shipped root is `Dataset/` with sets `base` (the stage 3 files, moved) and `long-sections`.
  - `DatasetLoader.LoadAsync` (one set) is unchanged.
- **Cross-set rules:** document ids and question ids must be unique across sets. Errors from one set are prefixed `"{set}: "`.
- **Isolation:** each (set, chunker, MaxTokens, overlap) gets a fresh store. Questions retrieve only within their own set, so the `base` numbers stay directly comparable with the stage 3 baseline.
- **Matrix:** chunkers default to `structure-aware,fixed-size,semantic` (`--chunkers`). Semantic runs once per MaxTokens, with OverlapRatio 0, whatever `--overlap` says, and its Overlap cell prints `n/a`.
- **Report:** the title is unchanged, followed by these metadata lines:
  - `- Embedder: …`
  - `- Chat model: …`
  - one line per set: ``- Set `base`: 8 documents, 40 questions, hash cb1f2ea1af73``
  - `- Date: …` in real mode only.

  Then a section `## {set}` per set and, when there are two or more sets, a section `## all`. Each section holds the stage 3 table, with the same header and row order.
  - In `all`, Chunks is the sum over sets, and the scores come from `Metrics.Summarize` over the concatenated ranks.
  - The `- Dataset hash:`, `- Corpus documents:` and `- Questions:` lines are replaced by the set lines.

**Tuning rule** (applied in Task 5, on the `all` section, vector mode, structure-aware rows only)
- **Baseline B:** the current default, MaxTokens 512 with overlap 0.1.
- **Candidate C:** the row with the highest MRR. Ties go to the higher R@5, then to the MaxTokens closest to 512, then to the lower overlap.
- **Change the defaults to C only if** MRR(C) − MRR(B) ≥ 0.03 **and** R@5(C) ≥ R@5(B). Otherwise keep 512 / 0.10 and record "no measurable gain" with both rows.
  - With 60 questions, 0.03 MRR is more than one question moving from a miss to rank 1. A smaller gap is within noise.
- The rule is fixed before the run. Task 5 applies it as written and does not adjust it after seeing the numbers.

## File map

| File | Responsibility | Task |
|---|---|---|
| `src/RagDotNet.Core/Chunking/FixedSizeChunker.cs` | sliding token window | 1 |
| `src/RagDotNet.Core/Chunking/Chunkers.cs` | add the `FixedSize` and `Semantic` constants | 1, 2 |
| `src/RagDotNet.Core/Chunking/ChunkerRegistry.cs`, `Configuration/RagEngineFactory.cs`, `Configuration/RagOptionsValidator.cs` | register the built-ins; reserve their names | 1, 2 |
| `src/RagDotNet.Core/Chunking/SemanticChunker.cs`, `SentenceSplitter.cs` | the semantic chunker and the shared sentence regex | 2 |
| `src/RagDotNet.Core/Chunking/ChunkingOptions.cs`, `Storage/Hashing.cs` | `BreakpointPercentile` and its hash | 2 |
| `src/RagDotNet.Core/PublicAPI.Unshipped.txt` | new public API | 1, 2 |
| `tests/RagDotNet.Core.Tests/FixedSizeChunkerTests.cs`, `SemanticChunkerTests.cs` | chunker tests | 1, 2 |
| `tests/RagDotNet.Evaluation/Dataset/*`, `Running/*`, `Cli/Options.cs` | sets, per-set running, report sections, `--chunkers` | 3 |
| `tests/RagDotNet.Evaluation/Dataset/base/**` (moved), `Dataset/long-sections/**` (new) | the dataset | 3, 4 |
| `tests/RagDotNet.Evaluation.Tests/*` | harness tests | 3, 4 |
| `docs/chunking.md` (new), `docs/evaluation.md`, `docs/evaluation-results.md`, `README.md` | chunker guide, results, tuning record | 5 |

---

### Task 1: FixedSize chunker, registered as a built-in

**Files:**
- Create: `src/RagDotNet.Core/Chunking/FixedSizeChunker.cs`, `tests/RagDotNet.Core.Tests/FixedSizeChunkerTests.cs`
- Modify: `Chunkers.cs`, `ChunkerRegistry.cs`, `RagEngineFactory.cs`, `RagOptionsValidator.cs`, `PublicAPI.Unshipped.txt`

**Interfaces:**
- Produces:
  - `public const string Chunkers.FixedSize = "fixed-size"`.
  - `public sealed class FixedSizeChunker(Tokenizer tokenizer) : IChunker`, with `Name => Chunkers.FixedSize` and `Version => "1"`.
  - `ChunkerRegistry(IEnumerable<IChunker> appChunkers, IEnumerable<IChunker> builtIns)`.
  - `RagOptionsValidator`'s reserved names include every built-in name.
- Consumes: `TokenCounter`, `ChunkOutputGuard` (unchanged).

- [ ] **Step 1: Write the failing tests** in `FixedSizeChunkerTests`. Use the default cl100k tokenizer and `TestRag` from `Fakes` for the pipeline tests.
  - `Windows_have_at_most_MaxTokens_and_overlap_by_the_ratio`: about 300 tokens of numbered words (`"w0 w1 … w299"`), with `MaxTokens = 100` and `OverlapRatio = 0.2`.
    - Every chunk has ≤ 100 tokens.
    - The last 20 tokens of chunk n equal the first 20 of chunk n + 1.
    - The concatenation with overlaps removed equals the original word sequence.
    - The last chunk ends with `w299`.
  - `Zero_overlap_tiles_the_text_exactly`: with `OverlapRatio = 0`, joining the chunks with `" "` gives the original text with whitespace collapsed.
  - `Headings_are_left_out_and_set_the_heading_path`: in Markdown `# A\n\npara one.\n\n## B\n\npara two.`, with MaxTokens large, there is one chunk.
    - Its text contains neither `"# A"` nor `"B\n"`.
    - Its `HeadingPath` is `["A"]`, from the first block.
  - `Document_without_body_text_gives_no_chunks` (Review Focus 1): headings only gives an empty list.
  - `Selected_by_name_through_the_pipeline`: ingest with `Chunking = new() { Chunker = Chunkers.FixedSize, MaxTokens = 32 }`. The result is `Created`, and `ChunkCount` equals what `FixedSizeChunker` returns for the same document.
  - `App_chunker_named_fixed_size_fails_validation` (Review Focus 3): `RagOptionsValidator.Validate` with an app chunker whose name is `"fixed-size"` fails with `"Chunker name 'fixed-size' is registered more than once."`.
  - `Invalid_options_throw`: `MaxTokens = 8` or `OverlapRatio = 0.5` throws `ArgumentOutOfRangeException`, as StructureAware does.

- [ ] **Step 2: Run them.** `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class "*FixedSizeChunkerTests"`. Expected: a compile failure (`FixedSizeChunker` missing), then FAIL for each test.

- [ ] **Step 3: Implement** `FixedSizeChunker` as set out under Decisions → FixedSize. Change `ChunkerRegistry` to take built-ins as a list. `RagEngineFactory` passes `[new StructureAwareChunker(tokenizer), new FixedSizeChunker(tokenizer)]`. The validator seeds `chunkerNames` from `Chunkers.StructureAware` and `Chunkers.FixedSize`. Add the public API lines.

- [ ] **Step 4: Run** `dotnet test --project tests/RagDotNet.Core.Tests`. Expected: all pass, on both net8.0 and net10.0.

- [ ] **Step 5: Commit** `feat(core): fixed-size chunker`.

---

### Task 2: Semantic chunker and `BreakpointPercentile`

**Files:**
- Create: `src/RagDotNet.Core/Chunking/SemanticChunker.cs`, `SentenceSplitter.cs`, `tests/RagDotNet.Core.Tests/SemanticChunkerTests.cs`
- Modify: `StructureAwareChunker.cs` (use `SentenceSplitter`), `Chunkers.cs`, `ChunkingOptions.cs`, `Storage/Hashing.cs`, `RagEngineFactory.cs`, `RagOptionsValidator.cs`, `PublicAPI.Unshipped.txt`

**Interfaces:**
- Produces:
  - `public const string Chunkers.Semantic = "semantic"`.
  - `public double ChunkingOptions.BreakpointPercentile { get; init; } = 95`.
  - `public sealed class SemanticChunker(Tokenizer tokenizer, IEmbeddingGenerator<string, Embedding<float>> embedder, int batchSize = 32) : IChunker`, with `Name => Chunkers.Semantic` and `Version => "1"`.
  - `internal static class SentenceSplitter { IReadOnlyList<string> Split(string text) }`.
- Consumes: `ChunkerRegistry` built-ins list (Task 1). `RagEngineFactory` already has `embedder` and `options.EmbeddingBatchSize`.

- [ ] **Step 1: Write the failing tests** in `SemanticChunkerTests`. Use a test embedder, `TopicEmbedder`, that maps text containing "cat" to `[1,0,0]`, "rocket" to `[0,1,0]`, and anything else to `[0,0,1]`, and counts its calls.
  - `Breaks_where_the_topic_changes`: the paragraph is four cat sentences then four rocket sentences, with MaxTokens 512 and the default percentile.
    - There are exactly 2 chunks.
    - The first contains only cat sentences, the second only rocket sentences.
  - `Respects_MaxTokens_within_a_topic`: 40 cat sentences with MaxTokens 64 give chunks of ≤ 64 tokens each, covering every sentence once, in order.
  - `Section_change_is_always_a_break`: the same topic under `## A` and `## B` gives ≥ 2 chunks, each with its own section's `HeadingPath`.
  - `Code_and_table_blocks_are_single_units`: a code block with two `.`-terminated lines is never split between them, given MaxTokens large enough.
  - `Single_sentence_and_uniform_distances_do_not_throw` (Review Focus 2): one sentence gives 1 chunk. Ten identical sentences give chunks grouped by MaxTokens only, and no NaN appears anywhere.
  - `No_body_text_makes_no_embedding_call` (Review Focus 1): headings only gives 0 chunks and `TopicEmbedder.Calls == 0`.
  - `Embeds_in_batches`: 70 units with `batchSize: 32` make 3 calls.
  - `Percentile_is_part_of_the_chunking_hash`: `Hashing.ChunkingHash` differs between percentile 95 and 90, all else equal.
  - `Invalid_percentile_throws`: values 0, −1, 101 and NaN throw `ArgumentOutOfRangeException`.
  - `App_chunker_named_semantic_fails_validation` (Review Focus 3), as in Task 1.

- [ ] **Step 2: Run them.** `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class "*SemanticChunkerTests"`. Expected: compile failure, then FAIL.

- [ ] **Step 3: Implement** as set out under Decisions → Semantic.
  - Move the sentence regex into `SentenceSplitter`. `StructureAwareChunkerTests` must stay green unchanged.
  - Append `|bp:{o.BreakpointPercentile:R}` to the chunking-hash input.
  - `RagEngineFactory` adds `new SemanticChunker(tokenizer, embedder, options.EmbeddingBatchSize)` to the built-ins, and the validator reserves its name.

- [ ] **Step 4: Run** `dotnet test --project tests/RagDotNet.Core.Tests`. Expected: all pass on both frameworks. Any test that pinned a chunking hash value is updated, and the reason is noted in the commit message.

- [ ] **Step 5: Commit** `feat(core): semantic chunker and BreakpointPercentile`.

---

### Task 3: Harness — dataset sets, per-set report, three chunkers

**Files:**
- Modify: `tests/RagDotNet.Evaluation/Dataset/DatasetLoader.cs`, `Question.cs`, `Running/EvaluationConfig.cs`, `EvaluationRunner.cs`, `ReportWriter.cs`, `Cli/Options.cs`, `Program.cs`, `RagDotNet.Evaluation.csproj` (copy `Dataset/**`), the existing tests whose expected metadata lines change.
- Move: `git mv tests/RagDotNet.Evaluation/Dataset/corpus tests/RagDotNet.Evaluation/Dataset/base/corpus`, and the same for `questions.json`.
- Test: `tests/RagDotNet.Evaluation.Tests/DatasetSetTests.cs`, plus additions to `CliTests.cs` and `RunnerTests.cs`.

**Interfaces:**
- Produces:
  - `public sealed record DatasetSet(string Name, Dataset Dataset)`.
  - `DatasetLoader.LoadSetsAsync(string root, CancellationToken ct) → Task<IReadOnlyList<DatasetSet>>`, which throws `DatasetValidationException` with every set's errors.
  - `ConfigResult` gains `string Set`.
  - `EvaluationRunner.RunAsync(IReadOnlyList<DatasetSet> sets, IReadOnlyList<EvaluationConfig> configs, CancellationToken ct)`.
  - `ReportWriter.Write(ReportMetadata meta, IReadOnlyList<DatasetSet> sets, IReadOnlyList<ConfigResult> results)`.
  - `Options.Chunkers` is set by `--chunkers`, which validates each name against the three `Chunkers` constants.
- Consumes: `Chunkers.FixedSize` and `Chunkers.Semantic` (Tasks 1–2).

- [ ] **Step 1: Write the failing tests.**
  - `Base_set_hash_is_unchanged_from_stage3`: loading the shipped `Dataset` root gives a `base` set with `Hash == "cb1f2ea1af73"`, 8 documents and 40 questions.
  - `Root_with_corpus_is_a_single_set_named_after_the_directory`: `SmallDataset()` gives one set whose name is the directory name.
  - `Duplicate_ids_across_sets_are_rejected_with_both_set_names` (Review Focus 4): sets `a` and `b` both contain document `doc` and question `q1`. The errors mention `doc`, `q1`, `a` and `b`.
  - `Errors_are_prefixed_with_the_set_name`: a bad evidence string in set `b` gives `"b: q1: evidence not found in doc-b"`.
  - `Semantic_runs_once_per_MaxTokens_and_shows_na` (Review Focus 5): `--chunkers semantic --max-tokens 128,256 --overlap 0.1,0.2` gives exactly 2 data rows, each with Overlap cell `n/a`.
  - `Report_has_a_section_per_set_and_all`: a two-set temp root gives headings `## a`, `## b` and `## all` in that order, one set line per set, and no `- Dataset hash:` line.
    - Within the `all` table, Chunks equals the sum of the two sets' Chunks for the same configuration.
  - `Single_set_report_has_no_all_section`.
  - Update `Fake_mode_report_is_byte_identical_across_runs` and `Report_lists_metadata_and_formats_scores_with_three_decimals` to the new metadata lines. Determinism is still asserted.

- [ ] **Step 2: Run** `dotnet test --project tests/RagDotNet.Evaluation.Tests`. Expected: the new tests FAIL; the moved dataset breaks `Shipped_*` tests until Step 3 points them at `Dataset/base`.

- [ ] **Step 3: Implement** as set out under Decisions → Harness. Point the shipped-dataset tests at `Dataset/base`. `Program` loads with `LoadSetsAsync`. Usage text and `docs/evaluation.md` gain `--chunkers`.

- [ ] **Step 4: Run** `dotnet test --project tests/RagDotNet.Evaluation.Tests`, then `dotnet run --project tests/RagDotNet.Evaluation` twice. Expected: all pass, and the two reports are byte-identical.

- [ ] **Step 5: Commit** `feat(eval): dataset sets, per-set report sections, all three chunkers`.

---

### Task 4: The `long-sections` set

**Files:**
- Create: `tests/RagDotNet.Evaluation/Dataset/long-sections/corpus/*.md` (4 documents) and `long-sections/questions.json`.
- Test: add to `DatasetTests.cs`.

**Content:**
- **Documents:** 4 new Quill documents, about topics the base set doesn't cover:
  - `migration-guide`: importing from other apps, mapping and cleanup.
  - `collaboration`: shared notebooks, roles, comments, history.
  - `templates-and-automation`: templates, variables, scheduled rules.
  - `performance-tuning`: indexing, cache, large vaults.
- **Sections:** each document has 2–3 `##` sections of 800–2000 words of body text each. They are long continuous prose with some lists, and at least one table or code block per document. Inside a section there are no `###` subheadings, so StructureAware has to split by size.
- **Questions:** 5 per document, 20 in total, with the base set's rules (half paraphrased, unique evidence, inside one non-heading block).
  - At least 2 are answered from tables and 2 from code.
  - At least 12 of the 20 have evidence in the second half of their section, by character position, so a chunk that only covers a section's opening misses them.
  - Question ids are prefixed `long-` and must be unique against the base set.

- [ ] **Step 1: Write the failing test** `Long_sections_set_is_valid_and_actually_long`.
  - The set loads, with 4 documents and 20 questions, 5 per document.
  - Every `##` section body has 800–2000 words, counted with `\w+` over the raw Markdown between headings.
  - At least 12 questions' evidence lies in the second half of its section.
  - At least 2 table and 2 code evidence questions, checked by block kind as in `Shipped_questions_cover_tables_and_code_blocks`.
  - In fake mode, structure-aware at 256 and at 1024 give different chunk counts on this set.

- [ ] **Step 2: Run it.** Expected: FAIL, because the set doesn't exist yet.

- [ ] **Step 3: Write the documents and questions.**

- [ ] **Step 4: Run** `dotnet test --project tests/RagDotNet.Evaluation.Tests`. Expected: all pass, and `Base_set_hash_is_unchanged_from_stage3` still passes.

- [ ] **Step 5: Commit** `feat(eval): long-sections dataset set`.

---

### Task 5: Real-mode run, tuning, and chunker docs

**Files:**
- Modify: `docs/evaluation.md`, `docs/evaluation-results.md`, `README.md`; possibly `src/RagDotNet.Core/Chunking/ChunkingOptions.cs` and the tests and docs that state its defaults.
- Create: `docs/chunking.md`.

- [ ] **Step 1: Run** `dotnet run --project tests/RagDotNet.Evaluation -- --embedder ollama --output docs/evaluation-results.md` (full default matrix, vector mode). If Ollama or its models are missing, stop and report; don't install or pull anything without asking.

- [ ] **Step 2: Apply the tuning rule** from Decisions to the `all` section, and write the outcome in `docs/evaluation.md` → "Default settings". Include B's row, C's row, the two differences, and the decision.
  - **If the rule says change:** first write a failing test, `Defaults_are_the_tuned_values`, in the Core tests, asserting the new `MaxTokens` and `OverlapRatio`. Then change `ChunkingOptions`, the XML docs, README and spec-referencing docs, and run the whole suite.
  - The commit message notes that stored documents re-chunk once on their next ingest.

- [ ] **Step 3: Write `docs/chunking.md`:**
  - the three chunkers, and when to pick each;
  - their settings and defaults, including the OverlapRatio note for FixedSize;
  - Semantic's ingest cost: one embedding per sentence;
  - the measured results per chunker from the `all` and `long-sections` sections, with the dataset hashes.

  Link it from README.

- [ ] **Step 4: Run** `dotnet test` from the repo root. Expected: all pass. Check every project ran: no "Zero tests ran", and no "blocked" in the output.

- [ ] **Step 5: Commit** `docs: stage 4 evaluation results and chunker guide` (plus `feat(core): tune StructureAware defaults` if Step 2 changed them).

---

## Self-review notes

- **Spec coverage:**
  - §5.4 FixedSize → Task 1.
  - Semantic, including its embedding cost and percentile 95 → Task 2.
  - Chunker names on `Chunkers` → Tasks 1 and 2.
  - Output guarantees → reused through `ChunkOutputGuard`.
  - "Results for the other chunkers are documented" → Task 5, `docs/chunking.md`.
  - §9.3 "for each chunker, chunk size, and retrieval mode" → Task 3.
  - Real-mode results set StructureAware's defaults → Task 5, by a rule fixed in advance.
- **Deviation recorded:** FixedSize's spec default overlap of 64 tokens is expressed through the shared `OverlapRatio`. The cost is in Decisions.
- **Type consistency:**
  - `DatasetSet`, `LoadSetsAsync`, `ConfigResult.Set` and the `ReportWriter.Write` signature are defined in Task 3 and used only there and in Task 4.
  - The `ChunkerRegistry` built-ins list is defined in Task 1 and extended in Task 2.
