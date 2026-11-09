# Document readers

RagDotNet reads each document into an ordered list of blocks: headings, paragraphs, lists, tables and code. Each block carries its heading path, for example `["Sync", "Conflicts"]`, and for PDF its page number. Chunkers work from these blocks, and citations show the heading path or the page.

Text (`.txt`) and Markdown (`.md`) readers are built into `RagDotNet.Core`. PDF, DOCX and HTML each have their own package, so an app that doesn't need a format doesn't carry its parser.

| Package | Reads | Parser |
|---|---|---|
| `RagDotNet.Readers.Pdf` | `.pdf`, `application/pdf` | [PdfPig](https://github.com/UglyToad/PdfPig) |
| `RagDotNet.Readers.Docx` | `.docx`, `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | [Open XML SDK](https://github.com/dotnet/Open-XML-SDK) |
| `RagDotNet.Readers.Html` | `.html`, `.htm`, `.xhtml`, `text/html`, `application/xhtml+xml` | [AngleSharp](https://anglesharp.github.io) |

## Registering a reader

With dependency injection:

```csharp
services.AddRag(o => { /* ... */ })
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader();
```

With the pipeline builder:

```csharp
var rag = new RagPipelineBuilder()
    // .UseChatClient(...), .UseEmbeddingGenerator(...), .UseVectorStore(...)
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader()
    .Build();
```

The reader is chosen by the document's content type when one is given, and otherwise by its file extension, ignoring case. `report.PDF` and a file named `download` with content type `application/pdf` both go to the PDF reader. A document no registered reader accepts raises `UnsupportedDocumentException`.

## What each reader keeps

**PDF**
- **Text:** every block carries its page number, so citations show `p. 3` or `pp. 3–4`. Text is grouped into blocks and put in reading order with PdfPig's layout analysis.
- **Headings:** recognised by font size. A block of at most two lines whose text is at least 1.15 times the document's most common size is a heading. The distinct heading sizes, largest first, give levels 1, 2, 3 and so on. A PDF whose headings are the same size as its body text gets no heading paths, but its citations still show pages.
- **Code:** a block set entirely in a monospace font (Courier, Consolas, or any font with "Mono" in its name) keeps its line breaks.
- **Lists:** a block whose first line starts with `•`, `-`, `–`, `*` or a number such as `1.` becomes a list. A line without a marker continues the previous item, so wrapped items stay whole.
- **Not read as structure:** tables become ordinary paragraph text. Text in images is not read (see warnings below).
- **Title:** the PDF's own title if it has one, otherwise its first top-level heading.

**DOCX**
- **Headings:** from the built-in Heading 1–9 styles, from the Title style, and from any style or paragraph that sets an outline level, following each style's "based on" chain. A company template with its own heading styles therefore works without configuration as long as those styles have outline levels, as Word's own templates do.
- **Lists:** consecutive numbered or bulleted paragraphs become one list, indented by level.
- **Tables:** become table blocks with one line per row (`| cell | cell |`), so a chunk never splits a row. Nested tables are flattened into their cell.
- **Code:** paragraphs in a style whose name contains "Code", or set entirely in a monospace font, become one code block.
- **Also:** tabs become spaces and line breaks are kept. Tracked deletions are ignored; tracked insertions are read.
- **Not read:** headers, footers, footnotes, endnotes, comments and text boxes.

**HTML**
- **Boilerplate removed first:**
  - the elements `script`, `style`, `noscript`, `template`, `svg`, `canvas`, `iframe`, `object`, `form`, `nav` and `aside`;
  - `header` and `footer` elements outside an `article`;
  - hidden elements: the `hidden` attribute, `aria-hidden="true"`, or `display: none`;
  - the roles `navigation`, `banner`, `contentinfo`, `complementary` and `search`;
  - any element whose `id` or `class` contains `cookie`, `consent`, `gdpr`, `newsletter`, `breadcrumb`, `sidebar`, `share` or `skip-link`.
- **Where the content is taken from:** the first `main` element, else the first element with `role="main"`, else the page's only `article`, else the whole `body`.
- **Blocks:**
  - `h1`–`h6` become headings;
  - `p`, `blockquote` and `figcaption` become paragraphs;
  - `ul`, `ol` and `dl` become lists;
  - `table` becomes a table;
  - `pre` becomes code;
  - loose text in a `div` or `section` becomes a paragraph.
- **Encoding:** detected like a browser does: byte-order mark, then `<meta charset>`, then UTF-8. A damaged page still gives its readable text. Nothing on the page is run or fetched.
- **Title:** the page `<title>`, otherwise its first `h1`.

## Warnings

A reader never drops content silently. Anything it cannot read becomes an entry in `IngestResult.Warnings`; ingest continues with everything else.

| Code (`IngestWarningCodes`) | When | `PageNumber` |
|---|---|---|
| `PageWithoutText` | A PDF page has no text layer, such as a scanned page. | The page |
| `ImageNotRead` | Images were not read: on a PDF page that has text, in a DOCX, or embedded in an HTML page as a `data:` URI. Images an HTML page links to elsewhere are not counted, because their bytes are not part of the document. | The page for PDF, otherwise none |
| `EmptyDocument` | The document produced no text to index. | None |

Reading text in images needs an OCR provider, which is planned as the separate `RagDotNet.Ocr.Tesseract` package.

## Errors

A PDF or DOCX file that is corrupt, truncated or password-protected raises `DocumentReadException`. Its message names the file, `FileName` and `Reason` give the details, and the parser's own exception is the inner exception. Nothing is written for that document. HTML never raises it for damaged markup, only if the stream itself cannot be read.

## How well they work

The evaluation harness renders its 8 Markdown test documents as PDF, DOCX and HTML and runs the same 40 questions on each format. See [Format check](evaluation.md#format-check) for the results.
