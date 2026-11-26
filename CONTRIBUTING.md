# Contributing

Thanks for helping. Bug reports, documentation fixes and pull requests are all welcome. For a security problem, do not open an issue; follow [SECURITY.md](SECURITY.md) instead.

## Before you start

For anything larger than a small fix, open an issue first and describe what you want to change, so we can agree on the approach before you write the code.

## Building and testing

You need the .NET SDK named in `global.json` (10.0.401 or a later 10.0 feature band) and the .NET 8 runtime. The libraries target net8.0 and net10.0, and `dotnet test` runs most test projects on both; without the .NET 8 runtime the net8.0 runs fail. To test on net10.0 only, add `--framework net10.0`.

```
dotnet build
dotnet test
```

Unit tests need no model, network or Docker. The tests below need something more; without it they are skipped, not failed (except that with `CI=true` set, as on GitHub Actions, missing Tesseract fails the OCR tests, so CI can never pass without running them):

- **OCR tests** need Tesseract and its English language data (`eng.traineddata`), found in `RAGDOTNET_TESSDATA`, else `/usr/share/tesseract-ocr/5/tessdata`. On Linux, `sudo apt install tesseract-ocr tesseract-ocr-eng` is enough. On Windows the package includes Tesseract, so download `eng.traineddata` from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) and point `RAGDOTNET_TESSDATA` at its folder. See [docs/ocr.md](docs/ocr.md).
- **Vector store tests** for PostgreSQL, Qdrant, Redis and SQL Server (the in-memory and SQLite ones always run) start containers through Testcontainers, so they need Docker and `RAGDOTNET_INTEGRATION=1`. See [docs/vector-stores.md](docs/vector-stores.md#how-stores-are-tested).
- **The Ollama end-to-end test** needs `RAGDOTNET_INTEGRATION=1` and Ollama running with `llama3.2:3b` and `nomic-embed-text` pulled.

If you have Docker but not the SDK, the repository's SDK image has the SDK and Tesseract: build it with `docker build -t ragdotnet-sdk:ocr docker/sdk`, then `./docker/sdk/run.ps1 test RagDotNet.slnx` runs the tests in it (the script needs PowerShell, `pwsh` on Linux and macOS). See [docker/sdk/README.md](docker/sdk/README.md).

## Making a change

- **Tests first.** A bug fix comes with a test that fails without the fix; a feature comes with tests for what it does. Tests use real code; mock only what you cannot run, such as a model.
- **Public API.** The packages track their public API in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, and warnings are errors, so adding a public type or member fails the build until you add it to the project's `PublicAPI.Unshipped.txt`. The analyzer's code fix adds the lines for you. Removing or changing a shipped member is a breaking change; say so in the pull request.
- **Docs.** If you change what a user sees, update the README or the page in `docs/` that describes it.
- **Package READMEs.** Each package's `README.md` is its page on nuget.org. CI checks that nuget.org will render it: links must be absolute `http` or `https` URLs, images must come from domains nuget.org trusts, and HTML tags are not allowed. [docs/publishing.md](docs/publishing.md) has the full rules.
- **Samples.** The apps in `samples/` build against the code in this repository, so a change that breaks one fails CI.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/) with the area in parentheses where there is one, for example `feat(core): ...`, `fix(dataingestion): ...`, `docs(ocr): ...` or `build(packaging): ...`.

`main` changes only through pull requests, and a pull request can be merged only when CI passes. The maintainer reviews each pull request before merging it. Keep a pull request to one change; a smaller one is reviewed sooner.

By contributing, you agree that your contribution is licensed under the [MIT License](LICENSE).
