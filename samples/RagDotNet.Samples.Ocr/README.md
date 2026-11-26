# OCR sample

Reads scanned PDFs, and images inside PDF, Word and HTML files, with Tesseract OCR. The recognised text is placed where the page or image was, with its page number, so it is searched and cited like any other text.

Tesseract needs its language data, which is not in the package. Download `eng.traineddata` from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) and set `RAGDOTNET_TESSDATA` to its folder (the default is `./tessdata`). On Windows the package includes Tesseract itself. On Linux, install it first with `sudo apt install tesseract-ocr`, which puts the language data in `/usr/share/tesseract-ocr/5/tessdata`; on macOS, `brew install tesseract`.

```
RAGDOTNET_TESSDATA=/usr/share/tesseract-ocr/5/tessdata dotnet run --project samples/RagDotNet.Samples.Ocr -- tests/RagDotNet.Evaluation/Dataset/base/formats/scanned/troubleshooting.pdf
```

```
Reading troubleshooting.pdf (OCR takes a few seconds a page)...
troubleshooting.pdf: Created, 9 chunks
```

If Tesseract or the language data is missing, the app says what to install and exits. OCR reads scanned pages and images inside documents; an image file on its own (`.png`, `.jpg`) is not a format RagDotNet reads, and is reported as not read.

Three things the code shows:

- The app creates the OCR engine and disposes it when it ends, which frees the language data. `.AddTesseractOcr(...)` on the builder is shorter, but keeps the engine for the life of the process, and Tesseract then prints harmless `WARNING! LEAK!` lines at exit.
- `.AddPdfPageRendering()` turns scanned PDF pages into images for the OCR provider. Without it they stay `PageWithoutText` warnings.
- `Ocr.ChunkMaxTokens = 128` chunks the recognised text in pieces of up to 128 tokens. OCR returns a scanned page as one block of text, so at the default 512 tokens a page becomes one chunk, and a small model such as `llama3.2:3b` can answer from the wrong part of it. Text from a page's text layer keeps the normal size. See [docs/ocr.md](../../docs/ocr.md#chunk-size-for-ocr-text).

In your own app:

```
dotnet add package RagDotNet.Ocr.Tesseract --prerelease
dotnet add package RagDotNet.Readers.Pdf --prerelease
dotnet add package RagDotNet.Readers.Docx --prerelease
dotnet add package RagDotNet.Readers.Html --prerelease
dotnet add package OllamaSharp
dotnet add package CommunityToolkit.VectorData.InMemory
```

`RagDotNet.Ocr.Tesseract` brings `RagDotNet.Readers.Pdf.Rendering` with it. The Word and HTML readers are there for images inside those formats; if you only read PDFs, leave them out along with their `Add...Reader()` calls. See [docs/ocr.md](../../docs/ocr.md).
