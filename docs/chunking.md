# Choosing a chunker

RagDotNet ships three chunkers. All of them are selected through `ChunkingOptions.Chunker`, globally, per content type or per ingest call (see the README's Chunking section). This page explains what each one does, what its settings mean, and how they compared on the evaluation corpus.

## The three chunkers

| | `structure-aware` (default) | `fixed-size` | `semantic` |
|---|---|---|---|
| Constant | `Chunkers.StructureAware` | `Chunkers.FixedSize` | `Chunkers.Semantic` |
| How it splits | Along headings, then paragraphs and blocks, then sentences and lines; merges small blocks under the same heading | A sliding window of `MaxTokens` tokens over the body text | Embeds each sentence and breaks where neighbouring sentences drift apart; always breaks at a heading |
| Crosses headings | Never | Yes | Never |
| Uses `MaxTokens` | Yes | Yes, as the window size | Yes, as an upper limit |
| Uses `OverlapRatio` | Yes | Yes | No |
| Uses `BreakpointPercentile` | No | No | Yes |
| Extra cost at ingest | None | None | One embedding per sentence |

All three leave heading text out of the chunk text. The headings above a chunk are kept in its `HeadingPath`, which is added to the text that gets embedded and is shown in citations. Tables and code blocks are never split in the middle by StructureAware or Semantic unless a single block is larger than `MaxTokens`.

Whatever a chunker returns, the library drops blank chunks, splits chunks longer than `MaxTokens` by tokens, and renumbers chunk indexes.

## Settings

| Setting | Default | Valid range | Meaning |
|---|---:|---|---|
| `MaxTokens` | 512 | at least 16 | Largest chunk, in tokens of the configured tokenizer (`cl100k_base` by default) |
| `OverlapRatio` | 0.10 | [0, 0.5) | Share of `MaxTokens` repeated from the end of one chunk at the start of the next |
| `BreakpointPercentile` | 95 | (0, 100] | Semantic only: a new chunk starts where the distance between neighbouring sentences is above this percentile of all such distances in the document |

**Fixed-size overlap.** The design spec gives the fixed-size chunker a default overlap of 64 tokens. RagDotNet expresses overlap as one ratio shared by all chunkers, so at the default MaxTokens of 512 and OverlapRatio of 0.10, fixed-size overlaps by 51 tokens. Set `OverlapRatio = 0.125` to get the spec's 64.

**Semantic thresholds.** A higher `BreakpointPercentile` makes fewer breaks and larger chunks; a lower one makes more, smaller chunks. Because the threshold is a percentile of each document's own distances, it adapts to how varied a document is: a document that stays on one topic still gets a few breaks at its relatively biggest shifts. Distances are only compared within a section, and the size limit still applies.

**Changing settings re-chunks.** The chunker's name and version, `MaxTokens` and the tokenizer are part of each document's chunking hash. So is each chunker's own setting: `OverlapRatio` for structure-aware and fixed-size documents, and `BreakpointPercentile` for semantic ones. Changing any of them re-chunks a document the next time it is ingested.

**Semantic chunks and a new embedding model.** The semantic chunker's boundaries come from the embedding model, but the model is not part of the chunking hash. `IRagAdmin.ReindexAsync()` re-embeds stored chunks with the new model without re-chunking them, so semantic documents keep the boundaries the old model chose until their content changes. To get boundaries chosen by the new model, delete those documents and ingest them again after the reindex.

**Dimensions.** The semantic chunker checks that every sentence vector has the configured `EmbeddingDimensions`, as ingest does for chunk vectors, and fails the ingest with `InvalidOperationException` if not.

## How they compared

These results come from the evaluation harness on 2026-11-07, with `nomic-embed-text` embeddings, vector search and the top 10 results. The corpus has 60 questions in two sets: `base`, with many short sections, and `long-sections`, with sections far longer than 512 tokens. MRR is the mean of 1/rank of the first chunk containing the answer. [evaluation.md](evaluation.md) explains the method and its limits, and [evaluation-results.md](evaluation-results.md) has every row.

MRR by chunker and size, with overlap 0.1 for structure-aware and fixed-size:

| MaxTokens | structure-aware, all | fixed-size, all | semantic, all | structure-aware, long-sections | fixed-size, long-sections | semantic, long-sections |
|---:|---:|---:|---:|---:|---:|---:|
| 128 | 0.823 | 0.685 | 0.795 | 0.784 | 0.782 | 0.751 |
| 256 | 0.836 | 0.711 | 0.786 | 0.835 | 0.772 | 0.715 |
| 512 | 0.819 | 0.675 | 0.805 | 0.783 | 0.833 | 0.772 |
| 1024 | 0.821 | 0.756 | 0.800 | 0.789 | 0.804 | 0.757 |

With 60 questions, differences under about 0.03 MRR are noise, and with the 20 `long-sections` questions alone, under about 0.05.

## Which one to use

- **Structured documents (Markdown, documentation, anything with headings): keep `structure-aware`.** It scored best overall at every size, and it was the most stable across settings. Its defaults (512 tokens, 0.10 overlap) were checked against the alternatives and kept; see [Default settings](evaluation.md#default-settings).
- **Long unstructured text (transcripts, plain text, extracted text without headings): `fixed-size` is a reasonable choice.** It was competitive on long sections but fell well behind wherever documents had many short sections, because it merges them across headings into mixed chunks. Overlap of 0.1 to 0.2 helped it clearly at 128 and 256 tokens; above that the effect was mixed.
- **`semantic` is rarely worth its cost on this kind of content.** It came close to structure-aware without beating it. It also embeds every sentence at ingest, on top of embedding the chunks themselves, so ingest makes many times more embedding calls. It may help with long text that changes topic without headings, but measure it on your own documents before relying on it.

These results come from one embedding model and one small, fictional corpus. If your documents look different, the harness can be pointed at your own dataset with `--dataset`.
