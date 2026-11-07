# Retrieval evaluation

`tests/RagDotNet.Evaluation` is a console harness that measures how well RagDotNet's retrieval finds the passage answering a question. It compares the three built-in chunkers and was used to choose the StructureAware chunker's default `MaxTokens` and overlap (see [Default settings](#default-settings)).

The harness uses only RagDotNet's public API. For each dataset set and configuration it builds a fresh in-memory pipeline, ingests the set's documents, runs each of the set's questions through `RetrieveAsync` with `TopK = 10`, and scores the results.

## The dataset

The dataset lives in `tests/RagDotNet.Evaluation/Dataset`, as two **sets**. Each set is a folder with its own `corpus/*.md` and `questions.json`, and is ingested and scored on its own, so a question only competes with the documents of its own set. All documents are original, fictional documentation for a note-taking app called Quill. Each document's id is its file name without the extension, for example `install-guide`.

| Set | Documents | Questions | What it is for |
|---|---|---|---|
| `base` | 8, 600–1500 words each, many short sections | 40, 5 per document; 6 from tables, 4 from code | The stage 3 set, unchanged, so its results stay comparable with the stage 3 baseline (hash `cb1f2ea1af73`) |
| `long-sections` | 4, each with 2 sections of 800–2000 words and no subheadings | 20, 5 per document; 4 from tables, 3 from code; at least 12 answered from the second half of a section | Separating chunk sizes: sections far longer than 512 tokens, so the size decides how they are cut |

The base documents cover install, configuration, sync and storage, troubleshooting, keyboard shortcuts, the plugin API, security and privacy, and release notes. The long-section documents cover migrating from other apps, collaboration, templates and automation, and performance tuning. In both sets about half the questions reuse the document's wording and half are paraphrased with few shared keywords.

Each question is labelled with its document and an `evidence` string: a short verbatim fragment (20–80 characters) of the answering passage.

`--dataset` accepts either one set folder or a root folder of set folders.

### Validation

The harness refuses to run, with exit code 1, if the dataset is inconsistent. Every problem is listed with the question id:

- question ids must be unique;
- `documentId` must name a corpus document;
- evidence must be 20–80 characters after normalization;
- evidence must occur exactly once in the whole corpus, and in the labelled document (`q7: evidence found 2 times`, `q7: evidence not found in install-guide`);
- evidence must lie inside one paragraph, list, table or code block, because a chunk can end at any block boundary;
- evidence must not be in a heading, because chunk text does not include headings;
- no entry in `questions.json` may be `null`;
- document ids and question ids must be unique across sets (`question id 'q1' appears in sets a and b`);
- a root folder may hold either `corpus/` or set folders, not both, which catches a stale build output from the older single-set layout.

Errors from a set are prefixed with its name, for example `long-sections: long-col-2: evidence not found in collaboration`.

Matching uses the text `MarkdownReader` produces, not the raw Markdown. Editing a document so that a question's evidence disappears, or appears twice, therefore fails the run instead of quietly changing the scores.

## Relevance and metrics

A retrieved chunk is **relevant** when its `DocumentId` equals the question's document and its normalized text contains the normalized evidence. Normalization removes `*`, `_` and backticks, collapses whitespace to single spaces, trims, and lower-cases.

Labelling by evidence text rather than by chunk index keeps the labels valid at every chunk size.

For each question, **rank** is the 1-based position of the first relevant chunk among the 10 retrieved, or none.

- **R@k** (recall at k): the share of questions with rank ≤ k, for k = 1, 3 and 5.
- **MRR** (mean reciprocal rank): the mean of 1/rank, counting 0 for questions with no rank.

### Limitations

- **Split evidence counts as a miss.** If a chunk boundary falls inside the evidence fragment, no single chunk contains it and the question scores as a miss, even though the answer was retrieved in two pieces. At the default sizes (128 and up) every shipped question's evidence fits in one chunk. Below 64 tokens splits become common, about a quarter of the questions at 16 tokens, so scores there understate retrieval.
- **Short sections in `base`.** The StructureAware chunker never crosses a heading, and most `base` sections are shorter than 256 tokens, so on that set chunk counts and scores change little above that size. The `long-sections` set exists to separate the larger sizes.
- **Small sets.** With 60 questions in all, one question is about 0.017 of a recall figure; in `long-sections` alone, one of 20 questions is 0.05. Treat small differences as noise, especially within one set.
- **One embedding model.** Real-mode results come from `nomic-embed-text`. Another model may favour different sizes.

## How to run

Fake mode needs no model and is what the tests run. It uses a hashed bag-of-words embedder, and an echo chat client for the chat-based modes. Its scores only show that the harness works.

```
dotnet run --project tests/RagDotNet.Evaluation
```

Real mode uses Ollama (`nomic-embed-text`, 768 dimensions, and `llama3.2:3b` for chat):

```
dotnet run --project tests/RagDotNet.Evaluation -- --embedder ollama --output docs/evaluation-results.md
```

Add `--modes vector,multi-query,hyde` to include query rewriting, which makes one chat call per question per mode.

| Option | Default |
|---|---|
| `--embedder fake\|ollama` | `fake` |
| `--ollama-url` | `http://localhost:11434` |
| `--embed-model` / `--chat-model` | `nomic-embed-text` / `llama3.2:3b` |
| `--dimensions` | 256 (fake), 768 (ollama) |
| `--modes` | `vector` |
| `--chunkers` | `structure-aware,fixed-size,semantic` |
| `--max-tokens` | `128,256,512,1024` |
| `--overlap` | `0,0.1,0.2` (semantic ignores it and runs once per size, shown as `n/a`) |
| `--dataset` | `Dataset` next to the harness: a root of set directories, or one set |
| `--output` | none; the report always goes to stdout |

The harness exits with:

| Code | Meaning |
|---:|---|
| 0 | success |
| 1 | bad arguments or an invalid dataset |
| 2 | Ollama is not reachable, lacks a model, or fails during the run |
| 130 | cancelled with Ctrl+C |

Before ingesting anything, real mode checks Ollama with a 10-second timeout each time:

1. It asks Ollama for its pulled models. If Ollama doesn't answer, it stops with `Ollama is not reachable at <url>: <reason>`. If the embed model, or the chat model when a chat mode is selected, is missing, it stops with `Ollama at <url> does not have model '<name>'; run ollama pull <name>`. A name without a tag matches `:latest`.
2. It embeds `"ping"`, and stops with `Ollama embedding check failed for '<model>': <reason>` if that fails or the vector length differs from `--dimensions`.

A request that fails later in the run stops it with one line, `Ollama request failed during the run: <reason>`, not a stack trace.

Each report lists every set with its hash: the first 12 hex characters of SHA-256 over the set's corpus files and `questions.json`. Results for a set with a different hash are not comparable.

## Results

Run on 2026-11-07 with Ollama (`nomic-embed-text`), vector mode, the default matrix (all three chunkers, MaxTokens 128/256/512/1024, overlap 0/0.1/0.2). The full report, with every row for each set and for all questions pooled, is in [evaluation-results.md](evaluation-results.md). Set hashes: `base` `cb1f2ea1af73`, `long-sections` `b51efdaa25f2`.

The `base` rows are identical to the stage 3 baseline (2026-11-06), which confirms that moving the set and adding the new chunkers changed nothing for StructureAware.

Best row per chunker, on all 60 questions:

| Chunker | Best MaxTokens / overlap | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---|---:|---:|---:|---:|---:|
| structure-aware | 256 / 0.2 | 162 | 0.800 | 0.867 | 0.917 | 0.842 |
| structure-aware (current default) | 512 / 0.1 | 136 | 0.750 | 0.883 | 0.917 | 0.819 |
| semantic | 512 / n/a | 160 | 0.717 | 0.883 | 0.883 | 0.805 |
| fixed-size | 512 / 0.2 | 45 | 0.683 | 0.833 | 0.867 | 0.773 |

What this shows:

- **StructureAware is the strongest chunker overall**, and the most stable: its MRR on all questions stays between 0.800 and 0.842 across all 12 settings.
- **Fixed-size loses most on `base`.** It ignores headings, so with short sections it builds large chunks that mix several topics: 13 chunks for 8 documents at 1024 tokens. On `long-sections` it is competitive. Its best rows there (256 / 0.2 at 0.881, 512 / 0.2 at 0.875) are slightly above StructureAware's best (0.852), but that gap is about one question of 20, within noise.
- **Semantic is close to StructureAware but never ahead**, and it costs one embedding call per sentence at ingest. MaxTokens changes its results little, because most of its breaks come from topic shifts rather than the size limit.
- **On `long-sections`, size now matters**, as intended. StructureAware's chunk count falls from 86 at 128 tokens to 16 at 1024, and R@1 and R@3 move in opposite directions. Larger chunks get the right passage into the top 3 more often (0.950 against 0.850) but rank it first less often.

## Default settings

StructureAware's defaults were decided by a rule fixed in the stage 4 plan before this run. It is applied to the structure-aware rows of the pooled `all` table, in vector mode:

- Baseline B is the current default, MaxTokens 512 with overlap 0.10.
- Candidate C is the row with the highest MRR. Ties go to the higher R@5, then the MaxTokens closest to 512, then the lower overlap.
- The defaults change to C only if MRR(C) − MRR(B) ≥ 0.03 and R@5(C) ≥ R@5(B).

| | MaxTokens | Overlap | R@5 | MRR |
|---|---:|---:|---:|---:|
| B (current default) | 512 | 0.10 | 0.917 | 0.819 |
| C (best MRR) | 256 | 0.20 | 0.917 | 0.842 |
| Difference | | | 0.000 | +0.023 |

**Decision: the defaults stay at MaxTokens 512 and OverlapRatio 0.10.** C's R@5 is no lower than B's, but its MRR gain of 0.023 is below the 0.03 threshold. Over 60 questions that adds up to about 1.4 in summed reciprocal rank, roughly one or two questions ranking a place or two higher, which is too small to trust from a set this size. The four best structure-aware rows (256 / 0.2, 256 / 0.1, 512 / 0.2 and 256 / 0) are all within 0.01 MRR of each other, so there is no clear winner to move to either.

If more questions or another embedding model are added later, rerun the harness and apply the same rule.

