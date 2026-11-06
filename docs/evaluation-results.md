# RagDotNet retrieval evaluation

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
