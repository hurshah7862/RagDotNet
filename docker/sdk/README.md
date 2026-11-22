# Build and test image

`Dockerfile` builds `ragdotnet-sdk:ocr`: the .NET 10 SDK, the .NET 8 runtime for the net8.0 tests, and Tesseract OCR with English language data.

```powershell
docker build -t ragdotnet-sdk:ocr docker/sdk
./docker/sdk/run.ps1 test RagDotNet.slnx
```

`run.ps1` mounts the repository at `/work`, keeps NuGet packages in the `ragdotnet-nuget` volume, and lets the container reach a local Ollama at `http://host.docker.internal:11434`.

It also mounts the Docker socket, so the store tests can start their database containers next to this one. To run them, set the variable first: `$env:RAGDOTNET_INTEGRATION = '1'; ./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests`.

The image exists because Windows Smart App Control on the development machine blocks freshly built binaries. Any machine with Docker can use it the same way.
