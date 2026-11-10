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
- **Text:** every block carries its page number, so citations show `p. 3` or `pp. 3–4`. Text is grouped into blocks and put in reading order with PdfPig's layout analysis. A block is also split wherever the font size changes sharply between lines, so a heading set close above its text stays separate. A word hyphenated at the end of a line is rejoined when the next line continues it in lower case.
- **Headings:** recognised by font size. A block of at most two lines whose text is at least 1.15 times the document's most common size is a heading. The distinct heading sizes, largest first, give levels 1, 2, 3 and so on. A PDF whose headings are the same size as its body text gets no heading paths, but its citations still show pages.
- **Code:** a block set entirely in a monospace font (Courier, Consolas, or any font with "Mono" in its name) keeps its line breaks.
- **Lists:** a block whose first line starts with `•`, `-`, `–` or `*` becomes a list, with each item written as `- item`. A block counts as a numbered list only when at least two of its lines start with a number such as `1.`, and those items keep their numbers. A paragraph that merely begins with a number, such as "2026. was a good year", stays a paragraph with its text unchanged. A line without a marker continues the previous item, so wrapped items stay whole. Numbered items spaced far enough apart to land in blocks of their own are joined back into one list when they follow on from each other (1., 2., 3.) on the same page.
- **Not read as structure:** tables become ordinary paragraph text. Text in images is not read (see warnings below).
- **Title:** the PDF's own title if it has one, otherwise its first top-level heading.

**DOCX**
- **Headings:** from the built-in Heading 1–9 styles, from the Title style, and from any style or paragraph that sets an outline level, following each style's "based on" chain. A company template with its own heading styles therefore works without configuration as long as those styles have outline levels, as Word's own templates do.
- **Lists:** consecutive numbered or bulleted paragraphs become one list, indented by level.
- **Tables:** become table blocks with one line per row (`| cell | cell |`), so a chunk never splits a row. Rows inside content controls are included, and nested tables are flattened into their cell.
- **Table of contents:** entries in the TOC 1–9 styles, and the TOC Heading ("Contents") above them, are skipped, because they repeat the headings with page numbers. The headings themselves are read.
- **Code:** paragraphs in a style with "Code" as a word of its id or name (such as `Code`, `SourceCode` or "HTML Code", but not "Barcode"), or set entirely in a monospace font, become one code block.
- **Also:** tabs become spaces, line breaks are kept, and non-breaking hyphens are kept as hyphens. Tracked deletions are ignored; tracked insertions are read. Symbol characters inserted from symbol fonts (`w:sym`, such as Wingdings arrows) are not read.
- **Not read:** headers, footers, footnotes, endnotes, comments and text boxes. Text boxes are skipped on purpose: Word stores each one twice inside the paragraph it is anchored to, so reading them would add duplicated text to that paragraph.

**HTML**
- **Where the content is taken from:** the first `main` element, else the first element with `role="main"`, else the page's only `article`, else the whole `body`. Nothing outside it is read, so navigation, banners and footers around a `main` never reach the text. A candidate inside page furniture (an `aside` or `nav`, a hidden element, a removed role or a boilerplate name below) is passed over, so a teaser `article` in a sidebar or a hidden `main` never becomes the content. The root is chosen before anything is removed, so no rule can remove the content or what contains it.
- **Removed inside the content:**
  - `script`, `style`, `noscript`, `template`, `svg`, `canvas`, `iframe`, `object` and `nav`;
  - form controls (`input`, `select`, `option`, `textarea`, `button`, `label`);
  - hidden elements: the `hidden` attribute (except `hidden="until-found"`, which is findable on the page), `aria-hidden="true"`, or `display: none`;
  - the roles `navigation`, `banner`, `contentinfo`, `complementary` and `search`;
  - elements whose `id`, or one of whose class names (separated by any whitespace), is exactly a common boilerplate name: `cookie-banner`, `cookie-consent`, `cookie-notice`, `cookie-bar`, `cookies`, `consent-banner`, `gdpr-banner`, `newsletter`, `newsletter-signup`, `breadcrumb`, `breadcrumbs`, `sidebar`, `share-buttons`, `social-share`, `sharing`, `skip-link`, `skip-to-content`. Only whole names match, so `has-sidebar`, `share-price` or `cookie-recipes` are kept.
- **Also removed when the content is the whole `body`** (no `main` or single `article`):
  - `aside`;
  - `header` and `footer` that are not inside an `article`, `section` or `main`;
  - a `form` with no headings, tables or sections and at most one paragraph, such as a search box with a hint. A form wrapping the whole page, as ASP.NET WebForms and SharePoint pages do, is read.
- **Blocks:**
  - `h1`–`h6` become headings;
  - `p`, `blockquote` and `figcaption` become paragraphs;
  - `ul`, `ol` and `dl` become lists, including lists nested inside a `div` within an item and `dt`/`dd` pairs grouped in `div`s;
  - `table` becomes a table;
  - `pre` becomes code;
  - loose text in a `div` or `section` becomes a paragraph;
  - an inline element that wraps blocks, such as a link around a card with a heading and text, is read as the blocks it holds.
- **Encoding:** detected like a browser does: byte-order mark, then `<meta charset>`, then UTF-8. A damaged page still gives its readable text. Nothing on the page is run or fetched.
- **Tables:** a cell that spans several columns (`colspan`) is followed by empty cells, so the columns of each row stay aligned.
- **Title:** the page `<title>`, otherwise its first top-level heading (an `h1`, or the highest heading the page has).

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

- **A DOCX that opens but contains something the reader does not expect** raises `DocumentReadException` with the reason "unexpected content in the document". A file whose zip opens but whose XML is damaged is still reported as not a readable DOCX.
- **One unreadable page fails the whole PDF.** If PdfPig cannot parse a single page or image, the reader raises `DocumentReadException` for the whole file rather than skipping that page, so nothing is lost without notice. If that happens with a file that opens in a PDF viewer, re-saving or printing it to PDF usually produces a file the reader can open.

The `Add…Reader()` methods for `RagPipelineBuilder` are in the `RagDotNet.Configuration` namespace, next to the builder itself. The ones for `services.AddRag(...)` are in `Microsoft.Extensions.DependencyInjection`, next to `AddRag`.

## How well they work

The evaluation harness renders its 8 Markdown test documents as PDF, DOCX and HTML and runs the same 40 questions on each format. See [Format check](evaluation.md#format-check) for the results.
