# RagDotNet.Readers.Pdf

PDF reader for [RagDotNet](https://github.com/hurshah7862/RagDotNet), built on [PdfPig](https://github.com/UglyToad/PdfPig). Every block keeps its page number, so citations show `p. 3`, and headings are recognised by font size, so chunks carry heading paths.

```
dotnet add package RagDotNet.Readers.Pdf
```

```csharp
services.AddRag(o => { /* ... */ })
    .AddPdfReader();

// or, without dependency injection:
var rag = new RagPipelineBuilder()
    // .UseChatClient(...), .UseEmbeddingGenerator(...), .UseVectorStore(...)
    .AddPdfReader()
    .Build();
```

- Reads `.pdf` files and the `application/pdf` content type.
- Keeps lists and monospace code blocks; tables become paragraph text.
- Pages without a text layer (scans) and images on text pages are reported as warnings in `IngestResult.Warnings`. To read them, add an OCR provider such as `RagDotNet.Ocr.Tesseract`.
- A corrupt, truncated or password-protected file raises `DocumentReadException`; nothing is written for it.

See [docs/readers.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/readers.md) for exactly what the reader keeps.
