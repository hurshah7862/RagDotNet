# Retrieval evaluation

`tests/RagDotNet.Evaluation` is a console harness that measures how well RagDotNet's retrieval finds the passage answering a question. Stage 4 uses it to choose the StructureAware chunker's default `MaxTokens` and overlap, and to compare the fixed-size and semantic chunkers once they exist.

The harness uses only RagDotNet's public API. For each configuration it builds a fresh in-memory pipeline, ingests the corpus, runs every question through `RetrieveAsync` with `TopK = 10`, and scores the results.

## The dataset

The dataset lives in `tests/RagDotNet.Evaluation/Dataset`:

- `corpus/*.md`: 8 original documents (600–1500 words each) for a fictional note-taking app called Quill. They cover install, configuration, sync and storage, troubleshooting, keyboard shortcuts, the plugin API, security and privacy, and release notes. Each document's id is its file name without the extension, for example `install-guide`.
- `questions.json`: 40 questions, 5 per document. About half reuse the document's wording and half are paraphrased with few shared keywords. 6 are answered from tables and 4 from code blocks.

Each question is labelled with its document and an `evidence` string: a short verbatim fragment (20–80 characters) of the answering passage.

### Validation

The harness refuses to run, with exit code 1, if the dataset is inconsistent. Every problem is listed with the question id:

- question ids must be unique;
- `documentId` must name a corpus document;
- evidence must be 20–80 characters after normalization;
- evidence must occur exactly once in the whole corpus, and in the labelled document (`q7: evidence found 2 times`, `q7: evidence not found in install-guide`);
- evidence must lie inside one paragraph, list, table or code block, because a chunk can end at any block boundary;
- evidence must not be in a heading, because chunk text does not include headings;
- no entry in `questions.json` may be `null`.

Matching uses the text `MarkdownReader` produces, not the raw Markdown. Editing a document so that a question's evidence disappears, or appears twice, therefore fails the run instead of quietly changing the scores.

## Relevance and metrics

A retrieved chunk is **relevant** when its `DocumentId` equals the question's document and its normalized text contains the normalized evidence. Normalization removes `*`, `_` and backticks, collapses whitespace to single spaces, trims, and lower-cases.

Labelling by evidence text rather than by chunk index keeps the labels valid at every chunk size.

For each question, **rank** is the 1-based position of the first relevant chunk among the 10 retrieved, or none.

- **R@k** (recall at k): the share of questions with rank ≤ k, for k = 1, 3 and 5.
- **MRR** (mean reciprocal rank): the mean of 1/rank, counting 0 for questions with no rank.

### Limitations

- **Split evidence counts as a miss.** If a chunk boundary falls inside the evidence fragment, no single chunk contains it and the question scores as a miss, even though the answer was retrieved in two pieces. At the default sizes (128 and up) every shipped question's evidence fits in one chunk. Below 64 tokens splits become common, about a quarter of the questions at 16 tokens, so scores there understate retrieval.
- **Short sections.** The StructureAware chunker never crosses a heading, and most sections in this corpus are shorter than 256 tokens. Chunk counts and scores therefore change little above that size. A corpus with longer sections would separate the larger sizes more.
- **Small set.** With 40 questions, one question is 0.025 of any recall figure, so treat differences smaller than about 0.05 as noise.

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
| `--max-tokens` | `128,256,512,1024` |
| `--overlap` | `0,0.1,0.2` |
| `--dataset` | `Dataset` next to the harness |
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

Each report records the dataset hash, which is the first 12 hex characters of SHA-256 over the corpus files and `questions.json`. Results with different hashes are not comparable.

## Baseline results

Run on 2026-11-06 with Ollama (`nomic-embed-text`), vector mode, default matrix. The full report is in [evaluation-results.md](evaluation-results.md).

- Embedder: ollama (nomic-embed-text)
- Chat model: none
- Corpus documents: 8
- Questions: 40
- Dataset hash: cb1f2ea1af73
- Date: 2026-11-06

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 128 | 0 | vector | 119 | 0.800 | 0.875 | 0.900 | 0.847 |
| structure-aware | 128 | 0.1 | vector | 119 | 0.800 | 0.875 | 0.900 | 0.843 |
| structure-aware | 128 | 0.2 | vector | 119 | 0.800 | 0.850 | 0.900 | 0.834 |
| structure-aware | 256 | 0 | vector | 109 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 256 | 0.1 | vector | 109 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 256 | 0.2 | vector | 109 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 512 | 0 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 512 | 0.1 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 512 | 0.2 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 1024 | 0 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 1024 | 0.1 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 1024 | 0.2 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |

What this shows:

- Real embeddings rank the answering chunk first for 32 of 40 questions and within the top 5 for 36.
- Chunk size and overlap make almost no difference on this corpus. Above 256 tokens the chunks are identical, because sections are shorter than the limit; at 128 tokens a few sections split, and R@3 rises by one question. That gap is within noise (see Limitations).
- Because the sizes barely differ here, this corpus alone cannot pick StructureAware's defaults. Stage 4 should add documents with longer sections, or more questions, before choosing them from data.

