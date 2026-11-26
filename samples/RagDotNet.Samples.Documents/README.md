# Documents sample

Reads PDF, Word (`.docx`), HTML, Markdown and text files, then answers questions about them. Citations say where each passage is: the page for a PDF, the heading path for the other formats.

- The chunks and their vectors are stored in a SQLite file, with `RagDotNet.VectorData.Sqlite`.
- Retrieval combines vector and keyword search (hybrid), which finds exact terms, such as command names and version numbers, that embeddings alone can miss. The SQLite store does the keyword search itself, with SQLite's full-text search, so nothing else is needed. With a store that has no keyword search of its own, RagDotNet keeps an in-memory keyword index, or a Lucene index on disk with `RagDotNet.Keyword.Lucene`; see [docs/retrieval.md](../../docs/retrieval.md).
- The file is saved in an `index` folder next to the app. Run it again and unchanged files are skipped (`Unchanged`), so only new or edited files are embedded.
- A scanned page or an image the reader cannot read is printed as a warning; the [Ocr sample](../RagDotNet.Samples.Ocr) reads them. A corrupt or password-protected file is reported and skipped.

Pass files, folders, or both:

```
dotnet run --project samples/RagDotNet.Samples.Documents -- tests/RagDotNet.Evaluation/Dataset/base/formats/pdf/install-guide.pdf tests/RagDotNet.Evaluation/Dataset/base/formats/docx/configuration.docx tests/RagDotNet.Evaluation/Dataset/base/formats/html/keyboard-shortcuts.html
```

```
configuration.docx: Created, 12 chunks
keyboard-shortcuts.html: Created, 13 chunks
install-guide.pdf: Created, 12 chunks

Question (empty line to exit): What command installs Quill with winget?
The command to install Quill with winget is [1] winget install --id Quill.Quill --exact.
[1] install-guide.pdf — p. 1

Question (empty line to exit): If a setting is in both the global file and the vault file, which one applies?
If a setting appears in both files, the vault file wins [1].
[1] configuration.docx — Configuration reference > The configuration file
```

In your own app:

```
dotnet add package RagDotNet.Readers.Pdf --prerelease
dotnet add package RagDotNet.Readers.Docx --prerelease
dotnet add package RagDotNet.Readers.Html --prerelease
dotnet add package RagDotNet.VectorData.Sqlite --prerelease
dotnet add package OllamaSharp
```

Each package brings `RagDotNet.Core` with it. See [docs/readers.md](../../docs/readers.md), [docs/vector-stores.md](../../docs/vector-stores.md) and [docs/retrieval.md](../../docs/retrieval.md).
