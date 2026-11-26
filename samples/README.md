# Samples

Small apps that show how to use RagDotNet, one task each. Run them from the repository root.

| Sample | What it shows | Packages it uses |
|---|---|---|
| [Console](RagDotNet.Samples.Console) | The quick start: Markdown and text files in an in-memory store, streamed answers with citations | `RagDotNet.Core` |
| [Documents](RagDotNet.Samples.Documents) | PDF, Word and HTML files in a SQLite file, with hybrid (vector and keyword) search; a second run skips unchanged files | `RagDotNet.Readers.Pdf`, `.Readers.Docx`, `.Readers.Html`, `.VectorData.Sqlite` |
| [Ocr](RagDotNet.Samples.Ocr) | Scanned PDFs, and images in documents, read with Tesseract | `RagDotNet.Ocr.Tesseract` and the readers |
| [WebApi](RagDotNet.Samples.WebApi) | An ASP.NET Core API: upload documents that are ingested in the background, check the job, ask questions | `RagDotNet.Hosting`, the readers, `RagDotNet.VectorData.Sqlite` |

## Before you run them

The samples use [Ollama](https://ollama.com) by default, running on this machine with two models:

```
ollama pull llama3.2:3b
ollama pull nomic-embed-text
```

To use another provider, set `Provider` in [Shared/providers.json](Shared/providers.json): `Ollama`, `GitHubModels` (needs the `GITHUB_TOKEN` environment variable) or `OpenAI` (needs `OPENAI_API_KEY`). The models and endpoints are set there too. Every sample reads the same file, through [Shared/ProviderFactory.cs](Shared/ProviderFactory.cs).

A 3B model is small. It answers simple questions well, but sometimes answers from the wrong part of a long passage. A larger model, such as `llama3.1:8b` or `qwen2.5:14b`, does better if your machine can run it.

## Using the packages in your own app

The samples build against the code in this repository, so they always match it. In your own app, install the packages from nuget.org instead; each sample's README lists the `dotnet add package` commands its code needs, with Ollama as the provider. RagDotNet is in preview, so add `--prerelease`. To keep the samples' provider switch as well, add `Microsoft.Extensions.AI.OpenAI`, `Microsoft.Extensions.Configuration.Json` and `Microsoft.Extensions.Configuration.Binder`, and copy [Shared/ProviderFactory.cs](Shared/ProviderFactory.cs).

You also bring the model client and, unless you use one of the RagDotNet stores, the vector store. The samples use `OllamaSharp`, `Microsoft.Extensions.AI.OpenAI` and `CommunityToolkit.VectorData.InMemory`; any `IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>` and Microsoft.Extensions.VectorData `VectorStore` works.
