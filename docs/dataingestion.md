# Microsoft.Extensions.DataIngestion adapters (RagDotNet.DataIngestion)

`RagDotNet.DataIngestion` lets RagDotNet use the readers and chunkers from Microsoft's `Microsoft.Extensions.DataIngestion` library. A Microsoft reader becomes a RagDotNet `IDocumentReader`, and a Microsoft chunker becomes a RagDotNet `IChunker`; the rest of the pipeline (embedding, stores, retrieval, answers) is unchanged.

**This package is a preview.** Microsoft.Extensions.DataIngestion is itself a preview (this package is built against `10.11.1-preview.1.26569.2`) and its API changes between releases, so RagDotNet keeps it out of Core: only apps that install this package depend on it. Microsoft marks its APIs experimental with the `MEDI001` diagnostic; suppress it in your project to use them.

## Setup

```csharp
using Microsoft.Extensions.DataIngestion;          // MarkdownReader (from Microsoft.Extensions.DataIngestion.Markdig)
using Microsoft.Extensions.DataIngestion.Chunkers; // HeaderChunker
using RagDotNet.DataIngestion;

services.AddRag(o => o.Chunking = o.Chunking with { Chunker = "ms-header" })
    .AddDataIngestionReader(_ => new MarkdownReader(), [".md"], ["text/markdown"])
    .AddDataIngestionChunker("ms-header", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o)));
```

Without DI, wrap them yourself and pass them to `RagPipelineBuilder.AddReader` and `AddChunker`:

```csharp
var reader = new DataIngestionReader(new MarkdownReader(), [".md"], ["text/markdown"]);
var chunker = new DataIngestionChunker("ms-header", "1", o => new HeaderChunker(DataIngestionChunker.CreateOptions(o)));
```

## Readers

A reader handles the file extensions (with their dot, matching ignores case) and content types (ignoring case and parameters such as `; charset=utf-8`) you give it. Readers you add are tried before RagDotNet's built-in ones, so registering one for `.md` replaces RagDotNet's Markdown reader for those files.

Microsoft's document becomes RagDotNet's like this:

| Microsoft element | RagDotNet block |
|---|---|
| Header | A heading block, and a level of the heading path for what follows. A header with no level counts as level 1. The first level-1 header is the document's title. |
| Table | A table block (its markdown) |
| Image with bytes | An OCR candidate, so the [OCR](ocr.md) stage reads its text, plus a paragraph of its alternative text if it has one. With `ExtractImages` off, no candidate. |
| Image without bytes | A paragraph of its markdown, which keeps the text around an inline image |
| Anything else (paragraphs, footers, list items) | A paragraph |

Page numbers are kept. Blank elements are skipped. An empty stream gives an empty document (and the usual `EmptyDocument` warning) without calling the reader. Any error from the reader becomes a `DocumentReadException`, which the [background queue](hosting.md) treats as permanent; cancellation passes through.

**Microsoft's MarkdownReader drops content:** fenced and indented code blocks and HTML blocks are left out, and list markers are removed. RagDotNet's own Markdown reader keeps all of these, so it stays the default for `.md`; use Microsoft's when you want its behaviour, or to wrap another Microsoft reader (such as `MarkItDownReader` for Office files, which needs the `markitdown` tool installed).

## Chunkers

The chunker is created for each document from the app's `ChunkingOptions`. `DataIngestionChunker.CreateOptions` turns those into Microsoft's options: `MaxTokens` per chunk, and an overlap of `OverlapRatio` of that, kept below `MaxTokens` as Microsoft requires. It counts tokens with cl100k_base, as RagDotNet does; pass another `Tokenizer` to change that. As with every chunker, RagDotNet renumbers the chunks, drops blank ones, splits any over `MaxTokens` and recounts their tokens.

- **Heading paths:** `HeaderChunker` labels each chunk with its headings, so its chunks get their heading path (found by lookup, so headings containing `#` work). A chunk whose label doesn't name a heading of the document gets an empty heading path; `DocumentTokenChunker` gives no label at all, so all its chunks do.
- **No page numbers:** Microsoft's chunks don't say which pages they came from, so chunks from these chunkers have no page range, even from a PDF.
- **Version:** change the version you register with when you change the chunker or its settings, so documents are re-chunked on their next ingest.
