# RagDotNet.Readers.Html

HTML reader for [RagDotNet](https://github.com/hurshah7862/RagDotNet), built on [AngleSharp](https://anglesharp.github.io). It reads the page's main content with its heading paths and leaves out navigation, banners, cookie notices and other page furniture.

```
dotnet add package RagDotNet.Readers.Html
```

```csharp
services.AddRag(o => { /* ... */ })
    .AddHtmlReader();

// or, without dependency injection:
var rag = new RagPipelineBuilder()
    // .UseChatClient(...), .UseEmbeddingGenerator(...), .UseVectorStore(...)
    .AddHtmlReader()
    .Build();
```

- Reads `.html`, `.htm` and `.xhtml` files and the `text/html` and `application/xhtml+xml` content types.
- Content comes from `main`, else `role="main"`, else a single `article`, else the `body` with boilerplate removed.
- Keeps lists, tables and code. Nothing on the page is run or fetched; images embedded as `data:` URIs are reported as warnings, or read by an OCR provider.
- The encoding is detected the way a browser does, and damaged markup still gives its readable text.

See [docs/readers.md](https://github.com/hurshah7862/RagDotNet/blob/main/docs/readers.md) for exactly what the reader keeps.
