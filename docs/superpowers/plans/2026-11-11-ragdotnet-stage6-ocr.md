# RagDotNet Stage 6: OCR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the stage 5 `PageWithoutText` and `ImageNotRead` warnings into searchable, citable text whenever an app registers an OCR provider. This covers a dependency-free OCR contract in Core, a PDF page renderer package, a Tesseract package and a scanned-document check in the evaluation harness.

**Architecture:** Readers stop emitting the two warnings. Instead they return *OCR candidates*: scanned pages, and embedded images with their bytes, each with an insert position. An internal `OcrStage` in Core wraps reading during ingest. It does the following:
- calls whichever providers are registered: a per-image provider, a whole-document provider, and page renderers;
- inserts the recognised text as paragraph blocks at the candidates' positions;
- produces every warning, including the exact stage 5 warnings when no provider is registered.

An OCR fingerprint, appended to the stored chunking hash only for documents that had candidates, makes those documents re-read when the OCR setup changes. Other documents stay `Unchanged`.

**Tech Stack:**
- .NET 10 SDK; Core targets net8.0 and net10.0.
- PdfPig 0.1.16, DocumentFormat.OpenXml 3.5.1, AngleSharp 1.10.0.
- New: TesseractOCR 5.5.2 and PDFtoImage 5.4.0.
- xUnit v3. Docker (`ragdotnet-sdk`) for all builds and tests.

**Spec:** `docs/superpowers/specs/2026-11-11-ragdotnet-stage6-ocr-design.md`. It refines §5.11 of `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`; where the two differ, the stage 6 spec wins.

## Global Constraints

**Dependencies**
- `RagDotNet.Core` gains no package dependency. It still targets net8.0 and net10.0.
- `RagDotNet.Readers.Pdf` gains no new dependency. It stays PdfPig only, with no native libraries.
- New src projects:
  - `src/RagDotNet.Readers.Pdf.Rendering` (PDFtoImage 5.4.0, references Core only);
  - `src/RagDotNet.Ocr.Tesseract` (TesseractOCR 5.5.2, references Core and the rendering project).
  - Both target net8.0 and net10.0, have PublicAPI files, treat warnings as errors and use central package versions.
- Registration follows stage 5: each new package has
  - a `Microsoft.Extensions.DependencyInjection.<Name>Extensions` class with `Add…(this IRagBuilder)`;
  - a `RagDotNet.Configuration.<Name>BuilderExtensions` class with `Add…(this RagPipelineBuilder)`;
  - both classes in one `*Extensions.cs` file, using file-scoped namespace blocks.

**Defaults from the specs**
- Page render DPI 300.
- Images smaller than 64×64 px are skipped.
- Identical images within a document are recognised once.
- Language data is not bundled; the default language is `eng` and the default data folder is `./tessdata`.
- Fill missing pages is the default mode for a whole-document provider.

**My calls**
- The default minimum confidence is 0.6, on a 0..1 scale.
- An identical image's text is inserted only at its first position.
- With `OcrEmbeddedImages = false`, skipped images give no warning, because the app turned them off on purpose.

**No-provider behaviour**
- With no OCR provider registered, ingest output, including warning codes, messages and page numbers, is byte-for-byte what stage 5 produced.
- The stage 5 messages are:
  - `Page {n} has no extractable text; register an OCR provider to read it.`
  - `Page {n} has {k} image(s) that were not read; register an OCR provider to read them.`
  - `{n} embedded image(s) were not read; register an OCR provider to read them.`

**Ingest failures**
- One failing image or page becomes an `OcrFailed` warning; ingest continues.
- `OcrUnavailableException` from a provider is not caught; the ingest fails with it.

**Environment**
- Windows Smart App Control blocks freshly built binaries. Every `dotnet build/test/run` runs in Docker through `docker/sdk/run.ps1` (Task 1).

## Review Focus

1. **Two ingests running at once share one Tesseract provider.** Both must get their own correct text, with no crash; the TesseractOCR engine is not thread-safe. *Test in Task 7.*
2. **Cancelling an ingest during OCR** throws `OperationCanceledException` and writes no chunks or manifest. It does not become an `OcrFailed` warning. *Test in Task 4.*
3. **An embedded image whose bytes are not a decodable image** (corrupt PNG in a DOCX, unsupported PDF image filter) gives one `OcrFailed` warning, and the rest of the document is indexed. *Test in Tasks 4 and 7.*
4. **A whole-document provider that returns blocks without page numbers, or for pages that had text, in fill mode.** Those blocks are ignored; only pages that lacked text are filled. *Test in Task 4.*
5. **A PDF page that PDFium cannot render** becomes an `OcrFailed` warning for that page, not a failed ingest. *Test in Task 4 (fake renderer that throws).*

---

### Task 1: Docker image with Tesseract, and the TesseractOCR spike (go/no-go gate)

**Files:**
- Create: `docker/sdk/Dockerfile`, `docker/sdk/run.ps1`, `docker/sdk/README.md`
- Scratch (not committed): a throwaway console app in the scratchpad

**Interfaces:**
- Produces:
  - image tag `ragdotnet-sdk:ocr`;
  - `docker/sdk/run.ps1 <dotnet args…>`, which runs `dotnet <args>` in that image with the repo at `/work`, NuGet volume `ragdotnet-nuget` and `--add-host host.docker.internal:host-gateway`;
  - a recorded Linux native-loading recipe for TesseractOCR, which Tasks 7 and 9 consume.

- [ ] **Step 1: Write `docker/sdk/Dockerfile`**
  - Rebuild it from `docker image history ragdotnet-sdk --no-trunc`: SDK 10.0.403, plus the .NET 8 runtime copied in, plus `curl git libatomic1 wget`.
  - Add `tesseract-ocr tesseract-ocr-eng libtesseract-dev libleptonica-dev`.
  - Record the distro (`/etc/os-release`), `tesseract --version` and the tessdata path in a comment.

  `run.ps1` takes its arguments as an array and splats them, because an inline `--` is swallowed by PowerShell.
- [ ] **Step 2: Build the image**
  Run: `docker build -t ragdotnet-sdk:ocr docker/sdk`
  Expected: success; `docker run --rm ragdotnet-sdk:ocr tesseract --version` prints 5.x.
- [ ] **Step 3: Spike.** Write a scratch console app (net10.0) referencing TesseractOCR 5.5.2 and PDFtoImage 5.4.0. It renders a one-line PDF ("Quill sync keeps notes current") to PNG at 300 DPI, then OCRs it from memory with `eng`.
  - TesseractOCR on Linux looks for `x64/libleptonica-1.85.0.dll.so` and `x64/libtesseract55.dll.so` next to the app (its Docker wiki), so find the least invasive way to satisfy that from the *package*. In order:
    - (a) the loader honours a search path or environment variable;
    - (b) a `NativeLibrary`/`AssemblyLoadContext` resolver set by our package before first use;
    - (c) our package creates the links in a writable app-local folder at startup.
  - Also confirm PDFtoImage's DPI parameter name (`RenderOptions(Dpi: …)` or equivalent) and an in-memory image load API (`Pix.Image.LoadFromMemory` or equivalent).
- [ ] **Step 4: Gate.** If the console prints text containing `Quill sync keeps notes current` with confidence > 0.6, record the recipe as a ledger `Ruling:`: the exact mechanism, the distro, and the version pairs. If no option from Step 3 works without the app author creating files by hand, **stop and bring the options back to the owner**, as the spec's Risks section requires. Do not start Task 7 until this passes.
- [ ] **Step 5: Point the run script at the new image** and prove the existing suite still passes in it.
  Run: `pwsh docker/sdk/run.ps1 test RagDotNet.slnx`
  Expected: 1102 total, 1101 passed, 1 skipped.
- [ ] **Step 6: Commit** `docker/sdk/*` with message `build: Docker SDK image with Tesseract for building and testing`.

### Task 2: OCR contract, options and registration in Core

**Files:**
- Create in `src/RagDotNet.Core/Ocr/`: `ImageInput.cs`, `OcrResult.cs`, `IOcrProvider.cs`, `IDocumentOcrProvider.cs`, `DocumentOcrResult.cs`, `IPageRenderer.cs`, `OcrUnavailableException.cs`, `OcrOptions.cs`, `DocumentOcrMode.cs`
- Create: `src/RagDotNet.Core/Configuration/RagOcrBuilderExtensions.cs`
- Modify:
  - `RagOptions.cs`, `RagOptionsValidator.cs`, `RagPipelineBuilder.cs`, `RagEngineFactory.cs`, `RagServiceCollectionExtensions.cs` (factory call);
  - `Pipeline/RagEngine.cs` (holds the providers);
  - `Documents/IngestWarningCodes.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/RagDotNet.Core.Tests/Ocr/OcrRegistrationTests.cs`

**Interfaces:**
- Produces (namespace `RagDotNet.Ocr` unless noted):
  - **Image and result types:**
    - `sealed record ImageInput(ReadOnlyMemory<byte> Bytes, string MediaType, int? Width = null, int? Height = null)`; Width and Height are in pixels when known.
    - `sealed record OcrResult(string Text, double Confidence)`, with Confidence from 0 to 1.
  - **Per-image provider:** `interface IOcrProvider { string Id { get; } Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken); }`. `Id` names the engine and its settings, for example `tesseract/5.5/eng`, and goes into the OCR fingerprint.
  - **Whole-document provider:** `interface IDocumentOcrProvider { string Id { get; } bool CanProcess(string? contentType, string fileName); Task<DocumentOcrResult> ProcessAsync(ReadOnlyMemory<byte> document, DocumentReadContext context, CancellationToken cancellationToken); }`
  - `sealed record DocumentOcrResult(IReadOnlyList<DocumentBlock> Blocks)` with `IReadOnlyList<IngestWarning> Warnings { get; init; } = []`. A plain-text provider returns one paragraph per page, with `PageNumber` set.
  - **Page renderer:** `interface IPageRenderer { bool CanRender(string? contentType, string fileName); Task<ImageInput> RenderPageAsync(ReadOnlyMemory<byte> document, int pageNumber, int dpi, CancellationToken cancellationToken); }`. `pageNumber` is 1-based.
  - **Exception:** `sealed class OcrUnavailableException(string message, Exception? innerException = null) : Exception`. A provider throws it when it cannot work at all.
  - **Mode:** `enum DocumentOcrMode { FillMissingPages, ReplaceReader }`
  - **Options:** `sealed class OcrOptions`, with properties:
    - `bool OcrEmbeddedImages = true`
    - `int MinImageSize = 64`
    - `double MinConfidence = 0.6`
    - `int PageDpi = 300`
    - `DocumentOcrMode DocumentProviderMode = FillMissingPages`

    All are get/set, so configuration binding works.
  - `RagOptions.Ocr { get; set; } = new()`
  - **Warning codes:** `IngestWarningCodes.OcrFailed = "OcrFailed"` and `IngestWarningCodes.OcrLowConfidence = "OcrLowConfidence"`
  - **Pipeline builder:** `RagPipelineBuilder.UseOcrProvider(IOcrProvider)`, `UseDocumentOcrProvider(IDocumentOcrProvider)`, `AddPageRenderer(IPageRenderer)`. All return the builder and throw once frozen.
  - **DI:** in namespace `Microsoft.Extensions.DependencyInjection`, `RagOcrBuilderExtensions` with `AddOcrProvider<T>()`, `AddDocumentOcrProvider<T>()` and `AddPageRenderer<T>()` on `IRagBuilder`.
    - The providers are singletons; the last registration wins (`Replace`).
    - Renderers are an enumerable (`TryAddEnumerable`).
  - **Engine wiring:** `RagEngineFactory.Create(…, IOcrProvider? ocr, IDocumentOcrProvider? documentOcr, IEnumerable<IPageRenderer> renderers)`. The engine exposes `internal OcrStage Ocr` (Task 3 creates `OcrStage`; in this task the engine just stores the three values).

- [ ] **Step 1: Write the failing tests** in `OcrRegistrationTests`:

```csharp
[Fact] public void Ocr_options_have_the_spec_defaults()
{ var o = new RagOptions().Ocr; Assert.True(o.OcrEmbeddedImages); Assert.Equal(64, o.MinImageSize); Assert.Equal(0.6, o.MinConfidence); Assert.Equal(300, o.PageDpi); Assert.Equal(DocumentOcrMode.FillMissingPages, o.DocumentProviderMode); }

[Theory] [InlineData(-1, 0.6, 300)] [InlineData(64, 1.5, 300)] [InlineData(64, 0.6, 0)]
public void Invalid_ocr_options_fail_validation(int minSize, double minConfidence, int dpi) // RagOptionsValidator.Validate(...).Failed

[Fact] public void Di_registers_the_last_ocr_provider_and_every_renderer()
// AddRag().AddOcrProvider<FakeA>().AddOcrProvider<FakeB>().AddPageRenderer<R1>().AddPageRenderer<R2>(): engine sees FakeB and [R1, R2]

[Fact] public void Builder_ocr_methods_throw_after_Build()
```

- [ ] **Step 2: Run them.** Run: `pwsh docker/sdk/run.ps1 test --project tests/RagDotNet.Core.Tests -f net10.0`. Expected: compile errors for the missing types.
- [ ] **Step 3: Implement** the types, options, validation rules, warning codes, registrations and the factory/engine plumbing listed in Interfaces:
  - `MinImageSize >= 0`;
  - `0 <= MinConfidence <= 1`;
  - `PageDpi` between 72 and 1200.
- [ ] **Step 4: Run Core tests.** Expected: all pass, PublicAPI analyzers clean.
- [ ] **Step 5: Commit** with message `feat(ocr): OCR contract, options and registration in Core`.

### Task 3: Readers return OCR candidates; Core reproduces the stage 5 warnings

**Files:**
- Create: `src/RagDotNet.Core/Documents/OcrCandidate.cs`, `src/RagDotNet.Core/Ocr/OcrStage.cs` (no-provider path only), `src/RagDotNet.Core/Ocr/ImageHeader.cs`
- Modify:
  - `Documents/ParsedDocument.cs`, `Ingestion/Ingestor.cs` (read through `OcrStage`);
  - the three readers: `PdfReader.cs`, `DocxReader.cs`, `HtmlReader.cs`;
  - reader tests that assert `Warnings` now assert `OcrCandidates`.
- Test:
  - `tests/RagDotNet.Core.Tests/Ocr/OcrStageNoProviderTests.cs`, `tests/RagDotNet.Core.Tests/Ocr/ImageHeaderTests.cs`;
  - `tests/RagDotNet.Readers.Tests/OcrCandidateTests.cs`;
  - `tests/RagDotNet.Readers.Tests/Stage5WarningParityTests.cs`.

**Interfaces:**
- Consumes: Task 2 types.
- Produces:
  - **Candidate type:** `sealed record OcrCandidate(OcrCandidateKind Kind, int InsertAt, int? PageNumber, ImageInput? Image = null)` in `RagDotNet.Documents`, with `bool HasTextLayer { get; init; }` and `bool IsPageBackground { get; init; }`.
    - **`InsertAt`:** the index into `ParsedDocument.Blocks` *before which* recognised text goes, so `Blocks.Count` means at the end.
    - **`Image`:** null for `ScannedPage`. It is also null for an `Image` whose bytes could not be extracted.
  - `enum OcrCandidateKind { ScannedPage, Image }`
  - `ParsedDocument.OcrCandidates { get; init; } = []`
  - **`OcrStage`:**
    - `internal sealed class OcrStage(IOcrProvider? images, IDocumentOcrProvider? documents, IReadOnlyList<IPageRenderer> renderers, OcrOptions options)`
    - `Task<ParsedDocument> ReadAsync(IDocumentReader reader, byte[] bytes, DocumentReadContext context, CancellationToken ct)`
    - `string Fingerprint { get; }`, which is `"none"` when no provider is registered.
    - The returned document has candidates resolved: blocks inserted and warnings appended. `OcrCandidates` is kept on it, so the ingestor can tell the document had some.
  - `internal static class ImageHeader { static bool TryGetSize(ReadOnlySpan<byte> bytes, out int width, out int height); }` covers PNG, JPEG (SOF0–SOF15 except DHT/JPG/DAC) and GIF.

**Candidate rules, per reader:**
- **PDF:**
  - **Page with no letters:** one `ScannedPage`, `HasTextLayer = false`, `InsertAt` = the index where that page's blocks would start. No image candidates for that page, as in stage 5.
  - **Page with text:** one `Image` per `page.GetImages()`.
    - Bytes come from `TryGetPng` (`image/png`), else `RawBytes` when the filter is DCTDecode (`image/jpeg`), else `Image = null`.
    - Width and Height are `WidthInSamples` and `HeightInSamples`.
    - `IsPageBackground` = image bounds cover ≥ 80% of the page area.
    - `InsertAt` = after the last block on that page whose top is above the image's top. Track positions on the final block list, after `MergeNumberedRuns` and the empty-text filter.
  - **Page with text and a background image whose non-whitespace letters number ≤ 30** (a stamp over a scan): also one `ScannedPage` with `HasTextLayer = true`.
- **DOCX:** one `Image` per `a:blip` or VML `v:imagedata`.
  - Bytes come from the related `ImagePart`, with its content type.
  - Width and Height come from the `wp:extent` EMUs ÷ 9525, when present.
  - `InsertAt` = right after the blocks of the paragraph that holds the image.
- **HTML:** one `Image` per `img` with a `data:` URI inside the content root.
  - The bytes are the decoded base64 and the media type comes from the URI.
  - Width and Height come from numeric `width`/`height` attributes.
  - `InsertAt` = the walker's block count at that point. Remote images get no candidate, as in stage 5.

**No-provider warnings in `OcrStage`:** derived from the candidates, in candidate order. They must be identical to stage 5:
- each `ScannedPage` with `!HasTextLayer` gives `PageWithoutText` for its page;
- `Image` candidates with a page number give one `ImageNotRead` per page, using the per-page message and count;
- `Image` candidates without a page number give one `ImageNotRead` per document, using the per-document message.

- [ ] **Step 1: Write the failing tests:**
  - **`OcrCandidateTests`:**
    - `Pdf_scanned_page_is_a_ScannedPage_candidate_at_its_page_position`, against `TestAssets/pdf/sample.pdf`, where page 2 is image-only;
    - `Pdf_image_on_a_text_page_is_placed_between_the_paragraphs_around_it`;
    - `Pdf_full_page_image_under_text_is_a_page_background`;
    - `Pdf_stamp_over_a_scan_is_also_a_ScannedPage_with_a_text_layer`;
    - `Docx_image_is_a_candidate_after_its_paragraph_with_part_bytes_and_extent_size`;
    - `Html_data_uri_image_is_a_candidate_and_remote_images_are_not`.
  - **`Stage5WarningParityTests`:** build a pipeline with all three readers and no OCR, ingest the three samples, and assert the exact stage 5 warning list. Copy the expected messages from the current reader tests before changing them.
  - **`OcrStageNoProviderTests`:**
    - `Fingerprint_is_none_without_providers`;
    - `Background_and_stamp_candidates_add_no_extra_warnings`.
  - **`ImageHeaderTests`:** PNG, JPEG and GIF sizes, plus garbage → false.
- [ ] **Step 2: Run** Core and Readers tests in Docker. Expected: compile errors or failures for the new behaviour.
- [ ] **Step 3: Implement** the candidates, `ImageHeader`, and `OcrStage`'s no-provider path. Make `Ingestor` read through `engine.Ocr.ReadAsync`, and remove the readers' own `PageWithoutText`/`ImageNotRead` warnings.
- [ ] **Step 4: Run** the full suite in Docker. Expected: all pass; parity tests green.
- [ ] **Step 5: Commit** with message `feat(ocr): readers return OCR candidates; warnings come from Core`.

### Task 4: OCR in ingest: per-image and whole-document providers

**Files:**
- Modify: `src/RagDotNet.Core/Ocr/OcrStage.cs`, `src/RagDotNet.Core/Diagnostics/RagTelemetry.cs` if a span name constant is needed
- Test: `tests/RagDotNet.Core.Tests/Ocr/OcrStageTests.cs`, using fake readers, providers and renderers defined in the test file

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces: the full `OcrStage.ReadAsync` behaviour below. `Fingerprint` is `string.Join("|", …)` of:
  - the per-image provider's `Id` or `-`;
  - the document provider's `Id` and mode, or `-`;
  - the renderers' full type names, sorted;
  - `OcrEmbeddedImages`, `MinImageSize` and `PageDpi`.

  `MinConfidence` is excluded, because it only changes warnings.

**Behaviour, in order:**
1. **Replace mode:** if a document provider is registered, the mode is `ReplaceReader`, and `CanProcess` is true, call it first.
   - **On success:** the result's blocks and warnings are the document, with no candidates.
   - **On failure** (any exception except `OperationCanceledException` and `OcrUnavailableException`): add `OcrFailed` ("The document OCR provider failed on '{file}'; read with the built-in reader instead: {message}") and continue with the reader.
2. Read with the reader.
3. **Fill mode:** if a document provider is registered, the mode is `FillMissingPages`, `CanProcess` is true, and there are `ScannedPage` candidates, call it once.
   - Take only blocks whose `PageNumber` is one of those pages, and insert them at those pages' `InsertAt`.
   - On failure, add one `OcrFailed` warning, and those pages fall through to step 4.
4. **Remaining `ScannedPage` candidates:**
   - **No per-image provider:** stage 5 `PageWithoutText` warning.
   - **Per-image provider but no renderer that `CanRender`:** `PageWithoutText` with the message `Page {n} has no extractable text; add RagDotNet.Readers.Pdf.Rendering (AddPdfPageRendering) so the OCR provider can read it.`
   - **Otherwise:** render at `PageDpi` and recognise.
5. **`Image` candidates:**
   - **Skipped:** when `OcrEmbeddedImages` is false (no warning), when `IsPageBackground`, and when the size from the candidate, or else from `ImageHeader`, is below `MinImageSize` in either dimension.
   - **Unknown size:** the image is read.
   - **No per-image provider:** stage 5 `ImageNotRead` warnings. If only a document provider is registered, their message ends `register a per-image OCR provider (IOcrProvider) to read them.` instead of `register an OCR provider to read them.`
   - **Duplicates:** grouped by SHA-256 of the bytes. Recognised once; text inserted at the first occurrence only.
   - **`Image == null`:** `OcrFailed` "Page {n}: an image could not be extracted for OCR." (or the same message without the page).
6. **Each recognition:**
   - Empty or whitespace text adds nothing.
   - Otherwise insert a `Paragraph` block with the candidate's `PageNumber` and the `HeadingPath` of `Blocks[InsertAt-1]`, or empty when `InsertAt == 0`.
   - Confidence below `MinConfidence` adds `OcrLowConfidence` "Page {n}: OCR text has low confidence ({c:0.00}); it was kept." (the page part is omitted without a page).
   - An exception adds `OcrFailed` "Page {n}: OCR failed: {message}" and continues.
   - `OperationCanceledException` and `OcrUnavailableException` propagate.
   - Insert in descending `InsertAt` order, so earlier positions stay valid.
7. **Telemetry:** wrap the work in an activity `rag.ocr` when any provider is registered.

- [ ] **Step 1: Write the failing tests:**
  - `Scanned_page_text_is_inserted_at_the_page_with_its_page_number_and_heading_path`
  - `Scanned_page_without_a_renderer_names_the_rendering_package`
  - `Embedded_image_text_is_inserted_at_its_position`
  - `Images_below_64px_are_skipped_using_header_size_when_the_reader_gave_none`
  - `Identical_images_are_recognised_once_and_inserted_once`
  - `Page_background_images_are_not_recognised`
  - `Embedded_images_off_reads_none_and_warns_none`
  - `Low_confidence_text_is_kept_with_a_warning`
  - `One_failing_image_is_a_warning_and_the_rest_is_read` (Review Focus 3)
  - `Renderer_failure_on_a_page_is_an_OcrFailed_warning` (Review Focus 5)
  - `OcrUnavailable_propagates`
  - `Cancellation_during_ocr_propagates_and_nothing_is_stored` (Review Focus 2): pipeline-level, with a provider that waits for the token; assert no manifest afterwards.
  - `Fill_mode_fills_only_scanned_pages_and_ignores_other_blocks` (Review Focus 4): the provider returns blocks for pages 1–3 and one block with a null page, page 2 is scanned, and only the page 2 blocks are used.
  - `Fill_mode_failure_falls_back_to_the_image_provider`
  - `Replace_mode_uses_the_provider_output_for_formats_it_claims`
  - `Replace_mode_failure_falls_back_to_the_reader_with_a_warning`
  - `Replace_mode_never_adds_formats_no_reader_handles`: pipeline-level. A provider claiming `.png` still gets `UnsupportedDocumentException`, because the reader is selected before `OcrStage` runs.
  - `Only_a_document_provider_leaves_images_as_ImageNotRead_naming_a_per_image_provider`
  - `Fingerprint_changes_with_provider_mode_and_options_but_not_min_confidence`
- [ ] **Step 2: Run them.** Expected: failures.
- [ ] **Step 3: Implement** the behaviour above in `OcrStage`.
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** with message `feat(ocr): run per-image and whole-document OCR during ingest`.

### Task 5: Re-read documents with unread items when the OCR setup changes

**Files:**
- Modify: `src/RagDotNet.Core/Ingestion/Ingestor.cs`
- Test: `tests/RagDotNet.Core.Tests/Ocr/OcrReingestTests.cs`

**Interfaces:**
- Consumes: `OcrStage.Fingerprint`, `ParsedDocument.OcrCandidates`.
- Produces: stored `ChunkingHash`, on both the manifest and the chunk records:
  - `chunkingHash` when the document had no candidates;
  - `chunkingHash + "+ocr:" + Fingerprint` when it had.

  The unchanged check passes when the stored value equals either `chunkingHash` or `chunkingHash + "+ocr:" + engine.Ocr.Fingerprint`. There is no schema change.

- [ ] **Step 1: Write the failing tests.** Each uses two pipelines over one in-memory store and a fake reader whose candidates can be switched:
  - `Document_with_a_scanned_page_is_reread_after_ocr_is_registered`: Updated, and the text is now found;
  - `Document_without_candidates_stays_Unchanged_after_ocr_is_registered`;
  - `Same_ocr_setup_is_Unchanged`;
  - `Removing_ocr_rereads_documents_that_were_read_with_it`;
  - `Without_ocr_the_stored_hash_equals_stage5`: equals `Hashing.ChunkingHash(...)` for a no-candidate document.
- [ ] **Step 2: Run them.** Expected: the first and fourth fail.
- [ ] **Step 3: Implement** in `Ingestor`.
- [ ] **Step 4: Run** the full suite. Expected: all pass.
- [ ] **Step 5: Commit** with message `feat(ocr): re-read documents with unread pages or images when OCR changes`.

### Task 6: `RagDotNet.Readers.Pdf.Rendering`

**Files:**
- Create: `src/RagDotNet.Readers.Pdf.Rendering/{RagDotNet.Readers.Pdf.Rendering.csproj, PdfPageRenderer.cs, PdfPageRenderingExtensions.cs, PublicAPI.Shipped.txt, PublicAPI.Unshipped.txt}`
- Create: `tests/RagDotNet.Ocr.Tests/{RagDotNet.Ocr.Tests.csproj, PdfPageRendererTests.cs}`. The project targets net8.0;net10.0 and references Readers.Pdf, Rendering and (from Task 7) Tesseract.
- Modify: `Directory.Packages.props` (`PDFtoImage` 5.4.0), `RagDotNet.slnx`

**Interfaces:**
- Produces:
  - `public sealed class PdfPageRenderer : IPageRenderer`. It renders PDF only and returns `image/png` with Width and Height set.
  - `AddPdfPageRendering(this IRagBuilder)` and `AddPdfPageRendering(this RagPipelineBuilder)`.

- [ ] **Step 1: Write the failing tests:**
  - `Renders_page_2_of_the_sample_at_300_dpi_as_png`: PNG signature, and width ≈ page width in points × 300 / 72 ± 2;
  - `CanRender_is_pdf_only`;
  - `Page_number_out_of_range_throws_ArgumentOutOfRangeException`;
  - `Pipeline_with_AddPdfPageRendering_and_a_fake_ocr_provider_indexes_the_scanned_page`.
- [ ] **Step 2: Run them.** Expected: compile errors.
- [ ] **Step 3: Implement** `PdfPageRenderer` with PDFtoImage, using the DPI API confirmed in the Task 1 spike. Run the CPU-bound render on `Task.Run`.
- [ ] **Step 4: Run** Ocr.Tests and the full suite. Expected: all pass.
- [ ] **Step 5: Commit** with message `feat(ocr): PDF page rendering package`.

### Task 7: `RagDotNet.Ocr.Tesseract`

**Files:**
- Create: `src/RagDotNet.Ocr.Tesseract/{csproj, TesseractOcrProvider.cs, TesseractOcrOptions.cs, TesseractNative.cs, TesseractOcrExtensions.cs, PublicAPI.*}`
- Create: `tests/RagDotNet.Ocr.Tests/TesseractOcrProviderTests.cs`, `tests/RagDotNet.Ocr.Tests/OcrTestEnvironment.cs`
- Modify: `Directory.Packages.props` (`TesseractOCR` 5.5.2)

**Interfaces:**
- Consumes: the Task 1 native-loading recipe; Task 6 `PdfPageRenderer`.
- Produces:
  - `public sealed class TesseractOcrOptions { string DataPath { get; set; } = "./tessdata"; string Languages { get; set; } = "eng"; }`. `Languages` uses Tesseract's `+` syntax, for example `eng+deu`.
  - `public sealed class TesseractOcrProvider : IOcrProvider, IDisposable`, constructed with `TesseractOcrOptions`.
    - The constructor creates the engine and throws `OcrUnavailableException` when:
      - the native library cannot load: the message names the OS install command (Linux `sudo apt install tesseract-ocr`, macOS `brew install tesseract`, Windows: the Visual C++ runtime);
      - `{DataPath}/{lang}.traineddata` is missing for any language: the message names the full path and https://github.com/tesseract-ocr/tessdata_fast.
    - `Id` is `tesseract/{Languages}`.
    - `RecognizeAsync` serialises calls on one engine with a `SemaphoreSlim` (Review Focus 1). It loads the image from memory and returns `page.Text` with `page.MeanConfidence`.
  - `AddTesseractOcr(this IRagBuilder, Action<TesseractOcrOptions>? configure = null)`:
    - registers options, with `ValidateOnStart` performing the same checks so a host fails at start;
    - registers the provider as a singleton `IOcrProvider`;
    - calls `AddPdfPageRendering()`.
  - `AddTesseractOcr(this RagPipelineBuilder, Action<TesseractOcrOptions>? configure = null)` constructs the provider immediately (it throws then) and adds `PdfPageRenderer`.
  - Test helper `OcrTestEnvironment.TessData`: `RAGDOTNET_TESSDATA`, else `/usr/share/tesseract-ocr/5/tessdata` (or the Task 1 path). When it is missing, tests call `Assert.Skip(...)`, unless `CI=true`, in which case they fail.

- [ ] **Step 1: Write the failing tests:**
  - `Reads_a_rendered_text_page`: render a one-page PDF ("Quill sync keeps notes current") with `PdfPageRenderer`; the text contains it and confidence > 0.6;
  - `Pipeline_with_AddTesseractOcr_finds_text_on_the_scanned_sample_page`: page 2 of `TestAssets/pdf/sample.pdf`, a citation with page 2, and no `PageWithoutText` warning;
  - `Missing_language_data_throws_OcrUnavailable_naming_the_path`;
  - `Di_host_fails_at_start_when_language_data_is_missing`;
  - `Concurrent_recognitions_each_get_their_own_text` (Review Focus 1): 8 parallel calls over two different images;
  - `Corrupt_image_bytes_throw_and_ingest_records_OcrFailed` (Review Focus 3);
  - `Id_names_the_languages`.

  Generate the scanned sample page's text expectations from what `TestAssetGen` drew on page 2. If page 2 has no drawn text, extend `TestAssetGen` to draw a known sentence onto that page's image and regenerate `sample.pdf`, as a ledger ruling.
- [ ] **Step 2: Run them.** Expected: compile errors.
- [ ] **Step 3: Implement** the provider, applying the Task 1 recipe in `TesseractNative.EnsureLoaded()`, which runs once, before the first engine.
- [ ] **Step 4: Run** Ocr.Tests and the full suite in Docker. Expected: all pass, none skipped.
- [ ] **Step 5: Commit** with message `feat(ocr): Tesseract OCR provider`.

### Task 8: Scanned check in the evaluation harness

**Files:**
- Modify:
  - `tests/RagDotNet.TestAssetGen/Program.cs`: new command `scanned`;
  - `tests/RagDotNet.Evaluation/Dataset/{Question.cs, DatasetLoader.cs}`;
  - `Running/EvaluationRunner.cs`, `Reporting/ReportWriter.cs`, `Cli/Options.cs`, `Program.cs`, `RagDotNet.Evaluation.csproj`;
  - `src/RagDotNet.Core/RagDotNet.Core.csproj`: `InternalsVisibleTo RagDotNet.Evaluation`, so the loader runs the same `OcrStage` as ingest.
- Create:
  - `tests/RagDotNet.Evaluation/Dataset/base/formats/scanned/*.pdf`, generated;
  - `tests/RagDotNet.Evaluation/Providers/CachingOcrProvider.cs`;
  - `tests/RagDotNet.Evaluation.Tests/ScannedFormatTests.cs`.

**Interfaces:**
- Consumes: `OcrStage`, `TesseractOcrProvider`, `PdfPageRenderer`.
- Produces:
  - **Format and files:** format `scanned` in `DatasetSet.Formats`. Its files are `formats/scanned/*.pdf`, using a format → extension map.
  - **Loader:** `DatasetLoader.LoadSetsAsync(root, formats, OcrSetup? ocr, ct)`, with `sealed record OcrSetup(IOcrProvider Provider, IPageRenderer Renderer)`. Loading `scanned` without an `OcrSetup` is a `DatasetValidationException`.
  - **Runner:** `EvaluationRunner(embedder, dimensions, chat, OcrSetup? ocr = null)` registers both with the pipeline.
  - **Cache:** `CachingOcrProvider(IOcrProvider inner)` caches by SHA-256 of the bytes. Every config in the matrix re-ingests, so this keeps the run to one OCR pass per page.
  - **CLI:** `--tessdata <dir>`, defaulting to `OcrTestEnvironment`'s path rule. When `scanned` is requested and Tesseract is unavailable, stderr gets the `OcrUnavailableException` message and the exit code is 1.
  - **Report:** for the `scanned` variant, after `- Unscorable questions: N`, the line `- Evidence intact after OCR: {total - N} of {total}`.
  - **Generator:** `TestAssetGen scanned <set dir>` renders each `formats/pdf/*.pdf` page with PDFtoImage at 200 DPI as JPEG quality 85. It writes a PDF of the same page size with only that image (PdfPig `AddJpeg`), so there is no text layer. If the total exceeds 8 MB, lower the quality and record a ruling.

- [ ] **Step 1: Write the failing tests:**
  - `Scanned_pdfs_have_no_text_layer`: PdfPig letters count is 0 on every page;
  - `Loading_scanned_without_ocr_is_a_dataset_error`;
  - `Scanned_variant_report_states_evidence_intact_after_ocr`: ReportWriter unit test, with fake numbers;
  - `Fake_run_over_scanned_scores_and_reports`: requires Tesseract; skips by the `OcrTestEnvironment` rule.
- [ ] **Step 2: Run them.** Expected: failures.
- [ ] **Step 3: Implement**, then generate the assets.
  Run: `pwsh docker/sdk/run.ps1 run --project tests/RagDotNet.TestAssetGen -- scanned tests/RagDotNet.Evaluation/Dataset/base`
- [ ] **Step 4: Run** the full suite. Expected: all pass. Also check that the fake md/pdf/docx/html report is unchanged from main: diff against a run made before this task.
- [ ] **Step 5: Real run.**
  Run: `pwsh docker/sdk/run.ps1 run --project tests/RagDotNet.Evaluation -c Release -- --embedder ollama --ollama-url http://host.docker.internal:11434 --formats md,scanned --output artifacts/scanned.md`
  Expected: the report includes `## base · scanned` with an `Evidence intact after OCR` line.
  - If the run is killed for low memory, ledger it and ask the owner to free memory. Do not publish numbers from a partial run.
- [ ] **Step 6: Commit** the code and assets with message `feat(eval): scanned check — base set as image-only PDFs through Tesseract`.

### Task 9: Docs and CI

**Files:**
- Create: `docs/ocr.md`
- Modify:
  - `docs/readers.md`: the Warnings section points to OCR;
  - `docs/evaluation.md`: a `scanned` row and a "Scanned check" section;
  - `docs/evaluation-results.md`: "## Scanned check (stage 6)", using the Task 8 default-config row and the intact count;
  - `README.md`: status table, notice and Readers/OCR section;
  - `.github/workflows/ci.yml`.

**Content:**
- **`docs/ocr.md`:**
  - the three ways (none / Tesseract / own provider);
  - the per-image and whole-document providers, with a 15-line custom `IOcrProvider` example;
  - fill and replace modes; the rendering package and who needs it;
  - per-OS install: Windows works as installed; Linux `apt install tesseract-ocr`, plus whatever the Task 1 recipe requires of an app; macOS `brew install tesseract`;
  - language data download and `DataPath`;
  - options and their defaults; the warning codes;
  - re-reading after OCR is turned on. Note: documents ingested before stage 6 need one forced re-ingest to pick up OCR.
- **CI:**
  - **Linux job:** `sudo apt-get install -y tesseract-ocr tesseract-ocr-eng`, plus the Task 1 recipe; a step `Test OCR (net8.0)` for `tests/RagDotNet.Ocr.Tests`.
  - **New `windows-latest` job:** restore, build, then test Core, Readers and Ocr on net8.0. It downloads `eng.traineddata` from tessdata_fast into `$RUNNER_TEMP/tessdata` and sets `RAGDOTNET_TESSDATA`.
  - Both jobs set `CI=true` (the default on GitHub), so OCR tests fail rather than skip.

- [ ] **Step 1:** Write the docs. Every claim matches the code; check option names and defaults against `OcrOptions` and `TesseractOcrOptions`.
- [ ] **Step 2:** Update CI.
- [ ] **Step 3: Run the full suite** and the CI commands in Docker. Expected: all pass.
- [ ] **Step 4: Commit** with message `docs: OCR guide, scanned-check results; ci: OCR tests on Linux and Windows`.
- [ ] **Step 5:** After the push, CI is green on both jobs. That is checked when the PR is opened.
