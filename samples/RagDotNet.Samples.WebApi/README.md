# Web API sample

An ASP.NET Core minimal API on RagDotNet's dependency injection setup (`services.AddRag(...)`):

| Endpoint | Does |
|---|---|
| `POST /documents` | Uploads a file (multipart form field `file`). Returns `202 Accepted` with the job id straight away; a background worker ingests it. |
| `GET /jobs/{jobId}` | The job's state (`Queued`, `Running`, `Succeeded`, `Failed` or `Cancelled`), its chunk count and warnings. `404` for an unknown job. |
| `POST /ask` | Takes `{"text": "question"}` and returns the answer, whether the documents held one, and the sources it cites. |

Documents are stored in a SQLite file in an `index` folder next to the app, so they are still there after a restart.

```
dotnet run --project samples/RagDotNet.Samples.WebApi
```

It listens on `http://localhost:5080`. [RagDotNet.Samples.WebApi.http](RagDotNet.Samples.WebApi.http) has the three requests for Visual Studio, Rider or VS Code's REST Client, or use curl:

```
curl -F "file=@tests/RagDotNet.Evaluation/Dataset/base/formats/pdf/install-guide.pdf" http://localhost:5080/documents
{"jobId":"72648805060d45bb8225867b62da058a"}

curl http://localhost:5080/jobs/72648805060d45bb8225867b62da058a
{"jobId":"72648805060d45bb8225867b62da058a","documentId":"install-guide.pdf","state":"Succeeded","attempts":1,"error":null,"chunks":12,"warnings":[]}

curl -H "Content-Type: application/json" -d "{\"text\": \"What command installs Quill with winget?\"}" http://localhost:5080/ask
{"text":"To install Quill with winget, run the command: winget install --id Quill.Quill --exact [1, 2].","isAnswered":true,"citations":[{"number":1,"sourceName":"install-guide.pdf","location":"p. 1"},{"number":2,"sourceName":"install-guide.pdf","location":"p. 3"}]}
```

The document id is the uploaded file's name, so uploading a file with the same name replaces the earlier one. A real app would use its own ids and add authentication. For per-user or per-tenant documents, RagDotNet's access scopes keep each caller to their own documents; see the main [README](../../README.md).

In your own app:

```
dotnet add package RagDotNet.Hosting --prerelease
dotnet add package RagDotNet.Readers.Pdf --prerelease
dotnet add package RagDotNet.VectorData.Sqlite --prerelease
dotnet add package OllamaSharp
```

See [docs/hosting.md](../../docs/hosting.md) for the queue's options, retries, pausing, and durable queues.
