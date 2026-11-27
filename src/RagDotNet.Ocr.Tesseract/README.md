# RagDotNet.Ocr.Tesseract

[Tesseract](https://github.com/tesseract-ocr/tesseract) OCR for [RagDotNet](https://github.com/hurshah7862/RagDotNet): reads scanned PDF pages and images embedded in PDF, DOCX and HTML, and puts their text where the page or image was, so it is searched and cited like any other text.

```
dotnet add package RagDotNet.Ocr.Tesseract
```

```csharp
services.AddRag(o => { /* ... */ })
    .AddPdfReader()
    .AddDocxReader()
    .AddHtmlReader()
    .AddTesseractOcr(o =>
    {
        o.DataPath = "/srv/tessdata"; // folder holding eng.traineddata (default ./tessdata)
        o.Languages = "eng";          // or "eng+deu"
    });
```

With the pipeline builder, call `.AddTesseractOcr(...)` the same way. It also adds PDF page rendering, so scanned pages work without anything else.

**Language data is not included.** Download the `.traineddata` files for your languages from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) and set `DataPath` to their folder.

**Installing Tesseract:**

- **Windows (x64):** nothing to install; the package includes Tesseract. It needs the Microsoft Visual C++ Redistributable, which most machines already have.
- **Linux:** `sudo apt install tesseract-ocr` (Debian, Ubuntu) or your distribution's package; the library is found automatically.
- **macOS:** `brew install tesseract`; the library is found automatically. Tested on Apple Silicon.

If the library or a language file is missing, startup fails with a message saying what to install.

See [docs/ocr.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/ocr.md) for warnings, confidence, and plugging in your own OCR service instead.
