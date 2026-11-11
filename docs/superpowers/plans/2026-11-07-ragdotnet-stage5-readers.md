# RagDotNet Stage 5 — PDF, DOCX and HTML Readers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three reader packages turn PDF, DOCX and HTML files into the same `ParsedDocument` blocks the Markdown reader produces, and the evaluation harness checks that retrieval over each format holds up against the Markdown baseline.

**Architecture:**
- **Packages.** Each format is its own package under `src/`, with its own parser dependency, so Core stays free of heavy dependencies (spec §2 anti-goal).
  - `RagDotNet.Readers.Pdf` uses PdfPig.
  - `RagDotNet.Readers.Docx` uses DocumentFormat.OpenXml.
  - `RagDotNet.Readers.Html` uses AngleSharp.
- **What each package holds:** one public `IDocumentReader`, plus `AddPdfReader()` / `AddDocxReader()` / `AddHtmlReader()` extensions for both `IRagBuilder` (DI) and `RagPipelineBuilder`.
- **Core:** gains only two things readers share: `DocumentReadException` and the `IngestWarningCodes` constants.
- **Test files:** a small generator tool writes the test files that need a binary format (sample PDF and DOCX, and the base Quill set rendered as PDF, DOCX and HTML). The generated files are committed, so tests never depend on the generator running.

**Tech Stack:**
- `PdfPig` 0.1.16, `DocumentFormat.OpenXml` 3.5.1, `AngleSharp` 1.10.0.
- Markdig 1.5.1 (already in Core) to render Markdown in the generator.
- .NET `net8.0;net10.0` for the packages and their tests; `net10.0` for the harness and the generator.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`, revision 2.1, §10 stage 5. Relevant sections:
- §3, the package table:
  - `RagDotNet.Readers.Pdf`: "PDF reader with page numbers", PdfPig.
  - `RagDotNet.Readers.Docx`: "DOCX reader with heading paths", DocumentFormat.OpenXml.
  - `RagDotNet.Readers.Html`: "HTML reader with heading paths; strips boilerplate", AngleSharp.
- §5.1, steps 3–5: readers are selected by `CanRead`. They output blocks (heading, paragraph, list, table, code), each with `PageNumber?` and `HeadingPath`. A page with no text and no OCR provider "is **reported as a warning** in `IngestResult.Warnings`. It is never silently skipped."
- §5.12: `.AddPdfReader() .AddDocxReader() .AddHtmlReader()`.
- §5.11: OCR targets are image-only PDF pages, images on PDF pages that have text, and embedded images in DOCX and HTML "where the image bytes are available locally". OCR itself is stage 6.
- §2 anti-goal: "silently skipping pages that have no text".
- §7: `tests/TestAssets/` holds the "sample corpus: md, pdf (incl. a scanned page), docx, html".
- Owner's stage 5 decision (2026-11-07): add a format check to the harness. The 8 base Quill documents are rendered as PDF, DOCX and HTML, and the same 40 questions run on each, compared with the Markdown baseline.

## Global Constraints

- Reader packages target `net8.0;net10.0`, have warnings as errors and the public API analyzer, and list their public API in `PublicAPI.Unshipped.txt` with XML docs, like Core.
- Core references none of the three parser libraries.
- Package names and namespaces are `RagDotNet.Readers.Pdf`, `RagDotNet.Readers.Docx` and `RagDotNet.Readers.Html`.
- Reader classes are `PdfReader`, `DocxReader` and `HtmlReader`, each `public sealed` with a parameterless constructor (spec §5.12: `.AddReader(new PdfReader())`).
- Readers never drop a page or embedded image without saying so. Each one they cannot read becomes an `IngestWarning`.
- Block text follows the Markdown reader's conventions, so chunking and evaluation evidence behave the same across formats:
  - list items are lines starting with `- `, with nested items indented 2 spaces per level;
  - table rows are lines `| a | b |`, with no separator row;
  - code keeps its line breaks.
- Unit tests and CI need no model, no network and no Docker. CI also runs the new reader tests on net8.0.
- Test and corpus content is original. The generator is deterministic for text: running it twice gives files that read back to identical blocks. Byte equality is not required.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A PDF with a scanned page** (an image and no text layer) ingests its other pages and returns a `PageWithoutText` warning naming that page number; nothing is dropped silently. Test: Task 3.
2. **A corrupt, truncated or password-protected PDF or DOCX** fails the ingest with `DocumentReadException`, which names the file and gives the reason. It is never an unhandled parser exception and never an empty document. Damaged HTML yields whatever text is readable, as a browser would show it. Test: Tasks 2–3 for the exception, Task 1 for HTML.
3. **HTML without a `<main>`**, with navigation, a cookie banner, a footer and inline scripts, still yields the article text and none of the boilerplate. Text inside `<script>`, `<style>` and hidden elements never reaches a block. Test: Task 1.
4. **DOCX headings** come from built-in heading styles and from custom styles that set an outline level. Lists and tables become List and Table blocks, so StructureAware never splits a table row. Test: Task 2.
5. **Content type and file name disagree** (for example `report.PDF`, or `application/pdf` on a file named `download`): the reader is chosen by content type first and case-insensitive extension second, matching the built-in readers. Test: Tasks 1–3.

---

## Decisions this plan makes *(builder's calls; the spec leaves them open)*

**Shared, in Core**
- **`public sealed class DocumentReadException : Exception`** in `RagDotNet.Documents`.
  - Constructor `DocumentReadException(string fileName, string reason, Exception? inner = null)`; properties `FileName` and `Reason`.
  - Message: `Could not read '{fileName}': {reason}`.
  - Readers wrap every parser exception in it. Ingest lets it pass through, as the spec does for provider failures.
- **`public static class IngestWarningCodes`** in `RagDotNet.Documents`, with constants `EmptyDocument = "EmptyDocument"` (the existing code, now named), `PageWithoutText = "PageWithoutText"` and `ImageNotRead = "ImageNotRead"`. Stage 6 OCR uses the same codes to find what to read.
- **Warning messages:**
  - `Page {n} has no extractable text; register an OCR provider to read it.` (`PageNumber` = n)
  - `Page {n} has {k} image(s) that were not read; register an OCR provider to read them.`
  - `{k} embedded image(s) were not read; register an OCR provider to read them.` (DOCX and HTML, `PageNumber` null)

**`CanRead`.** Readers follow the built-ins: when a content type is given, match on it (parameters such as `; charset=` are ignored, and matching is case-insensitive); otherwise match the file extension, case-insensitively.

| Reader | Content types | Extensions |
|---|---|---|
| Pdf | `application/pdf` | `.pdf` |
| Docx | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | `.docx` |
| Html | `text/html`, `application/xhtml+xml` | `.html`, `.htm`, `.xhtml` |

**HTML** (AngleSharp `HtmlParser`; no scripts run, nothing loaded over the network)
- **Removed before walking:**
  - the elements `script`, `style`, `noscript`, `template`, `svg`, `canvas`, `iframe`, `object`, `form`, `nav`, `aside`;
  - `header` and `footer` that are not inside an `article`;
  - any element with the `hidden` attribute, `aria-hidden="true"` or an inline style `display:none`;
  - roles `navigation`, `banner`, `contentinfo`, `complementary` and `search`;
  - any element whose `id` or `class` contains one of `cookie`, `consent`, `gdpr`, `newsletter`, `breadcrumb`, `sidebar`, `share`, `skip-link` (case-insensitive).
- **Content root:** the first `main`, else the first `[role=main]`, else the only `article` if there is exactly one, else `body`.
- **Blocks:**
  - `h1`–`h6` give Heading blocks at their own level, maintaining `HeadingPath` like the Markdown reader.
  - `p`, `blockquote` and `figcaption` give Paragraph blocks.
  - `ul`, `ol` and `dl` give List blocks (`dt`/`dd` become `- term: definition`).
  - `table` gives a Table block; `th` and `td` cells are joined into rows.
  - `pre` gives a Code block from its text content.
  - Loose text directly inside `div`, `section` or `article` that is not in a child block gives a Paragraph.
  - Inline whitespace collapses to single spaces, and `br` becomes a newline.
- **Title:** `<title>` if it is not empty, else the first `h1`.
- **Images:** only `img` elements with a `data:` URI count, because they are the only images whose bytes are available locally (spec §5.11). Their count becomes one `ImageNotRead` warning. Remote images are ignored, and docs/readers.md says so.
- **Errors and encoding:** AngleSharp parses the stream itself and detects the encoding the way browsers do (BOM, then `<meta charset>`, then UTF-8). It tolerates malformed markup, and it replaces bytes it cannot decode instead of failing, so a damaged page still yields its readable text. Only an I/O failure while reading the stream raises `DocumentReadException`. Empty input gives no blocks, and ingest then reports its existing `EmptyDocument` warning.

**DOCX** (`WordprocessingDocument.Open(stream, false)`)
- **Body walk:** paragraphs and tables in body order. Headers, footers, footnotes, comments and text boxes are not read in v1, and docs/readers.md says so.
- **Heading level:**
  - from the paragraph's style chain: style id `Heading1`–`Heading9`, or an `outlineLvl` of 0–8 on the style or the paragraph, gives level outlineLvl+1;
  - style `Title` gives level 1, and its text also becomes the document title.
  - A level-1 heading opens a new top-level path, as in Markdown.
- **Lists:** consecutive paragraphs with numbering (`numPr` on the paragraph or its style) become one List block. Each item is indented 2 spaces per `ilvl`.
- **Code:** consecutive paragraphs whose style id or name contains `Code`, or whose runs all use a monospace font (`Consolas`, `Courier New`, `Cascadia`, any name containing `Mono`), become one Code block with line breaks kept.
- **Tables:** each `w:tr` becomes `| cell | cell |`, with paragraphs inside a cell joined by spaces. Nested tables are flattened into their cell's text.
- **Paragraph text:** `w:t` text, with `w:tab` as a space, `w:br` as a newline, and deleted text (`w:delText`) ignored.
- **Images:** each `w:drawing` or `w:pict` that holds a blip counts towards one `ImageNotRead` warning.
- **Errors:** `OpenXmlPackageException`, `InvalidDataException` and `FileFormatException` (including encrypted files) become `DocumentReadException`.

**PDF** (`PdfDocument.Open(stream)`)
- **Per page:**
  - words come from `NearestNeighbourWordExtractor`, text blocks from `DocstrumBoundingBoxes`, and the order from `UnsupervisedReadingOrderDetector`;
  - every block gets `PageNumber` = the page's 1-based number;
  - lines within a block join with a space, except in Code blocks, which keep their line breaks.
- **Body font size:** the most frequent rounded letter size across the whole document.
- **Headings:** a block is a heading when it has at most 2 lines and its average letter size is ≥ 1.15 × the body size.
  - Distinct heading sizes, rounded to 0.5 pt and ranked largest first, give levels 1, 2, 3, ….
  - This is a font-size heuristic. Real-world PDFs without size differences get no heading paths; citations still show pages (spec §5.10).
- **Code:** a block whose letters all use a font with `Courier`, `Mono` or `Consolas` in its name.
- **Lists:** a block whose every line starts with `•`, `-`, `–`, `*` or `\d+[.)]` becomes a List block, each line rewritten to `- item`.
- **Tables** are not detected; a table becomes paragraph text, and docs/readers.md says so.
- **Pages and images:**
  - a page with no letters gives a `PageWithoutText` warning and no blocks;
  - a page with letters and `page.GetImages()` non-empty gives an `ImageNotRead` warning with the image count.
- **Title:** the document's info title if it is not empty, else the first level-1 heading.
- **Errors:** any PdfPig exception becomes `DocumentReadException`, including an encrypted file that cannot be opened without a password.

**Test-file generator.** `tests/RagDotNet.TestAssetGen`, a `net10.0` console app that is not packed or tested itself. Run it as `dotnet run --project tests/RagDotNet.TestAssetGen -- <command>` with one of these commands:
- `samples` writes `tests/TestAssets/pdf/sample.pdf` and `tests/TestAssets/docx/sample.docx`. `tests/TestAssets/html/sample.html` is written by hand.
- `formats` renders every `Dataset/base/corpus/*.md` to `Dataset/base/formats/{pdf,docx,html}/<id>.{pdf,docx,html}`. It walks the Markdig AST: headings, paragraphs, lists, pipe tables and fenced code.
  - **HTML:** a full page with `<nav>`, a cookie banner `div` and a `<footer>` around a `<main>`, so the format check also exercises boilerplate removal.
  - **DOCX:** built-in `Heading1`–`Heading3` styles, numbered paragraphs for lists, real tables, and a style named `Code` with Consolas.
  - **PDF:** A4 pages with Helvetica 11 pt body text and 18/15/13 pt headings, Courier 9.5 pt code, 1.6× line spacing inside a block and a 1.2-line gap between blocks, so Docstrum keeps one source block as one PDF block. Text wraps at the right margin. A table row is drawn as one line `cell | cell`, wrapped if needed.
- **PDF metadata** (creation date, producer) is fixed, so reruns change no text.
- **The scanned page** in `sample.pdf` is a page holding only an embedded PNG of a solid grey rectangle. A tiny PNG encoder inside the generator writes it with `System.IO.Compression.ZLibStream`.

**Harness format check**
- **Format variants:** a set may have `formats/<fmt>/` folders next to `corpus/`. A variant shares the set's questions and document ids, and its documents are the variant folder's files, with the id taken from the file name without its extension.
- **Validation:**
  - variants are checked with the set's rules, except that a question whose evidence cannot be found intact in one non-heading block of the variant's reader output is not a load error;
  - such a question is counted in the variant's `Unscorable` count and still scores as a miss;
  - a variant that is missing a document of the base set is an error, because the comparison would be meaningless.
- **Reading:** `DatasetLoader` reads each corpus file with a reader chosen by extension: `.md` uses `MarkdownReader`, and the others use the three new readers. The harness references the three packages.
- **`--formats <list>`:** `md` is the set's own corpus and the default; the others are `pdf`, `docx` and `html`. Each format runs the full matrix for every set that has it.
- **Report:** a variant gets its own section, `## base · pdf`, with the usual table and one extra line before it: `- Unscorable questions: {n}`. The `all` section still pools only `md` sets, so it stays comparable with stage 4.
- **Hashes:** a variant's hash covers its own files plus `questions.json`, and appears in the metadata as ``- Set `base` · pdf: 8 documents, 40 questions, hash …``.
- **Real run for the format check:** `--chunkers structure-aware --max-tokens 512 --overlap 0.1 --formats md,pdf,docx,html`. That is the library default, which is what the check is about; it is not a tuning run.

## File map

| File | Responsibility | Task |
|---|---|---|
| `src/RagDotNet.Core/Documents/DocumentReadException.cs`, `IngestWarningCodes.cs` | shared exception and warning codes | 1 |
| `src/RagDotNet.Readers.Html/` (`HtmlReader.cs`, `HtmlReaderExtensions.cs`, csproj, `PublicAPI.*.txt`) | HTML reader | 1 |
| `src/RagDotNet.Readers.Docx/` (same shape) | DOCX reader | 2 |
| `src/RagDotNet.Readers.Pdf/` (same shape) | PDF reader | 3 |
| `tests/RagDotNet.Readers.Tests/` (`HtmlReaderTests.cs`, `DocxReaderTests.cs`, `PdfReaderTests.cs`) | reader tests, `net8.0;net10.0` | 1–3 |
| `tests/RagDotNet.TestAssetGen/` (`Program.cs`, `PdfWriter.cs`, `DocxWriter.cs`, `HtmlWriter.cs`, `Png.cs`) | generator for test files | 2–4 |
| `tests/TestAssets/{html,docx,pdf}/sample.*` | sample files | 1–3 |
| `tests/RagDotNet.Evaluation/Dataset/base/formats/**` | the base set rendered as pdf, docx and html | 4 |
| `tests/RagDotNet.Evaluation/Dataset/DatasetLoader.cs`, `Running/*`, `Cli/Options.cs` | format variants, `--formats`, report sections | 4 |
| `RagDotNet.slnx`, `Directory.Packages.props`, `.github/workflows/ci.yml` | new projects, versions, CI | 1–3 |
| `docs/readers.md` (new), `docs/evaluation.md`, `docs/evaluation-results.md`, `README.md` | reader guide, format-check results | 5 |

---

### Task 1: Core additions and the HTML reader

**Files:**
- Create: `src/RagDotNet.Core/Documents/DocumentReadException.cs` and `IngestWarningCodes.cs`.
- Create: the `RagDotNet.Readers.Html` package and the `RagDotNet.Readers.Tests` project, which references Core, the three reader packages as they appear (Html now), and `CommunityToolkit.VectorData.InMemory`.
- Create: `tests/TestAssets/html/sample.html`.
- Modify:
  - `Ingestor.cs:107`, to use `IngestWarningCodes.EmptyDocument`;
  - `RagDotNet.slnx`;
  - `Directory.Packages.props`, adding AngleSharp 1.10.0;
  - Core's `PublicAPI.Unshipped.txt`;
  - `.github/workflows/ci.yml`, adding a step `dotnet test --project tests/RagDotNet.Readers.Tests --no-build --configuration Release --framework net8.0`.

**Interfaces:**
- Produces:
  - `DocumentReadException(string fileName, string reason, Exception? inner = null)` with `FileName` and `Reason`;
  - `IngestWarningCodes.{EmptyDocument, PageWithoutText, ImageNotRead}`;
  - `public sealed class HtmlReader : IDocumentReader`;
  - `HtmlReaderExtensions.AddHtmlReader(this IRagBuilder)` and `AddHtmlReader(this RagPipelineBuilder)`, both returning the builder.

`sample.html` is about 600 words of original Quill text. It needs: a `<title>`; `<nav>`; a cookie banner `div id="cookie-consent"`; `<main>` holding h1/h2/h3, paragraphs, a nested `ul`, a `table` with a `thead`, a `pre` code block and one `data:` image; an inline `<script>` holding the word `SECRET_SCRIPT`; a `<p hidden>` holding `HIDDEN_TEXT`; an `<aside>`; and a `<footer>`.

- [ ] **Step 1: Write the failing tests** in `HtmlReaderTests`.
  - `Reads_headings_paragraphs_lists_tables_and_code`: from `sample.html`, assert:
    - the block kinds in order and the heading paths, e.g. `["Sync", "Conflicts"]`;
    - a Table block whose text has a line `| Setting | Default |`;
    - nested list items indented with 2 spaces;
    - the `pre` text with its newlines kept;
    - `Title` equal to the `<title>`.
  - `Boilerplate_and_hidden_text_never_reach_blocks` (Review Focus 3): no block text contains `SECRET_SCRIPT`, `HIDDEN_TEXT`, the nav link text, the cookie text, the aside text or the footer text.
  - `Without_main_the_single_article_is_the_root`, and `Without_main_or_article_the_body_is_used_minus_boilerplate`: built from inline HTML strings.
  - `Data_uri_images_give_one_ImageNotRead_warning_and_remote_images_none`.
  - `Damaged_or_empty_input_never_throws` (Review Focus 2, HTML case): invalid UTF-8 bytes inside a paragraph still yield that paragraph's readable text; an empty stream gives no blocks; neither throws.
  - `CanRead_matches_content_type_first_then_extension` (Review Focus 5), as a Theory:
    - `("text/html; charset=utf-8","x.bin")` → true;
    - `(null,"Page.HTM")` → true;
    - `("text/plain","x.html")` → false;
    - `(null,"x.md")` → false.
  - `Pipeline_ingests_html_after_AddHtmlReader`: on both the builder and DI paths, `IngestAsync` of `sample.html` is `Created`, and a retrieved chunk carries the heading path.
- [ ] **Step 2: Run** `dotnet test --project tests/RagDotNet.Readers.Tests`. Expected: compile failure, then FAIL.
- [ ] **Step 3: Implement** as set out under Decisions → Shared and Decisions → HTML.
- [ ] **Step 4: Run** `dotnet test` from the repo root. Expected: all pass, on both frameworks for the reader tests, and no "Zero tests ran".
- [ ] **Step 5: Commit** `feat(readers): HTML reader, DocumentReadException and warning codes`.

---

### Task 2: DOCX reader and the test-file generator

**Files:**
- Create:
  - the `RagDotNet.Readers.Docx` package;
  - `DocxReaderTests.cs`;
  - `tests/RagDotNet.TestAssetGen`, with `samples` writing the DOCX for now;
  - `tests/TestAssets/docx/sample.docx`, which the generator writes.
- Modify: `RagDotNet.slnx`; `Directory.Packages.props`, adding DocumentFormat.OpenXml 3.5.1.

**Interfaces:**
- Produces:
  - `public sealed class DocxReader : IDocumentReader`;
  - `AddDocxReader(this IRagBuilder)` and `AddDocxReader(this RagPipelineBuilder)`;
  - `TestAssetGen`'s `DocxWriter.Write(string path, IReadOnlyList<GenBlock> blocks, string? title)`, where `GenBlock` is a generator-internal record `(BlockKind Kind, int Level, string Text, IReadOnlyList<IReadOnlyList<string>>? Rows, IReadOnlyList<(int Depth, string Text)>? Items)`. Task 3's `PdfWriter` and Task 4's `HtmlWriter` take the same `GenBlock` list.
  - `MarkdownToGenBlocks.Convert(string markdown)` turns Markdig output into `GenBlock`s; Task 4 uses it.

`sample.docx` holds:
- a `Title`;
- `Heading1` and `Heading2` paragraphs;
- a custom style `Chapter` with `outlineLvl` 0;
- body paragraphs that include a tab and a line break;
- a two-level numbered list;
- a 3×2 table;
- two `Code`-style paragraphs;
- one inline PNG image;
- a tracked deletion containing `DELETED_TEXT`.

- [ ] **Step 1: Write the failing tests** in `DocxReaderTests`.
  - `Headings_come_from_heading_styles_and_outline_levels` (Review Focus 4): the `Chapter` paragraph is a level-1 heading, and the heading paths match.
  - `Title_style_sets_the_document_title`.
  - `Numbered_paragraphs_become_one_list_block_indented_by_level`.
  - `Tables_become_table_blocks_with_one_line_per_row`.
  - `Code_style_paragraphs_become_one_code_block_with_line_breaks`.
  - `Deleted_text_is_ignored`.
  - `Embedded_image_gives_one_ImageNotRead_warning`.
  - `Corrupt_or_encrypted_file_throws_DocumentReadException` (Review Focus 2): truncated `sample.docx` bytes, and random bytes named `x.docx`.
  - `CanRead_matches_content_type_first_then_extension` (Review Focus 5).
  - `Pipeline_ingests_docx_after_AddDocxReader`.
- [ ] **Step 2: Run** `dotnet run --project tests/RagDotNet.TestAssetGen -- samples`, then `dotnet test --project tests/RagDotNet.Readers.Tests`. Expected: FAIL, because the reader doesn't exist yet.
- [ ] **Step 3: Implement** the reader as set out under Decisions → DOCX.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(readers): DOCX reader and test-file generator`, including the generated `sample.docx`.

---

### Task 3: PDF reader

**Files:**
- Create: the `RagDotNet.Readers.Pdf` package; `PdfReaderTests.cs`; `TestAssetGen/PdfWriter.cs` and `Png.cs`; `tests/TestAssets/pdf/sample.pdf`.
- Modify: `RagDotNet.slnx`; `Directory.Packages.props`, adding PdfPig 0.1.16; the generator's `samples` command, which now also writes the PDF.

**Interfaces:**
- Produces:
  - `public sealed class PdfReader : IDocumentReader`;
  - `AddPdfReader(this IRagBuilder)` and `AddPdfReader(this RagPipelineBuilder)`;
  - `PdfWriter.Write(string path, IReadOnlyList<GenBlock> blocks, string? title, IReadOnlyList<int>? imageOnlyPagesAfterBlock = null)`, which Task 4 uses with no image pages.

`sample.pdf` has three pages:
1. headings, paragraphs, a bullet list and a Courier code block;
2. only a grey PNG, i.e. the scanned page;
3. text plus one small embedded PNG.

- [ ] **Step 1: Write the failing tests** in `PdfReaderTests`.
  - `Blocks_carry_their_page_numbers`: blocks from pages 1 and 3 have `PageNumber` 1 and 3, and none has 2.
  - `Scanned_page_gives_a_PageWithoutText_warning_and_other_pages_are_read` (Review Focus 1): exactly one warning with code `PageWithoutText` and `PageNumber` 2, and the text of pages 1 and 3 is present.
  - `Image_on_a_text_page_gives_an_ImageNotRead_warning_with_its_page`.
  - `Larger_fonts_become_headings_ranked_by_size`: the 18 pt heading is level 1 and the 15 pt heading is level 2, and the heading paths match.
  - `Courier_text_becomes_a_code_block_with_line_breaks`.
  - `Bullet_lines_become_a_list_block`.
  - `Ingest_through_the_pipeline_reports_the_page_warning`: `AddPdfReader`, then `IngestAsync(sample.pdf)`, gives `Created` and `Warnings` containing the page-2 warning. A retrieved chunk has `PageStart`/`PageEnd` set, and a citation `Location` shows the page.
  - `Corrupt_or_encrypted_pdf_throws_DocumentReadException` (Review Focus 2): truncated bytes, and a PDF the generator wrote with an owner and user password, if PdfPig's writer supports encryption. If it doesn't, use truncated and garbage input only, and record a ledger ruling.
  - `CanRead_matches_content_type_first_then_extension` (Review Focus 5): `(null,"Report.PDF")` → true, and `("application/pdf","download")` → true.
- [ ] **Step 2: Run** the generator's `samples` command, then the reader tests. Expected: FAIL.
- [ ] **Step 3: Implement** as set out under Decisions → PDF.
- [ ] **Step 4: Run** `dotnet test`. Expected: all pass.
- [ ] **Step 5: Commit** `feat(readers): PDF reader with page numbers and no-text page warnings`.

---

### Task 4: Harness format check

**Files:**
- Modify:
  - `tests/RagDotNet.Evaluation/Dataset/DatasetLoader.cs`, `Question.cs`, `Running/EvaluationRunner.cs`, `Running/ReportWriter.cs`, `Cli/Options.cs`, `Program.cs` and the Evaluation csproj (reference the three reader packages, and copy `Dataset/**/*.{pdf,docx,html}`);
  - `TestAssetGen`, adding the `formats` command and `HtmlWriter.cs`.
- Create: `tests/RagDotNet.Evaluation/Dataset/base/formats/{pdf,docx,html}/*`, written by the generator.
- Test: `tests/RagDotNet.Evaluation.Tests/FormatTests.cs`.

**Interfaces:**
- Consumes: the three readers (Tasks 1–3), and `MarkdownToGenBlocks`, `PdfWriter`, `DocxWriter` and `HtmlWriter` (Tasks 2–4).
- Produces:
  - `DatasetSet` gains `string Format` (default `"md"`) and `int Unscorable` (default 0);
  - `DatasetLoader.LoadSetsAsync(string root, IReadOnlyList<string> formats, CancellationToken ct)`. The existing two-argument overload means `["md"]`.
  - Report section titles are `{set}` for md and `{set} · {format}` for the others.

- [ ] **Step 1: Write the failing tests** in `FormatTests`.
  - `Every_base_document_exists_in_every_format`: each of `pdf`, `docx` and `html` has the 8 base ids.
  - `Format_variants_load_with_the_set_questions_and_their_own_hash`: loading with `["md","pdf"]` gives two `DatasetSet`s named `base`, formats `md` and `pdf`, the same 40 questions, and different hashes.
  - `Html_variant_has_no_unscorable_questions`: HTML renders every block one-to-one.
  - `Unscorable_questions_are_counted_not_rejected`: a temp set's pdf variant is missing one evidence string, so it loads with `Unscorable == 1`, and that question's rank is null.
  - `Missing_document_in_a_variant_is_an_error`.
  - `Report_has_a_section_per_format_and_all_pools_md_only`: fake mode with `--formats md,html` gives headings `## base` and `## base · html`. `## all` is absent, since there's one md set, and the html section shows `- Unscorable questions: 0`.
  - `Unknown_format_exits_1`.
- [ ] **Step 2: Run** `dotnet run --project tests/RagDotNet.TestAssetGen -- formats`, then `dotnet test --project tests/RagDotNet.Evaluation.Tests`. Expected: the new tests FAIL. All existing tests still pass, and `Base_set_hash_is_unchanged_from_stage3` must stay green because `formats/` is outside `corpus/`.
- [ ] **Step 3: Implement** as set out under Decisions → Harness format check.
- [ ] **Step 4: Run** `dotnet test`, then a fake-mode run with `--formats md,pdf,docx,html --chunkers structure-aware --max-tokens 512 --overlap 0.1` twice. Expected: all pass, and the two reports are byte-identical.
- [ ] **Step 5: Commit** `feat(eval): format check over the base set rendered as PDF, DOCX and HTML`, including the generated files.

---

### Task 5: Real format-check run and docs

**Files:**
- Create: `docs/readers.md`.
- Modify: `docs/evaluation.md`, `docs/evaluation-results.md` and `README.md`.

- [ ] **Step 1: Run** `dotnet run --project tests/RagDotNet.Evaluation -- --embedder ollama --chunkers structure-aware --max-tokens 512 --overlap 0.1 --formats md,pdf,docx,html` and save the report into `docs/evaluation-results.md` under a new heading, `## Format check (stage 5)`. Keep the stage 4 report above it unchanged. If Ollama or its models are missing, stop and report.
- [ ] **Step 2: Write `docs/evaluation.md` → "Format check":**
  - the method;
  - the table of md, pdf, docx and html rows, each with its Unscorable count;
  - an explanation of each gap of 0.05 MRR or more, found by comparing that format's ranks with md question by question.

  Describe only what the numbers show.
- [ ] **Step 3: Write `docs/readers.md`:**
  - what each reader keeps (pages, heading paths, lists, tables, code) and what it does not read (DOCX headers, footers and footnotes; PDF tables as structure; remote HTML images);
  - the boilerplate rules;
  - the warning codes;
  - `DocumentReadException`;
  - how to register each reader.

  In `README.md`:
  - status table: "PDF, DOCX and HTML readers" moves to Works;
  - the "Under development" note no longer lists them as still to come;
  - the evaluation section mentions the format check;
  - a short "Readers" subsection links to docs/readers.md.
- [ ] **Step 4: Run** `dotnet test`, then the CI commands (Release build, net8.0 Core and Readers tests). Expected: all pass, with 0 warnings.
- [ ] **Step 5: Commit** `docs: stage 5 reader guide and format-check results`.

---

## Self-review notes

- **Spec coverage:**
  - The §3 packages and dependencies → Tasks 1–3.
  - Page numbers for PDF → Task 3.
  - Heading paths for DOCX and HTML → Tasks 1–2.
  - Boilerplate stripping → Task 1.
  - §5.1 steps 3–5 (selection, blocks, the no-text-page warning) → Tasks 1–3, with the warning in Task 3.
  - §5.12 registration → each task's extensions.
  - §7 TestAssets (md, pdf with a scanned page, docx, html) → Tasks 1–3.
  - §2 anti-goal (no silent skipping) → the `PageWithoutText` and `ImageNotRead` warnings.
  - The owner's format check → Tasks 4–5.
- **Deliberately not in this stage:** OCR (stage 6). Its hook is the two warning codes, which stage 6 will act on.
- **Type consistency:**
  - `GenBlock`, `MarkdownToGenBlocks`, `DocxWriter`, `PdfWriter` and `HtmlWriter` are generator-internal; they are defined in Tasks 2–4 and used only there.
  - The extension method names match §5.12.
  - The `DatasetSet` additions are used only by Task 4.
