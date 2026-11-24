# RagDotNet.Readers.Pdf.Rendering

PDF page rendering for [RagDotNet](https://github.com/hurshah7862/RagDotNet) OCR: turns scanned PDF pages into images, at 300 DPI, so a per-image OCR provider can read them. Uses PDFium through [PDFtoImage](https://github.com/sungaila/PDFtoImage), with native libraries for Windows, Linux and macOS.

```
dotnet add package RagDotNet.Readers.Pdf.Rendering
```

```csharp
services.AddRag(o => { /* ... */ })
    .AddPdfReader()
    .AddOcrProvider<MyCloudOcr>() // your own IOcrProvider
    .AddPdfPageRendering();       // so scanned PDF pages reach it too
```

With the pipeline builder, call `.AddPdfPageRendering()` the same way.

- **`RagDotNet.Ocr.Tesseract` already includes it**; add it yourself only for your own per-image OCR provider.
- A whole-document OCR provider (`IDocumentOcrProvider`) does not need it: it receives the PDF itself.
- It is a separate package so that apps reading only text PDFs never install PDFium's native libraries.

See [docs/ocr.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/ocr.md) for how OCR fits into ingest.
