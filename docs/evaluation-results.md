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
| semantic | 128 | n/a | vector | 88 | 0.650 | 0.800 | 0.850 | 0.751 |
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
| semantic | 128 | n/a | vector | 220 | 0.717 | 0.850 | 0.867 | 0.795 |
| semantic | 256 | n/a | vector | 178 | 0.683 | 0.883 | 0.883 | 0.786 |
| semantic | 512 | n/a | vector | 160 | 0.717 | 0.883 | 0.883 | 0.805 |
| semantic | 1024 | n/a | vector | 155 | 0.717 | 0.867 | 0.883 | 0.800 |
