# OCR sample

Reads scanned PDFs, and images inside PDF, Word and HTML files, with Tesseract OCR. The recognised text is placed where the page or image was, with its page number, so it is searched and cited like any other text.

Tesseract needs its language data, which is not in the package. Download `eng.traineddata` from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) and set `RAGDOTNET_TESSDATA` to its folder (the default is `./tessdata`). On Windows the package includes Tesseract itself. On Linux, install it first with `sudo apt install tesseract-ocr`, which puts the language data in `/usr/share/tesseract-ocr/5/tessdata`; on macOS, `brew install tesseract`.

```
RAGDOTNET_TESSDATA=/usr/share/tesseract-ocr/5/tessdata dotnet run --project samples/RagDotNet.Samples.Ocr -- tests/RagDotNet.Evaluation/Dataset/base/formats/scanned/troubleshooting.pdf
```

```
Reading troubleshooting.pdf (OCR takes a few seconds a page)...
tests/RagDotNet.Evaluation/Dataset/base/formats/scanned/troubleshooting.pdf: Created, 3 chunks
```

If Tesseract or the language data is missing, the app says what to install and exits.

Two things the code shows:

- The app creates the OCR engine and disposes it when it ends, which frees the language data. `.AddTesseractOcr(...)` on the builder is shorter, but keeps the engine for the life of the process, and Tesseract then prints harmless `WARNING! LEAK!` lines at exit.
- `.AddPdfPageRendering()` turns scanned PDF pages into images for the OCR provider. Without it they stay `PageWithoutText` warnings.

Each scanned page becomes one long passage of text. With a small model such as `llama3.2:3b`, the answer sometimes comes from the wrong part of the right page; the citation still names the page. A larger model does better.

In your own app:

```
dotnet add package RagDotNet.Ocr.Tesseract --prerelease
dotnet add package RagDotNet.Readers.Pdf --prerelease
```

`RagDotNet.Ocr.Tesseract` brings `RagDotNet.Readers.Pdf.Rendering` with it. Add `RagDotNet.Readers.Docx` and `RagDotNet.Readers.Html` to read images in those formats. See [docs/ocr.md](../../docs/ocr.md).
