# RagDotNet.Readers.Docx

DOCX reader for [RagDotNet](https://github.com/hurshah7862/RagDotNet), built on the [Open XML SDK](https://github.com/dotnet/Open-XML-SDK). Heading paths come from heading styles and outline levels, so company templates work without configuration.

```
dotnet add package RagDotNet.Readers.Docx
```

```csharp
services.AddRag(o => { /* ... */ })
    .AddDocxReader();

// or, without dependency injection:
var rag = new RagPipelineBuilder()
    // .UseChatClient(...), .UseEmbeddingGenerator(...), .UseVectorStore(...)
    .AddDocxReader()
    .Build();
```

- Reads `.docx` files and the `application/vnd.openxmlformats-officedocument.wordprocessingml.document` content type.
- Keeps lists, tables (one line per row, so a chunk never splits a row) and code; skips the table of contents.
- Headers, footers, footnotes, comments and text boxes are not read. Embedded images are reported as warnings, or read by an OCR provider such as `RagDotNet.Ocr.Tesseract`.
- A corrupt or password-protected file raises `DocumentReadException`; nothing is written for it.

See [docs/readers.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/readers.md) for exactly what the reader keeps.
