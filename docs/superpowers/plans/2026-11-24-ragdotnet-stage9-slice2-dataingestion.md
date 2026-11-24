# Stage 9 slice 2: RagDotNet.DataIngestion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An optional `RagDotNet.DataIngestion` package that lets an app use any Microsoft.Extensions.DataIngestion reader as a RagDotNet `IDocumentReader`, and any of its chunkers as a RagDotNet `IChunker`.

**Architecture:** There are two adapters and a registration helper:
- **`DataIngestionReader`** turns an `IngestionDocument` into a `ParsedDocument`.
- **`DataIngestionChunker`** turns a `ParsedDocument` into an `IngestionDocument`, runs Microsoft's chunker on it, and maps the `IngestionChunk<string>` results back to `Chunk`s, recovering each chunk's heading path.
- **Two `IRagBuilder` extensions** register adapter instances in DI.

Core does not change; the stable API never depends on the preview package (spec §1).

**Tech Stack:** Microsoft.Extensions.DataIngestion `10.11.1-preview.1.26569.2`. It is the 10.x line that matches the repo's Microsoft.Extensions.AI 10.11; the 11.x line pulls in the 11.0 libraries. Tests also use Microsoft.Extensions.DataIngestion.Markdig at the same version.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md` (§1 "Relationship to Microsoft.Extensions.DataIngestion", the package table in §4, versioning in §9, §10 stage 9, and the risks table).

## Global Constraints

- The package ships as prerelease, always with a `-preview` label, using the Lucene csproj's MinVer target (spec §9).
- TFMs are net8.0 and net10.0; TreatWarningsAsErrors and the PublicAPI analyzers apply.
- The package references only `Microsoft.Extensions.DataIngestion`, which brings in `.Abstractions`; Core is unchanged.
- The preview APIs carry the `MEDI001` experimental diagnostic. Suppress it in the adapter csproj and the tests only, never for consumers.

## Spike findings (10.11.1-preview, run against Markdig's `MarkdownReader`)

- **Document shape:**
  - `IngestionDocument.Sections` holds one root section.
  - Lists become nested sections of paragraphs.
  - Fenced code, indented code and HTML blocks are **dropped** by Microsoft's MarkdownReader.
  - A paragraph containing an image becomes an `IngestionDocumentImage` whose `GetMarkdown()` still holds the paragraph's text.
  - A paragraph after `---` becomes an `IngestionDocumentFooter`.
  - `Header.Level` can be null.
- **Reader input handling:**
  - An empty stream throws `ArgumentException`; whitespace-only input gives zero elements.
  - The reader leaves the stream open and accepts a null media type.
- **`HeaderChunker`'s context:**
  - `Context` is the chain of heading markdown joined with `" "` (`"# Handbook ## Leave"`), and each chunk's `Content` starts with `Context + "\n"`.
  - Parsing `Context` back is ambiguous: a heading `# C# ## guide` breaks a split on `" ## "`.
- **Other chunkers:**
  - `DocumentTokenChunker` has an empty context and concatenates element markdown with no separator.
  - `SectionChunker` gives the first heading as context for every chunk.
- **What chunks lose:** they carry no page numbers and no element metadata.
- **Options:** `IngestionChunkerOptions` throws `ArgumentOutOfRangeException` when `OverlapTokens >= MaxTokensPerChunk`.

## Review Focus

1. **Empty, whitespace-only and non-seekable empty input:** the reader returns an empty document, so the Ingestor gives its usual `EmptyDocument` warning; it never throws `ArgumentException`.
2. **A wrapped reader that throws:** this becomes `DocumentReadException`, which is permanent in the Hosting queue. `OperationCanceledException` passes through unchanged.
3. **Headings containing `#` or repeated heading text:** a chunk's `HeadingPath` is still exact, found by lookup and never by parsing.
4. **Images:**
   - With bytes and `ExtractImages` on, an image becomes an OCR candidate, and its alt text, if any, a paragraph.
   - Without bytes, its markdown is kept, so the surrounding paragraph text isn't lost.
   - With `ExtractImages` off, there are no candidates.
5. **Tiny `MaxTokens` or a high `OverlapRatio`:** `CreateOptions` keeps `OverlapTokens < MaxTokensPerChunk`, so the chunker is never handed options it throws on.

---

### Task 1: Package and `DataIngestionReader`

**Files:**
- Create:
  - `src/RagDotNet.DataIngestion/RagDotNet.DataIngestion.csproj`, copied from Lucene's: the preview MinVer target is renamed `DataIngestionPreviewVersion`, with `NoWarn` MEDI001 and `InternalsVisibleTo` the tests.
  - `README.md` (a stub until Task 3)
  - `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`
  - `DataIngestionReader.cs`
  - `tests/RagDotNet.DataIngestion.Tests/RagDotNet.DataIngestion.Tests.csproj`, copied from the Hosting tests: references Markdig DataIngestion and links the Core fakes.
  - `tests/RagDotNet.DataIngestion.Tests/DataIngestionReaderTests.cs`
- Modify:
  - `Directory.Packages.props`: add `Microsoft.Extensions.DataIngestion` and `Microsoft.Extensions.DataIngestion.Markdig` at `10.11.1-preview.1.26569.2`.
  - `RagDotNet.slnx`: add both projects.

**Interfaces:**
- Produces: `public sealed class DataIngestionReader : IDocumentReader`
  - `DataIngestionReader(IngestionDocumentReader reader, IEnumerable<string> fileExtensions, IEnumerable<string>? contentTypes = null)`.
    - Extensions are given with their dot (`".md"`).
    - An empty extension list with no content types throws `ArgumentException`.
  - `CanRead(contentType, fileName)` is true when either matches:
    - the content type, case-insensitively, ignoring `;` parameters;
    - the file name's extension, case-insensitively.

**Mapping** (walk the sections in order, descending into nested sections; the heading stack follows Core's `MarkdownReader`):

| Element | Block |
|---|---|
| `IngestionDocumentHeader` | `Heading`, with text `GetMarkdown()`. Level is `Level ?? 1`; pop levels ≥ it and push `Text` (or the markdown with its leading `#`s and spaces removed when `Text` is empty). `HeadingPath` includes itself. The first level-1 heading is the `Title`. |
| `IngestionDocumentTable` | `Table`, `GetMarkdown()` |
| `IngestionDocumentImage` with `Content` | When `context.ExtractImages`, an `OcrCandidate(Image, InsertAt: blocks.Count, PageNumber, new ImageInput(Content, MediaType ?? "application/octet-stream"))`. A `Paragraph` of `AlternativeText` when it isn't blank. |
| `IngestionDocumentImage` without `Content` | `Paragraph`, `GetMarkdown()` |
| Paragraph, footer, anything else | `Paragraph`, `GetMarkdown()`, falling back to `Text` |

Every block carries the element's `PageNumber`. Blank text (after `Trim()`) is skipped.

**Errors:**
- A seekable stream with zero length returns `new ParsedDocument([])` without calling the reader.
- An `ArgumentException` from the reader on a non-seekable stream is wrapped like any other exception.
- Any exception except `OperationCanceledException` becomes `new DocumentReadException(context.FileName, $"{e.GetType().Name}: {e.Message}", e)`.

**The reader call:** the identifier is `context.FileName` and the media type is `context.ContentType ?? ""`.

- [ ] **Step 1: Write the failing tests** in `DataIngestionReaderTests`. They use Microsoft's `MarkdownReader` unless they say otherwise; a `FakeReader : IngestionDocumentReader` returns a hand-built document or throws.
  - `CanRead_matches_extension_or_content_type`:
    - `.MD`, `"text/markdown; charset=utf-8"` → true
    - `.txt` with a null content type → false
  - `Constructor_requires_an_extension_or_content_type`: `ArgumentException`.
  - `Headings_build_the_path_and_title`, on the spike's handbook markdown:
    - the `Sick leave` paragraph has the path `["Handbook","Leave","Sick leave"]`;
    - the `## Expenses` heading block has `["Handbook","Expenses"]`;
    - `Title == "Handbook"`.
  - `Heading_text_with_hashes_is_kept`: `# C# ## guide` gives the path `["C# ## guide"]`.
  - `Tables_are_table_blocks`: `Kind == Table`, and the text contains `| 1 | 2 |`.
  - `Inline_image_keeps_paragraph_text`: the block text contains `Body` and `more.`.
  - `Image_with_bytes_becomes_ocr_candidate_and_alt_text`:
    - FakeReader, an image with `Content = [1,2,3]`, `MediaType = "image/png"`, `AlternativeText = "chart"`, `PageNumber = 4`;
    - one candidate: `Kind == Image`, `InsertAt == 0`, `PageNumber == 4`, image `MediaType == "image/png"`;
    - one `Paragraph` block, `"chart"`.
  - `ExtractImages_off_gives_no_candidates`: the same input with `context with { ExtractImages = false }` gives no candidates.
  - `Page_numbers_are_kept`: FakeReader paragraphs on pages 1 and 2 → `PageNumber` 1 and 2.
  - `Null_header_level_counts_as_level_one`: FakeReader → the path is `["Heading"]`.
  - `Empty_and_whitespace_input_give_an_empty_document`, a theory over `""` and `"  \n\n "`: `Blocks` is empty and nothing throws.
  - `Reader_errors_become_DocumentReadException`:
    - FakeReader throws `InvalidDataException("bad")`;
    - the result is a `DocumentReadException` whose message contains `bad`, with `InnerException` of that type.
  - `Cancellation_passes_through`: FakeReader throws `OperationCanceledException` → `OperationCanceledException`.

- [ ] **Step 2: Run them and check they fail.**
  - Run: `dn.sh test --project tests/RagDotNet.DataIngestion.Tests -f net8.0`
  - Expected: a compile failure, because `DataIngestionReader` doesn't exist.

- [ ] **Step 3: Implement `DataIngestionReader`** with the mapping above.
  - Add the PublicAPI lines using `apiadd.py`.

- [ ] **Step 4: Run them and check they pass.**
  - Run the same command for net8.0 and net10.0.
  - Expected: all pass.

- [ ] **Step 5: Commit.** `feat(dataingestion): DataIngestionReader adapts Microsoft.Extensions.DataIngestion readers`

### Task 2: `DataIngestionChunker`

**Files:**
- Create: `src/RagDotNet.DataIngestion/DataIngestionChunker.cs`
- Test: `tests/RagDotNet.DataIngestion.Tests/DataIngestionChunkerTests.cs`

**Interfaces:**
- Consumes: Core's `IChunker`, `ParsedDocument`, `Chunk` and `ChunkingOptions`.
- Produces: `public sealed class DataIngestionChunker : IChunker`
  - **`DataIngestionChunker(string name, string version, Func<ChunkingOptions, IngestionChunker<string>> createChunker)`**
    - A blank name or version throws `ArgumentException`.
    - `createChunker` is called on every `ChunkAsync`, so the chunk size follows the app's `ChunkingOptions`.
  - **`public static IngestionChunkerOptions CreateOptions(ChunkingOptions options, Tokenizer? tokenizer = null)`**
    - `MaxTokensPerChunk = options.MaxTokens`
    - `OverlapTokens = Math.Min((int)(options.MaxTokens * options.OverlapRatio), options.MaxTokens - 1)`, at least 0
    - The tokenizer defaults to `TiktokenTokenizer.CreateForEncoding("cl100k_base")`, created once and cached in a static.

**Building the Microsoft document:**
- It has one root `IngestionDocumentSection`, and the identifier is `document.Title ?? "document"`.
- A `Heading` block becomes `new IngestionDocumentHeader(block.Text) { Level = block.HeadingPath.Count, Text = block.HeadingPath[^1], PageNumber }`.
- Every other block becomes `new IngestionDocumentParagraph(block.Text) { Text = block.Text, PageNumber }`.

**Mapping chunks back:**
- **The heading-path lookup:**
  - While building, record for each heading block the key `string.Join(" ", <markdown of each heading on its path, outermost first>)` → its `HeadingPath`.
  - Keep a stack of `(level, markdown)` alongside the paths.
  - The first heading with a given key wins.
- **For each `IngestionChunk<string>`:**
  - `HeadingPath` = `lookup[chunk.Context]` if present, otherwise `[]`.
  - `Text` = `Content` with a leading `Context + "\n"` removed when `Context` isn't empty and `Content` starts with it, then `Trim()`.
  - The chunk is `new Chunk(i, text, path, PageStart: null, PageEnd: null, TokenCount: 0)`. Core renumbers chunks, drops blank ones, splits oversized ones and recounts tokens.
- **Errors:** an exception from Microsoft's chunker (except cancellation) propagates unchanged; Core's ingestion already reports chunker failures.

- [ ] **Step 1: Write the failing tests:**
  - `Header_chunker_chunks_carry_the_heading_path_without_the_prefix`:
    - Core's `MarkdownReader` output for the handbook markdown, chunked through `o => new HeaderChunker(DataIngestionChunker.CreateOptions(o))`;
    - the chunk containing `Tell HR` has `HeadingPath == ["Handbook","Leave","Sick leave"]`, and its `Text` doesn't start with `#`.
  - `Heading_path_survives_hashes_in_headings`:
    - `# C# ## guide` then `## Part #2`, each with a paragraph;
    - the second paragraph's chunk path is `["C# ## guide","Part #2"]`.
  - `Unknown_context_gives_an_empty_path`: with `DocumentTokenChunker`, every chunk has an empty `HeadingPath` and non-empty text.
  - `Chunk_size_follows_ChunkingOptions`:
    - `MaxTokens = 50` on a 200-word paragraph;
    - more than one chunk, each within 60 tokens by cl100k.
  - `CreateOptions_keeps_overlap_below_max`:
    - `(MaxTokens 1, OverlapRatio 0.5)` → overlap 0
    - `(10, 0.99)` → 9
    - `(512, 0.1)` → 51
  - `Name_and_version_are_required`: a blank name or version → `ArgumentException`.

- [ ] **Step 2: Run them and check they fail.** Expected: a compile failure, because `DataIngestionChunker` doesn't exist.

- [ ] **Step 3: Implement `DataIngestionChunker`** as described above, and add the PublicAPI lines.

- [ ] **Step 4: Run the whole test project on net8.0 and net10.0.** Expected: all pass.

- [ ] **Step 5: Commit.** `feat(dataingestion): DataIngestionChunker adapts Microsoft.Extensions.DataIngestion chunkers`

### Task 3: Registration, end to end, docs, CI

**Files:**
- Create:
  - `src/RagDotNet.DataIngestion/DataIngestionBuilderExtensions.cs`
  - `docs/dataingestion.md`
  - `tests/RagDotNet.DataIngestion.Tests/EndToEndTests.cs`
- Modify:
  - `src/RagDotNet.DataIngestion/README.md`
  - Root `README.md`: the status row "Microsoft.Extensions.DataIngestion adapters" becomes "Works ([dataingestion](docs/dataingestion.md))", and a short section follows the Background ingestion one.
  - `.github/workflows/ci.yml`: a "Test DataIngestion adapters (net8.0)" step after the Hosting step in both the Linux and Windows jobs.

**Interfaces:**
- Produces, on `IRagBuilder`:
  - **`AddDataIngestionReader(Func<IServiceProvider, IngestionDocumentReader> createReader, IEnumerable<string> fileExtensions, IEnumerable<string>? contentTypes = null)`** registers a singleton `IDocumentReader`. App readers come before Core's built-ins, so it overrides them for the same extension.
  - **`AddDataIngestionChunker(string name, string version, Func<IServiceProvider, ChunkingOptions, IngestionChunker<string>> createChunker)`** registers a singleton `IChunker`.
  - Both return the builder. Arguments are validated eagerly, throwing `ArgumentException` or `ArgumentNullException` at the call.

- [ ] **Step 1: Write the failing tests:**
  - `Registered_reader_and_chunker_ingest_and_retrieve`. This is also the docs example:
    - DI with `AddRag(...)` (fakes, InMemory store);
    - `.AddDataIngestionReader(_ => new MarkdownReader(), [".md"], ["text/markdown"])`;
    - `.AddDataIngestionChunker("ms-header", "1", (_, o) => new HeaderChunker(DataIngestionChunker.CreateOptions(o)))`;
    - `ChunkingOptions.Chunker = "ms-header"`.
    - Ingest the handbook `.md`. `RetrieveAsync("Tell HR")` returns a hit whose `HeadingPath` ends with `"Sick leave"`.
  - `Reader_overrides_the_builtin_for_its_extension`:
    - a FakeReader registered for `.md` that returns one paragraph `"from-ms"`;
    - after ingesting, retrieval finds `"from-ms"`.
  - `Registration_validates_arguments`: a null factory → `ArgumentNullException`; a blank name → `ArgumentException`.

- [ ] **Step 2: Run them and check they fail.** Expected: a compile failure, because the extensions don't exist.

- [ ] **Step 3: Implement the extensions.** Then write the docs:
  - **`docs/dataingestion.md`** covers:
    - setup (the example above);
    - the mapping table from Task 1;
    - what Microsoft's MarkdownReader drops (code and HTML blocks), so RagDotNet's own Markdown reader stays the default;
    - images and OCR;
    - chunk heading paths, which only `HeaderChunker` gives;
    - no page numbers on chunks;
    - that the chunk size follows `ChunkingOptions`;
    - that the package is a preview that tracks Microsoft's preview versions.
  - **The package README** gets a summary.

- [ ] **Step 4: Run the whole test project on net8.0 and net10.0, then every other test project once on net8.0.** Expected: all pass, and the other projects' totals are unchanged.

- [ ] **Step 5: Commit.** `feat(dataingestion): registration helpers; docs: DataIngestion adapters`
