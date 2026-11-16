# RagDotNet stage 6: OCR — design

Stage 6 of the main spec (`2026-10-18-ragdotnet-design.md`, §5.11 and §10 item 6). This document refines §5.11 with the owner's decisions from the stage 6 brainstorm (2026-11-11). Where the two differ, this document wins.

## Intent

Scanned PDF pages and images inside documents hold text that RagDotNet cannot search today. The stage 5 readers report them as `PageWithoutText` and `ImageNotRead` warnings. Stage 6 turns those warnings into searchable, citable text whenever the app registers an OCR provider.

RagDotNet is a NuGet library used by apps on Windows, Linux and macOS. OCR must work on all three. An app that already has its own OCR (Azure, AWS Textract, Google Vision, an in-house engine) must be able to use it instead, without ever installing the Tesseract package or its native binaries.

## Goals

1. **An OCR contract in Core, with no dependencies** *(owner)*. Two kinds of provider:
   - **Per image (the default kind)** *(owner)*: `IOcrProvider` receives one image (a rendered scanned page, or an embedded image) and returns text with a confidence.
   - **Whole document** *(owner)*: `IDocumentOcrProvider` receives the whole file and returns text per page, optionally with structure (headings, tables).
2. **The app chooses how a whole-document provider is used** *(owner)*:
   - **Fill missing pages (the default)** *(owner)*: our reader reads the document, and the provider's output is used only for pages with no text layer.
   - **Replace the reader**: for the formats the provider says it handles, its output replaces our reader's for the whole file.
3. **`RagDotNet.Ocr.Tesseract`**, a per-image provider built on the `TesseractOCR` NuGet package (maintained fork of charlesw/Tesseract, Tesseract 5.5) *(owner)*:
   - **Windows:** works as installed.
   - **Linux and macOS:** need Tesseract installed (`apt install tesseract-ocr`, `brew install tesseract`). When it is missing, the package fails at startup with a clear error naming the install command for that OS, not deep inside an ingest.
   - **Language data:** not bundled (main spec). The data folder and languages (default `eng`) are set in options.
4. **What gets read**, following the main spec:
   - PDF pages with no text layer, rendered at 300 DPI;
   - images embedded in PDF pages that do have text (diagrams, screenshots);
   - embedded images in DOCX, and `data:` URI images in HTML. Remote HTML images are never fetched.
5. **Where the text goes:** recognised text becomes its own block at the image's position, with the page number and heading path of where it appears, so it is chunked and cited like the surrounding text (main spec).
6. **Scanned check in the evaluation harness** *(owner)*. The 8 `base` documents are rendered as image-only PDFs (no text layer, like a photocopy), run through Tesseract, and scored with the same 40 questions against the Markdown baseline. The result goes in `docs/evaluation-results.md`, next to the stage 5 format check.

## Non-goals

- **Bundling Tesseract binaries for Linux or macOS**, or bundling language data.
- **Fetching remote images** referenced by HTML.
- **OCR for formats other than PDF, DOCX and HTML.**
- **Shipping any cloud OCR provider.** The contract makes one easy to write; the docs show a short example of a custom provider.
- **Handwriting, or layout analysis beyond what Tesseract gives.**

## Anti-goals (a failure even if it works)

- **Core taking any OCR or native dependency.**
- **An app with its own OCR having to install or load anything from `RagDotNet.Ocr.Tesseract`.**
- **Content dropped silently.** Every page or image that is not read gives a warning, as in stage 5.
- **One bad image or page failing a whole ingest.** A provider failure on one item becomes a warning. A provider that cannot work at all (for example, missing native library or language data) is a clear error.
- **Ingest without OCR getting slower or behaving differently.** With no provider registered, results are identical to stage 5, warnings included.

## Behaviour

- **Async and cancellable:** both provider kinds are async and take a `CancellationToken`.
- **Images skipped and read once** *(main spec, builder's call there)*:
  - images smaller than 64×64 px are skipped;
  - identical images within one document are recognised once.
- **Low confidence:** text below a minimum confidence is kept, and flagged with a warning (main spec).
- **Turning embedded-image OCR off:** it is on by default whenever a provider is registered, and can be turned off (`OcrEmbeddedImages`, main spec).
- **Warnings:**
  - with no provider registered, `PageWithoutText` and `ImageNotRead` behave exactly as in stage 5;
  - with a provider, new codes cover OCR failures and low confidence.
- **Both kinds registered** *(my call)*: the whole-document provider handles missing pages (or the whole file, in replace mode), and the per-image provider handles embedded images.

## Turning OCR on for documents already ingested *(owner)*

Re-ingesting an unchanged document normally returns `Unchanged`. After an app registers OCR, or changes its OCR setup, the documents that had pages or images left unread must be read again automatically on their next ingest, without `force`.

- Every other document stays `Unchanged`, so turning on OCR does not re-embed the whole library.
- Apps without OCR see exactly the stage 5 behaviour and hashes.

One way to do it is to record, per document, whether it had unread OCR items and which OCR setup it was read with. A changed setup then only invalidates documents that recorded unread items. The mechanism is the builder's choice.

## Scoring the scanned check *(owner)*

- **Strict matching stays.** A question is found only when its exact evidence text comes back, so OCR errors count against the score. The number shows real end-to-end quality on scanned documents.
- **OCR intactness is reported separately.** Alongside the scores, the report states how many of the 40 evidence passages came through OCR intact, so a low score can be traced to OCR or to retrieval.
- **Mechanics:** the harness reads datasets with the readers directly, not through ingest. The scanned variant must run OCR the same way ingest does: same provider, same placement of the text.
- **Unscorable questions:** under the stage 5 rules, a question whose evidence is damaged by OCR counts as unscorable, and an unscorable question scores as a miss. The report counts these as the OCR-damaged passages.

## Edge cases settled after the builder check *(my calls)*

- **Searchable scans.** On a PDF page that has a text layer, an image covering most of the page is not OCR'd, because it is the scan under that text layer. A page whose only text is a small stamp (a page number or Bates number, a few characters) over a full-page image counts as a scanned page. The builder sets the thresholds and pins them with tests.
- **Only a whole-document provider registered.** Embedded images (DOCX, HTML, and images on PDF pages with text) stay `ImageNotRead` warnings, as in stage 5. The message says that a per-image provider would read them.
- **Replace-mode scope.** Replace mode only applies to PDF, DOCX and HTML, which are formats a reader already handles. A provider cannot add new formats such as `.png` or `.tiff`.
- **Replace mode when the provider fails.** If the whole-document provider fails on a file, ingest falls back to our reader and adds a warning. It does not fail the ingest.
- **The Docker test image.** Its original Dockerfile is gone. The builder recreates it from `docker image history ragdotnet-sdk`:
  - .NET SDK 10.0.403 image, plus the .NET 8 runtime;
  - adds Tesseract and `eng` language data;
  - commits it to the repo.

## Constraints

- **Frameworks:** Core targets net8.0 and net10.0. The new packages follow the existing reader packages' conventions:
  - PublicAPI files;
  - `Add…` extensions for both `IRagBuilder` and `RagPipelineBuilder`, in the same two namespaces as stage 5.
- **Where it can be tested:** Windows Smart App Control blocks freshly built binaries on the dev machine, so all builds and tests run in the `ragdotnet-sdk` Docker image (Linux).
  - That image gains Tesseract and `eng` language data.
  - Its Dockerfile moves into the repo so the setup is reproducible *(my call)*.
  - Windows is tested by adding a Windows job to CI *(my call)*.
- **CI:** Linux CI installs Tesseract with apt and runs the OCR tests.

## Decided by me (the owner can overrule)

1. **Page rendering is a separate package** *(owner)*. The main spec put PDFtoImage (PDFium) in the Tesseract package, but a custom per-image provider also needs scanned pages as images.
   - **`RagDotNet.Readers.Pdf` gains no new dependency.** It stays PdfPig only, with no native libraries. It defines a small page-renderer interface (render page N at a given DPI to an image) and uses it only when an OCR provider is registered.
   - **New package `RagDotNet.Readers.Pdf.Rendering`** implements that interface with PDFtoImage. It has its own `Add…` extensions, following the stage 5 conventions.
   - **`RagDotNet.Ocr.Tesseract` depends on the rendering package** and registers it, so Tesseract users add nothing extra.
   - **A custom per-image provider without the rendering package** still gets the `PageWithoutText` warning for scanned pages. The message names the package to add.
   - **A whole-document provider never needs rendering,** because it receives the file.
   - **Anti-goal:** no app that doesn't render PDF pages for OCR ever installs PDFtoImage.
2. **Image dimensions for the 64×64 rule** come from the source where it gives them (PdfPig, DOCX drawing extents), and otherwise from reading PNG, JPEG and GIF headers in Core. That needs no image library. An image whose size cannot be told is read.
3. **Whole-document provider results** come back as blocks with page numbers.
   - A provider that only knows plain text returns one paragraph per page.
   - Fill mode takes the blocks of pages that had no text; replace mode takes them all.
4. **Pipeline position:** readers return the images they found (bytes, position, page, heading path) instead of only counting them, and the ingest step runs OCR. Skipping, de-duplication, warnings and confidence are therefore handled once, in Core, for every reader and every provider.
5. **De-duplication:** identical images are detected by content hash.

## Left to the builder

- Exact type and option names, apart from those named above.
- The default minimum confidence.
- How the scanned PDFs are generated. For example, render the stage 5 PDFs with PDFtoImage and wrap each page image in a new PDF. Any approach works as long as the result has no text layer.
- Whether tracing spans (`rag.ocr`) are added, depending on whether tracing exists yet.

## Risks

- **`TesseractOCR` against the system Tesseract on Linux:** the wrapper expects particular native library names and versions (5.5), while distributions ship other versions (Debian 12 ships 5.3). This is the biggest unknown.
  - The plan's first task should be a short spike in the Docker image that proves `TesseractOCR` loads the system library and reads one image.
  - If it cannot, stop and bring the options back to the owner before building on it.
- **Accuracy of the scanned check:** OCR errors in rare words can cost retrieval. The check exists to measure that, not to hit a target. A low number is a finding to report, not a failure.
