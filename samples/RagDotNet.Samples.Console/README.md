# Console sample

Ingests every `.md` and `.txt` file in a folder into an in-memory store, then answers questions with streamed text and a numbered list of the sources it cites. An empty line exits.

```
dotnet run --project samples/RagDotNet.Samples.Console -- tests/TestAssets/markdown
```

```
Question (empty line to exit): How do I install on Linux?
To install on Linux, run `sudo apt install quill` [2].
[2] install-guide.md — Install > Linux
```

The in-memory store is lost when the app exits, and is not meant for production; the [Documents sample](../RagDotNet.Samples.Documents) keeps its store on disk.

In your own app:

```
dotnet add package RagDotNet.Core --prerelease
dotnet add package OllamaSharp
dotnet add package CommunityToolkit.VectorData.InMemory
```
