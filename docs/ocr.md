# OCR

Scanned PDF pages and images inside documents hold text that a reader cannot extract. With an OCR provider registered, ingest reads them and puts the text where the page or image was, with its page number and heading path, so it is searched and cited like any other text. Without one, nothing changes: each unread page or image is reported as a warning in `IngestResult.Warnings`.

There are three ways to set it up:

| Setup | What you add | What gets read |
|---|---|---|
| No OCR | nothing | Scanned pages give `PageWithoutText` warnings, images `ImageNotRead`. |
| Tesseract | the `RagDotNet.Ocr.Tesseract` package | Scanned PDF pages, and images in PDF, DOCX and HTML. |
| Your own OCR | a class implementing `IOcrProvider` or `IDocumentOcrProvider` | Whatever your service reads; Tesseract is never installed. |

## Tesseract

```csharp
services.AddRag(o => { /* ... */ })
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader()
    .AddTesseractOcr(o =>
    {
        o.DataPath = "/srv/tessdata"; // folder holding eng.traineddata
        o.Languages = "eng";          // or "eng+deu"
    });
```

With the pipeline builder, call `.AddTesseractOcr(...)` the same way. `AddTesseractOcr` also adds PDF page rendering (below), so scanned pages work without anything else.

The engine holds the language data in memory. With dependency injection it is freed when the container is disposed. With the pipeline builder, `AddTesseractOcr` keeps it for the life of the process; to free it earlier, create it yourself and dispose it once the pipeline is no longer used:

```csharp
using var ocr = new TesseractOcrProvider(new TesseractOcrOptions { DataPath = "/srv/tessdata" });
var rag = builder.UseOcrProvider(ocr).AddPdfPageRendering().Build();
```

Disposing waits for a recognition already running; calls after that throw `ObjectDisposedException`. If the process exits without disposing the engine, Tesseract prints `ObjectCache ... WARNING! LEAK!` lines as it shuts down; they are harmless, and disposing the provider as above stops them.

**Language data** is not included in the package. Download the `.traineddata` files for your languages from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) (or `tessdata_best` for better accuracy at lower speed) and set `DataPath` to their folder. The default is `./tessdata`.

**Installing Tesseract:**

| OS | Install |
|---|---|
| Windows (x64) | Nothing: the package includes Tesseract. It needs the Microsoft Visual C++ Redistributable, which most machines already have. |
| Linux | `sudo apt install tesseract-ocr` (Debian, Ubuntu), `sudo dnf install tesseract` (Fedora), or your distribution's package. The package finds the installed library itself; nothing needs to be copied or linked by hand. |
| macOS | `brew install tesseract`. Expected to work the same way as Linux, but not tested yet. |

On Linux the package finds the system's `libtesseract` and `liblept`/`libleptonica` in the usual library folders, and makes them visible to the TesseractOCR wrapper through links in a new folder under the temp directory, created for each process with owner-only permissions and removed when the process exits. On a Linux distribution's own package the language data is usually in `/usr/share/tesseract-ocr/5/tessdata`.

**When something is missing,** the error says what to install:

- With dependency injection, the host fails at start with an `OptionsValidationException` naming the missing language file or the install command.
- With the pipeline builder, `AddTesseractOcr` throws `OcrUnavailableException` with the same message.

Tested on Ubuntu 24.04 (Tesseract 5.3) in this project's Docker image and in CI on Linux and Windows.

## PDF page rendering

A scanned PDF page has to be turned into an image before a per-image OCR provider can read it. `RagDotNet.Readers.Pdf.Rendering` does that with PDFium (through PDFtoImage), at 300 DPI by default. It is a separate package so that apps reading only text PDFs never install PDFium's native libraries.

- Tesseract users get it automatically.
- With your own per-image provider, add it with `.AddPdfPageRendering()`. Without it, scanned pages stay `PageWithoutText` warnings, and the message names the package. (A scan with a short stamp is still read without it, from the page image the PDF already holds.)
- A whole-document provider does not need it: it receives the PDF itself.

## Your own OCR provider

A per-image provider receives one image at a time: a rendered scanned page, or an image embedded in a document.

```csharp
public sealed class MyCloudOcr(MyOcrClient client) : IOcrProvider
{
    // Names the engine and anything that changes its output; a change makes affected documents be read again.
    public string Id => "my-cloud-ocr/v2";

    public async Task<OcrResult> RecognizeAsync(ImageInput image, CancellationToken cancellationToken)
    {
        var response = await client.ReadAsync(image.Bytes, image.MediaType, cancellationToken);
        return new OcrResult(response.Text, response.Confidence); // confidence from 0 to 1
    }
}

services.AddRag(o => { /* ... */ })
    .AddPdfReader()
    .AddOcrProvider<MyCloudOcr>()
    .AddPdfPageRendering(); // so scanned PDF pages reach it too
```

A **whole-document provider** (`IDocumentOcrProvider`) receives the whole file, as services such as Azure Document Intelligence expect. It returns blocks with page numbers; a service that only returns plain text returns one paragraph per page. Register it with `.AddDocumentOcrProvider<T>()` or `UseDocumentOcrProvider(...)`. `OcrOptions.DocumentProviderMode` chooses how it is used:

- **`FillMissingPages`** (default): our reader reads the document, and the provider's output is used only for pages that had no text layer.
- **`ReplaceReader`**: for formats the provider says it handles (`CanProcess`), its output replaces our reader's for the whole file, keeping its own headings and tables. If the provider fails on a file, our reader reads it instead and an `OcrFailed` warning says so. A provider can only replace formats a reader already handles (PDF, DOCX, HTML, Markdown, text); it cannot add new ones such as `.png`.

Both kinds can be registered together: the whole-document provider handles scanned pages (or whole files in replace mode), and the per-image provider handles embedded images. With only a whole-document provider, embedded images stay `ImageNotRead` warnings that say a per-image provider would read them.

A provider that cannot work at all, for example because its engine is not installed, throws `OcrUnavailableException`, which stops the ingest with its message. Any other exception affects only that page or image and becomes an `OcrFailed` warning.

## Options

`RagOptions.Ocr` (`OcrOptions`), bindable from configuration (`"Ocr": { "MinConfidence": 0.8 }`):

| Option | Default | Meaning |
|---|---|---|
| `OcrEmbeddedImages` | `true` | Read images embedded in documents. When false they are skipped without a warning; scanned pages are still read. |
| `MinImageSize` | `64` | Images narrower or shorter than this many pixels, such as icons, are skipped. The size comes from the document, else from the image header; an image whose size cannot be told is read. |
| `MinConfidence` | `0.6` | Text recognised below this confidence (0 to 1) is kept, with an `OcrLowConfidence` warning. Changing it does not make documents be read again. |
| `PageDpi` | `300` | Resolution scanned pages are rendered at, from 72 to 1200. |
| `DocumentProviderMode` | `FillMissingPages` | How a whole-document provider is used (above). |
| `ChunkMaxTokens` | not set | Largest chunk, in tokens, for text that came from OCR; at least 16. Other text keeps `Chunking.MaxTokens`. See [Chunk size for OCR text](#chunk-size-for-ocr-text). |

Other rules:

- An image that appears more than once in a document, such as a logo on every page, is read once and its text placed only at its first position.
- On a PDF page that has a text layer, an image covering most of the page is not read, because it is the scan under that text (a "searchable PDF"). A page whose only text is a short stamp (up to 30 characters, such as a page or Bates number) over a full-page image is treated as a scanned page and read.
- Recognised text that is empty adds nothing.
- Vector images (SVG, EMF, WMF), which OCR engines cannot decode, are not sent to the provider; one `ImageNotRead` warning counts them.
- With OCR on, readers extract image bytes only when a per-image provider will read them, so ingest without OCR (or with `OcrEmbeddedImages` off) does not decode images.

## Chunk size for OCR text

Tesseract, and any per-image provider, returns a scanned page as one block of text, without the headings and paragraphs a text layer has. With the default chunk size of 512 tokens, a whole page usually becomes one chunk. Retrieval still finds the right page, but a small chat model given that page can answer from the wrong part of it.

`ChunkMaxTokens` gives text that came from OCR its own, smaller chunk size: scanned pages, text read from images in documents, and a whole-document provider's output. Text from a page's text layer, and every document without OCR text, keeps `Chunking.MaxTokens`. The chunker and its overlap stay the same, and no chunk mixes OCR text with other text. That last point has a side effect: text read from an image in the middle of a section splits the section in two, so the paragraphs before and after the image are chunked separately and no longer merge or overlap with each other. OCR done inside a DataIngestion reader (see [dataingestion.md](dataingestion.md)) is not marked as OCR text, so this option does not apply to it.

```csharp
services.AddRag(o =>
{
    o.Ocr.ChunkMaxTokens = 128;
});
```

or `"Ocr": { "ChunkMaxTokens": 128 }` in configuration. It also applies when `IngestOptions.Chunking` sets the chunking for one call.

In one small check, the scanned copy of the troubleshooting guide from the [scanned check](evaluation.md#scanned-check) was read with Tesseract and asked its 5 questions with `llama3.2:3b` and `nomic-embed-text`, once at 512 tokens and once at 128 (set as the document's chunk size, which for a fully scanned PDF is the same as this option). At 128 the 3 page-sized chunks became 9, and the answers that stated the expected fact went from 3 of 5 to 5 of 5; retrieval found the right passage at both sizes. It was a single run of 5 questions with a small model whose answers vary from run to run, so measure on your own documents before relying on it.

Changing it reads documents with scanned pages or images again on their next ingest, as for the other OCR options. Documents a whole-document provider read in `ReplaceReader` mode are not detected; delete and ingest them again (see below).

## Warnings

| Code | When |
|---|---|
| `PageWithoutText` | A page has no text layer and no provider read it. |
| `ImageNotRead` | Images were not read because no per-image provider is registered. |
| `OcrFailed` | OCR failed on a page or image, an image could not be extracted from the document, or the whole-document provider failed. The rest of the document is read. |
| `OcrLowConfidence` | Text was recognised with confidence below `MinConfidence`; it was kept. |

## Documents ingested before OCR was turned on

Re-ingesting an unchanged document normally returns `Unchanged`. A document that had scanned pages or images is read again automatically when the OCR setup changes: a provider is added, removed or replaced (its `Id` changes), or `OcrEmbeddedImages`, `MinImageSize`, `PageDpi`, `DocumentProviderMode` or `ChunkMaxTokens` change. Documents without scanned pages or images stay `Unchanged`, so turning OCR on does not re-embed a whole library.

A document where OCR failed on a page or image (an `OcrFailed` warning, for example a cloud service's rate limit or timeout) is read again on its next ingest, until it goes through without failures. A file whose image fails every time is therefore re-read on every ingest; the warning names the page. For Tesseract, `Id` includes a fingerprint of the language files, so switching between `tessdata_fast` and `tessdata_best` also reads affected documents again.

Two cases need the document deleted (`DeleteDocumentAsync`) and ingested again, once:

- documents ingested with a RagDotNet version before OCR existed;
- documents a whole-document provider read in `ReplaceReader` mode, when that provider changes, because such a document has no unread pages left to detect.

## How well it works

The evaluation harness renders its 8 test documents as image-only PDFs, reads them with Tesseract and scores the same 40 questions. See [Scanned check](evaluation.md#scanned-check).
