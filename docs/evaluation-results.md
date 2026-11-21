# RagDotNet retrieval evaluation

- Embedder: ollama (nomic-embed-text)
- Chat model: none
- Set `base`: 8 documents, 40 questions, hash cb1f2ea1af73
- Set `long-sections`: 4 documents, 20 questions, hash b51efdaa25f2
- Date: 2026-11-07

## base

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
| fixed-size | 128 | 0 | vector | 75 | 0.475 | 0.675 | 0.775 | 0.600 |
| fixed-size | 128 | 0.1 | vector | 79 | 0.475 | 0.750 | 0.800 | 0.637 |
| fixed-size | 128 | 0.2 | vector | 84 | 0.550 | 0.750 | 0.800 | 0.668 |
| fixed-size | 256 | 0 | vector | 40 | 0.525 | 0.775 | 0.800 | 0.645 |
| fixed-size | 256 | 0.1 | vector | 41 | 0.550 | 0.775 | 0.850 | 0.681 |
| fixed-size | 256 | 0.2 | vector | 44 | 0.425 | 0.825 | 0.825 | 0.617 |
| fixed-size | 512 | 0 | vector | 21 | 0.600 | 0.750 | 0.850 | 0.701 |
| fixed-size | 512 | 0.1 | vector | 24 | 0.425 | 0.775 | 0.850 | 0.595 |
| fixed-size | 512 | 0.2 | vector | 24 | 0.650 | 0.750 | 0.800 | 0.722 |
| fixed-size | 1024 | 0 | vector | 13 | 0.650 | 0.775 | 0.900 | 0.755 |
| fixed-size | 1024 | 0.1 | vector | 13 | 0.600 | 0.800 | 0.900 | 0.732 |
| fixed-size | 1024 | 0.2 | vector | 13 | 0.500 | 0.775 | 0.850 | 0.674 |
| semantic | 128 | n/a | vector | 132 | 0.750 | 0.875 | 0.875 | 0.817 |
| semantic | 256 | n/a | vector | 126 | 0.750 | 0.875 | 0.875 | 0.822 |
| semantic | 512 | n/a | vector | 125 | 0.750 | 0.875 | 0.875 | 0.822 |
| semantic | 1024 | n/a | vector | 125 | 0.750 | 0.875 | 0.875 | 0.822 |

## long-sections

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 128 | 0 | vector | 86 | 0.700 | 0.850 | 0.900 | 0.784 |
| structure-aware | 128 | 0.1 | vector | 86 | 0.700 | 0.850 | 0.900 | 0.784 |
| structure-aware | 128 | 0.2 | vector | 89 | 0.600 | 0.850 | 0.850 | 0.732 |
| structure-aware | 256 | 0 | vector | 44 | 0.750 | 0.900 | 0.900 | 0.825 |
| structure-aware | 256 | 0.1 | vector | 47 | 0.800 | 0.850 | 0.900 | 0.835 |
| structure-aware | 256 | 0.2 | vector | 53 | 0.800 | 0.900 | 0.950 | 0.852 |
| structure-aware | 512 | 0 | vector | 25 | 0.600 | 0.950 | 0.950 | 0.750 |
| structure-aware | 512 | 0.1 | vector | 28 | 0.650 | 0.950 | 0.950 | 0.783 |
| structure-aware | 512 | 0.2 | vector | 28 | 0.750 | 0.950 | 0.950 | 0.833 |
| structure-aware | 1024 | 0 | vector | 16 | 0.700 | 0.950 | 0.950 | 0.814 |
| structure-aware | 1024 | 0.1 | vector | 16 | 0.650 | 0.950 | 0.950 | 0.789 |
| structure-aware | 1024 | 0.2 | vector | 16 | 0.600 | 0.950 | 0.950 | 0.763 |
| fixed-size | 128 | 0 | vector | 75 | 0.650 | 0.750 | 0.800 | 0.707 |
| fixed-size | 128 | 0.1 | vector | 79 | 0.700 | 0.850 | 0.900 | 0.782 |
| fixed-size | 128 | 0.2 | vector | 88 | 0.600 | 0.850 | 0.900 | 0.733 |
| fixed-size | 256 | 0 | vector | 40 | 0.600 | 0.800 | 0.900 | 0.717 |
| fixed-size | 256 | 0.1 | vector | 41 | 0.650 | 0.900 | 0.900 | 0.772 |
| fixed-size | 256 | 0.2 | vector | 43 | 0.850 | 0.850 | 0.950 | 0.881 |
| fixed-size | 512 | 0 | vector | 19 | 0.750 | 0.900 | 0.950 | 0.835 |
| fixed-size | 512 | 0.1 | vector | 20 | 0.700 | 0.950 | 0.950 | 0.833 |
| fixed-size | 512 | 0.2 | vector | 21 | 0.750 | 1.000 | 1.000 | 0.875 |
| fixed-size | 1024 | 0 | vector | 11 | 0.500 | 0.950 | 0.950 | 0.733 |
| fixed-size | 1024 | 0.1 | vector | 12 | 0.650 | 0.950 | 1.000 | 0.804 |
| fixed-size | 1024 | 0.2 | vector | 12 | 0.650 | 0.900 | 1.000 | 0.798 |
| semantic | 128 | n/a | vector | 87 | 0.650 | 0.800 | 0.850 | 0.751 |
| semantic | 256 | n/a | vector | 52 | 0.550 | 0.900 | 0.900 | 0.715 |
| semantic | 512 | n/a | vector | 35 | 0.650 | 0.900 | 0.900 | 0.772 |
| semantic | 1024 | n/a | vector | 30 | 0.650 | 0.850 | 0.900 | 0.757 |

## all

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 128 | 0 | vector | 205 | 0.767 | 0.867 | 0.900 | 0.826 |
| structure-aware | 128 | 0.1 | vector | 205 | 0.767 | 0.867 | 0.900 | 0.823 |
| structure-aware | 128 | 0.2 | vector | 208 | 0.733 | 0.850 | 0.883 | 0.800 |
| structure-aware | 256 | 0 | vector | 153 | 0.783 | 0.867 | 0.900 | 0.833 |
| structure-aware | 256 | 0.1 | vector | 156 | 0.800 | 0.850 | 0.900 | 0.836 |
| structure-aware | 256 | 0.2 | vector | 162 | 0.800 | 0.867 | 0.917 | 0.842 |
| structure-aware | 512 | 0 | vector | 133 | 0.733 | 0.883 | 0.917 | 0.808 |
| structure-aware | 512 | 0.1 | vector | 136 | 0.750 | 0.883 | 0.917 | 0.819 |
| structure-aware | 512 | 0.2 | vector | 136 | 0.783 | 0.883 | 0.917 | 0.835 |
| structure-aware | 1024 | 0 | vector | 124 | 0.767 | 0.883 | 0.917 | 0.829 |
| structure-aware | 1024 | 0.1 | vector | 124 | 0.750 | 0.883 | 0.917 | 0.821 |
| structure-aware | 1024 | 0.2 | vector | 124 | 0.733 | 0.883 | 0.917 | 0.812 |
| fixed-size | 128 | 0 | vector | 150 | 0.533 | 0.700 | 0.783 | 0.636 |
| fixed-size | 128 | 0.1 | vector | 158 | 0.550 | 0.783 | 0.833 | 0.685 |
| fixed-size | 128 | 0.2 | vector | 172 | 0.567 | 0.783 | 0.833 | 0.690 |
| fixed-size | 256 | 0 | vector | 80 | 0.550 | 0.783 | 0.833 | 0.669 |
| fixed-size | 256 | 0.1 | vector | 82 | 0.583 | 0.817 | 0.867 | 0.711 |
| fixed-size | 256 | 0.2 | vector | 87 | 0.567 | 0.833 | 0.867 | 0.705 |
| fixed-size | 512 | 0 | vector | 40 | 0.650 | 0.800 | 0.883 | 0.746 |
| fixed-size | 512 | 0.1 | vector | 44 | 0.517 | 0.833 | 0.883 | 0.675 |
| fixed-size | 512 | 0.2 | vector | 45 | 0.683 | 0.833 | 0.867 | 0.773 |
| fixed-size | 1024 | 0 | vector | 24 | 0.600 | 0.833 | 0.917 | 0.748 |
| fixed-size | 1024 | 0.1 | vector | 25 | 0.617 | 0.850 | 0.933 | 0.756 |
| fixed-size | 1024 | 0.2 | vector | 25 | 0.550 | 0.817 | 0.900 | 0.715 |
| semantic | 128 | n/a | vector | 219 | 0.717 | 0.850 | 0.867 | 0.795 |
| semantic | 256 | n/a | vector | 178 | 0.683 | 0.883 | 0.883 | 0.786 |
| semantic | 512 | n/a | vector | 160 | 0.717 | 0.883 | 0.883 | 0.805 |
| semantic | 1024 | n/a | vector | 155 | 0.717 | 0.867 | 0.883 | 0.800 |

## Format check (stage 5)

The 8 `base` documents rendered as PDF, DOCX and HTML and read back with the stage 5 readers, at the library defaults (structure-aware, 512 tokens, 0.1 overlap). Method and discussion: [evaluation.md](evaluation.md#format-check).

- Embedder: ollama (nomic-embed-text)
- Chat model: none
- Set `base`: 8 documents, 40 questions, hash cb1f2ea1af73
- Set `base` · pdf: 8 documents, 40 questions, hash f0a5e009f7e5
- Set `base` · docx: 8 documents, 40 questions, hash 589b9f114c4c
- Set `base` · html: 8 documents, 40 questions, hash 36ba9218060e
- Set `long-sections`: 4 documents, 20 questions, hash b51efdaa25f2
- Date: 2026-11-09

### base

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |

### base · pdf

- Unscorable questions: 0

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 108 | 0.825 | 0.850 | 0.900 | 0.849 |

### base · docx

- Unscorable questions: 0

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 108 | 0.825 | 0.875 | 0.900 | 0.855 |

### base · html

- Unscorable questions: 0

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 108 | 0.825 | 0.875 | 0.900 | 0.855 |

### long-sections

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 28 | 0.650 | 0.950 | 0.950 | 0.783 |

### all

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 136 | 0.750 | 0.883 | 0.917 | 0.819 |

## Scanned check (stage 6)

The 8 `base` documents as image-only PDFs (each page rendered from the stage 5 PDF at 200 DPI and stored as a JPEG, with no text layer), read with Tesseract 5.3 (`eng`, Ubuntu 24.04) through `RagDotNet.Ocr.Tesseract`, at the library defaults (structure-aware, 512 tokens, 0.1 overlap). Method and discussion: [evaluation.md](evaluation.md#scanned-check).

- Embedder: ollama (nomic-embed-text)
- Chat model: none
- Set `base`: 8 documents, 40 questions, hash cb1f2ea1af73
- Set `base` · scanned: 8 documents, 40 questions, hash bb1339c1bbd2
- Date: 2026-11-12

### base

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 108 | 0.800 | 0.850 | 0.900 | 0.836 |

### base · scanned

- Unscorable questions: 1
- Evidence intact after OCR: 39 of 40

| Chunker | MaxTokens | Overlap | Mode | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | 24 | 0.600 | 0.800 | 0.850 | 0.710 |

## Hybrid check (stage 7)

Vector, hybrid and reranked retrieval at the library defaults (structure-aware, 512 tokens, 0.1 overlap), with `LlmReranker` over five chat models, one run per model. Method and discussion: [evaluation.md](evaluation.md#hybrid-check).

- Embedder: ollama (nomic-embed-text)
- Reranker chat models: llama3.2:3b, qwen2.5:7b, qwen2.5:14b, qwen2.5-coder:14b, deepseek-r1:14b
- Set `base`: 8 documents, 48 questions, hash ca2b85ecf304
- Set `base` · scanned: 8 documents, 48 questions, hash bc590c9657ac
- Set `long-sections`: 4 documents, 20 questions, hash b51efdaa25f2
- Date: 2026-11-20 and 2026-11-21
- Where: Ollama 0.40.16 on a Kaggle notebook with two NVIDIA T4 GPUs, where every chat model had a 32,768-token context window. The harness ran on the same machine, with Tesseract 5.5.1, except for `qwen2.5:7b`: its harness ran on the 16 GB development machine with Tesseract 5.3 and reached Ollama through a tunnel.
- Code: `61a822c` for `llama3.2:3b`; `92f53ea` for the others, before `LlmRerankerOptions.MaxOutputTokens` existed. The non-reasoning models reply in a few dozen tokens, so that limit should not change their results; `deepseek-r1:14b` thinks first and needs it raised (see [retrieval.md](retrieval.md#llmreranker)).
- Not run: `deepseek-r1:14b` on the scanned set, to save time (its Markdown run took 89 minutes).

The `vector` and `hybrid` rows were the same in every run made on Kaggle; the five reranked rows below come from the five runs. The `qwen2.5:7b` run read the scanned PDFs with Tesseract 5.3, which gave hybrid 0.667 / 0.854 / 0.896 / 0.772 on `base` · scanned instead of the row shown; its other `vector` and `hybrid` rows matched.

The earlier `llama3.2:3b` result on `base` (MRR 0.273, 2026-11-17) was measured with 10 passages per chat call on a 4,096-token window, which cut the prompt; it is replaced by the row below.

### base

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 108 | 0.771 | 0.813 | 0.875 | 0.806 |
| structure-aware | 512 | 0.1 | vector | hybrid | 108 | 0.708 | 0.792 | 0.854 | 0.773 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 108 | 0.229 | 0.438 | 0.667 | 0.404 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 108 | 0.500 | 0.854 | 0.917 | 0.690 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 108 | 0.792 | 1.000 | 1.000 | 0.885 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 108 | 0.771 | 0.958 | 1.000 | 0.867 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `deepseek-r1:14b` | 108 | 0.792 | 1.000 | 1.000 | 0.892 |

#### base · exact-term (8 questions)

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 108 | 0.625 | 0.625 | 0.750 | 0.656 |
| structure-aware | 512 | 0.1 | vector | hybrid | 108 | 0.625 | 0.750 | 0.750 | 0.705 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 108 | 0.375 | 0.375 | 0.500 | 0.475 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 108 | 0.625 | 0.875 | 0.875 | 0.750 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 108 | 0.750 | 1.000 | 1.000 | 0.875 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 108 | 0.875 | 1.000 | 1.000 | 0.938 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `deepseek-r1:14b` | 108 | 0.625 | 1.000 | 1.000 | 0.813 |

### base · scanned

- Unscorable questions: 1
- Evidence intact after OCR: 47 of 48

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 24 | 0.604 | 0.813 | 0.875 | 0.722 |
| structure-aware | 512 | 0.1 | vector | hybrid | 24 | 0.646 | 0.854 | 0.896 | 0.762 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 24 | 0.646 | 0.854 | 0.896 | 0.762 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 24 | 0.708 | 0.896 | 0.938 | 0.810 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 24 | 0.896 | 0.958 | 0.958 | 0.923 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 24 | 0.854 | 0.958 | 0.958 | 0.905 |

#### base · scanned · exact-term (8 questions)

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 24 | 0.625 | 0.875 | 1.000 | 0.781 |
| structure-aware | 512 | 0.1 | vector | hybrid | 24 | 0.875 | 1.000 | 1.000 | 0.938 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 24 | 0.875 | 1.000 | 1.000 | 0.938 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 24 | 0.750 | 1.000 | 1.000 | 0.875 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 24 | 1.000 | 1.000 | 1.000 | 1.000 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 24 | 1.000 | 1.000 | 1.000 | 1.000 |

### long-sections

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 28 | 0.650 | 0.950 | 0.950 | 0.783 |
| structure-aware | 512 | 0.1 | vector | hybrid | 28 | 0.700 | 0.900 | 1.000 | 0.823 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 28 | 0.700 | 0.900 | 1.000 | 0.823 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 28 | 0.650 | 0.950 | 1.000 | 0.796 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 28 | 0.850 | 1.000 | 1.000 | 0.917 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 28 | 0.800 | 1.000 | 1.000 | 0.883 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `deepseek-r1:14b` | 28 | 0.850 | 1.000 | 1.000 | 0.925 |

### all

| Chunker | MaxTokens | Overlap | Mode | Retrieval | Chunks | R@1 | R@3 | R@5 | MRR |
|---|---:|---:|---|---|---:|---:|---:|---:|---:|
| structure-aware | 512 | 0.1 | vector | vector | 136 | 0.735 | 0.853 | 0.897 | 0.800 |
| structure-aware | 512 | 0.1 | vector | hybrid | 136 | 0.706 | 0.824 | 0.897 | 0.788 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `llama3.2:3b` | 136 | 0.368 | 0.574 | 0.765 | 0.527 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:7b` | 136 | 0.544 | 0.882 | 0.941 | 0.721 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5:14b` | 136 | 0.809 | 1.000 | 1.000 | 0.895 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `qwen2.5-coder:14b` | 136 | 0.779 | 0.971 | 1.000 | 0.872 |
| structure-aware | 512 | 0.1 | vector | hybrid-rerank, `deepseek-r1:14b` | 136 | 0.809 | 1.000 | 1.000 | 0.902 |
