# Stage 10: CI and Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every push and pull request packs all ten packages and checks them, a pushed `v*` tag publishes them to nuget.org, and every package carries its own README.

**Architecture:** Package metadata moves into `Directory.Build.props` for everything under `src/`. A C# file-based app, `eng/check-packages.cs`, opens the `.nupkg` and `.snupkg` files `dotnet pack` produced and fails on anything a nuget.org user would trip over; it is the test for this stage. CI runs pack plus the check on every push and PR; a separate `publish.yml` repeats them on a `v*` tag and pushes with NuGet trusted publishing.

**Tech Stack:** .NET SDK 10.0.401 (file-based apps: `dotnet run file.cs`), MinVer 8.0.0, the SDK's built-in SourceLink, GitHub Actions, `NuGet/login@v1`.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md` (§3 Constraints, §4 Packages, §7 Repository layout and Versioning, §10 stage 10)

## Global Constraints

- Target frameworks: `net8.0;net10.0` for every shipped package.
- License: MIT.
- Package naming: `RagDotNet.*`. Checked free on nuget.org on 2026-11-24 (`ragdotnet.core`, `ragdotnet.hosting`, `ragdotnet.readers.pdf`, `ragdotnet.vectordata.sqlite` all 404).
- One shared version for every package, from MinVer and `v*` tags. `RagDotNet.Keyword.Lucene` and `RagDotNet.DataIngestion` always get a `-preview` suffix added to that version.
- "v1 complete" means every build stage is done and packages pack in CI. Publishing to nuget.org is a separate manual step: nothing in this stage pushes a package or a tag.
- Core contains no parser, native, or preview dependency (Markdig is the spec's own builder's call and stays).
- `Directory.Build.props` carries SourceLink and MinVer (§7).
- Build and run everything through Docker (`dn.sh`); local `dotnet` is blocked by App Control.

## The ten packages

`RagDotNet.Core`, `RagDotNet.Readers.Pdf`, `RagDotNet.Readers.Pdf.Rendering`, `RagDotNet.Readers.Docx`, `RagDotNet.Readers.Html`, `RagDotNet.Ocr.Tesseract`, `RagDotNet.Keyword.Lucene` (preview), `RagDotNet.Hosting`, `RagDotNet.DataIngestion` (preview), `RagDotNet.VectorData.Sqlite` — ten projects under `src/`, all packed. (`Readers.Pdf.Rendering` is the tenth; the spec's §4 table predates it.)

## Review Focus

1. **A release tag MinVer ignores.** Today `Directory.Build.props` sets no `MinVerTagPrefix`, so a `v1.0.0` tag is not a version tag and the "release" packs as `0.0.0-alpha.0.N`. Expected: tag `v1.2.3` packs as `1.2.3` (and `1.2.3-preview` for the two preview packages). Pinned by Task 1's `-Version` check and Task 3's tag-vs-version step.
2. **A shallow checkout in the publish job.** `actions/checkout` defaults to depth 1; MinVer then cannot see the tag's history and versions the build wrong. Expected: publish uses `fetch-depth: 0`, and the version check fails the job before any push. Pinned in Task 3.
3. **A package that would break for its user.** A missing README (nuget.org shows an empty page), a README with relative links (broken on nuget.org), a missing `lib/net8.0` or `lib/net10.0` assembly or XML docs, missing sqlite-vec natives for one of the three RIDs, or a stable package depending on a prerelease one. Expected: the check fails naming the package and the problem. Pinned in Tasks 1 and 2.
4. **Something that is not a library gets published.** Tests, the evaluation harness, the test-asset generator and the sample must never reach nuget.org. Expected: the check fails on any package not in the list of ten. Pinned in Task 1.
5. **A re-run after a partial push.** If nuget.org accepts four packages and the job then fails, re-running must not fail on the four already there. Expected: `--skip-duplicate`. Pinned in Task 3.

---

### Task 1: Package metadata and the package check

**Files:**
- Create: `eng/check-packages.cs`
- Modify: `Directory.Build.props`

**Interfaces:**
- Produces: `dotnet run eng/check-packages.cs -- <packageDir> [--version <x.y.z[-pre]>]` — exit code 0 when every check passes; otherwise prints one line per failure (`<PackageId>: <problem>`) and exits 1. Task 2 adds README checks to it; Task 3 calls it from CI.

- [ ] **Step 1: Write the check** in `eng/check-packages.cs` (C# file-based app; only `System.IO.Compression` and `System.Xml.Linq`, no packages). It reads every `*.nupkg` (excluding `*.snupkg`) in `packageDir` and checks:
  - The set of package ids is exactly the ten above — a missing one and an extra one are both failures.
  - Each package has a matching `.snupkg` with the same id and version.
  - Nuspec: `license type="expression"` is `MIT`; `authors`, `description`, `tags` non-empty; `projectUrl` is `https://github.com/hurshah7862/RagDotNet`; `repository` has `type="git"`, `url="https://github.com/hurshah7862/RagDotNet"` and a non-empty `commit`.
  - Entries `lib/net8.0/<Id>.dll`, `lib/net8.0/<Id>.xml`, `lib/net10.0/<Id>.dll`, `lib/net10.0/<Id>.xml` exist.
  - `RagDotNet.VectorData.Sqlite` contains `runtimes/linux-x64/native/vec0.so`, `runtimes/osx-arm64/native/vec0.dylib`, `runtimes/win-x64/native/vec0.dll` and `THIRD-PARTY-NOTICES.md`.
  - Versions: the two preview packages have a prerelease label starting with `preview`; every other package has the same version as `RagDotNet.Core`, and each preview package's version is Core's major.minor.patch plus that label. With `--version V`: Core's version equals `V`, and the preview packages are `V-preview` when `V` has no prerelease label, `V-preview.<label>` when it has one.
  - A package with no prerelease label has no dependency whose version range's minimum is a prerelease.
  - `RagDotNet.Core` has no dependency on any package with a prerelease version.

- [ ] **Step 2: Pack and run the check; watch it fail**

Run: `dn.sh pack -c Release -o artifacts/packages`, then `dn.sh run eng/check-packages.cs -- artifacts/packages` (no `--version`: an untagged build's version is `0.0.0-alpha.0.N`).
Expected: FAIL, listing for every package at least: missing `.snupkg`, missing `projectUrl`, missing `repository`/`commit`, empty `tags`. No failure for ids, lib files, sqlite natives or versions (those already hold); if one appears, the check or the build is wrong — find out which before Step 3.

- [ ] **Step 3: Add the metadata** to the `src/` property group in `Directory.Build.props`:
  - `PackageProjectUrl` and `RepositoryUrl` = `https://github.com/hurshah7862/RagDotNet`, `RepositoryType` = `git`, `PublishRepositoryUrl` = `true`.
  - `IncludeSymbols` = `true`, `SymbolPackageFormat` = `snupkg`, `EmbedUntrackedSources` = `true`.
  - `ContinuousIntegrationBuild` = `true` when `'$(GITHUB_ACTIONS)' == 'true'`.
  - `PackageTags` = `rag;retrieval-augmented-generation;ai;llm;embeddings;vector-search;semantic-search;Microsoft.Extensions.AI` (shared; packages do not add their own).
  - `MinVerTagPrefix` = `v` (in the unconditional group, so every project versions alike).
  - SourceLink needs no package: the .NET 8+ SDK includes it for GitHub.

- [ ] **Step 4: Pack and run the check again**

Run: as Step 2.
Expected: PASS (exit 0, no failure lines).

- [ ] **Step 5: Prove the version rule and the id rule bite.** Create a lightweight local tag `v9.8.7` on HEAD, pack into `artifacts/tagged`, run the check with `--version 9.8.7` → PASS and Core is `9.8.7`, Lucene `9.8.7-preview`; run it with `--version 9.8.8` → FAIL naming Core. Then pack `tests/RagDotNet.Evaluation` with `-p:IsPackable=true` into the same directory and run the check → FAIL naming `RagDotNet.Evaluation` as an unexpected package. Delete the tag (`git tag -d v9.8.7`) and both scratch dirs. Record the outputs in the ledger.

- [ ] **Step 6: Commit** — `Directory.Build.props`, `eng/check-packages.cs`; add `artifacts/` to `.gitignore` if it is not ignored. Message: `build: package metadata, symbols, v-prefixed MinVer tags, and a package check`.

### Task 2: A README in every package

**Files:**
- Create: `src/RagDotNet.Core/README.md`, `src/RagDotNet.Readers.Pdf/README.md`, `src/RagDotNet.Readers.Pdf.Rendering/README.md`, `src/RagDotNet.Readers.Docx/README.md`, `src/RagDotNet.Readers.Html/README.md`, `src/RagDotNet.Ocr.Tesseract/README.md`
- Modify: `Directory.Build.props`, `eng/check-packages.cs`, the `.csproj` of Hosting, DataIngestion, Keyword.Lucene and VectorData.Sqlite (remove their own `PackageReadmeFile` and README `None` item)

**Interfaces:**
- Consumes: `eng/check-packages.cs` from Task 1.

- [ ] **Step 1: Add README checks** to `eng/check-packages.cs`: the nuspec has `<readme>README.md</readme>`; the package contains `README.md` at its root; the README has no relative Markdown link or image (every `](target)` target starts with `http://`, `https://` or `#`) — nuget.org cannot resolve relative ones.

- [ ] **Step 2: Pack and run the check; watch it fail**
Expected: FAIL for exactly the six packages above, "no README"; the four that have one pass.

- [ ] **Step 3: Wire READMEs centrally.** In the `src/` group of `Directory.Build.props`: `PackageReadmeFile` = `README.md` and `<None Include="README.md" Pack="true" PackagePath="/" />`; remove the per-project copies from the four `.csproj` files. Write the six READMEs, each following the shape of the existing `src/RagDotNet.Hosting/README.md`: one-paragraph what it is, `dotnet add package <Id>`, a minimal registration example using that package's real public extension method (read its `*Extensions.cs` and `PublicAPI.Shipped.txt`; never invent an API), any platform note (Tesseract: Windows binaries included, Linux/macOS system Tesseract, language data not included; Rendering: PDFium natives for Windows, Linux and macOS), and an absolute link to the matching `docs/*.md` on GitHub (`https://github.com/hurshah7862/RagDotNet/blob/main/docs/...`). Core's README covers install, the builder quick start and the DI quick start from the root README, trimmed.

- [ ] **Step 4: Pack and run the check again**
Expected: PASS.

- [ ] **Step 5: Commit** — message: `docs: a README in every package, packed from one place`.

### Task 3: CI pack, the publish workflow, and release docs

**Files:**
- Modify: `.github/workflows/ci.yml`, `README.md` (root)
- Create: `.github/workflows/publish.yml`, `docs/publishing.md`

**Interfaces:**
- Consumes: `eng/check-packages.cs` (Task 1, with Task 2's checks).

- [ ] **Step 1: CI packs.** In `ci.yml`'s `build-and-test` job: `actions/checkout@v4` with `fetch-depth: 0`; after the test steps, `dotnet pack --no-build --configuration Release --output artifacts/packages`, then `dotnet run eng/check-packages.cs -- artifacts/packages`, then `actions/upload-artifact@v4` named `packages` with `artifacts/packages/*` (retention 7 days).

- [ ] **Step 2: Publish workflow** `.github/workflows/publish.yml`:
  - `on: push: tags: ['v*']`; one job `publish`, `runs-on: ubuntu-latest`, `environment: nuget`, `permissions: contents: read, id-token: write`.
  - Checkout with `fetch-depth: 0`; setup-dotnet as in `ci.yml`; install Tesseract as in `ci.yml`; restore; build Release; run the same unit-test steps as `build-and-test` (copy the list, not the store suite).
  - Pack to `artifacts/packages`; run the check with `--version ${GITHUB_REF_NAME#v}` — a tag MinVer did not pick up fails here, before anything is pushed.
  - `NuGet/login@v1` with `user: ${{ vars.NUGET_USER }}` → step output `NUGET_API_KEY`; then `dotnet nuget push "artifacts/packages/*.nupkg" --api-key <that output> --source https://api.nuget.org/v3/index.json --skip-duplicate` (the `.snupkg` files go up with their packages).
  - A comment at the top: what has to exist before the first tag (see `docs/publishing.md`).

- [ ] **Step 3: Validate both workflows** with `actionlint` (run its Docker image `rhysd/actionlint:1.7.7` over `.github/workflows`).
Expected: no findings.

- [ ] **Step 4: Docs.** `docs/publishing.md`: one-time setup (on nuget.org, a trusted publishing policy for owner `hurshah7862`, repository `RagDotNet`, workflow `publish.yml`, environment `nuget`; in GitHub, repo variable `NUGET_USER` = the nuget.org user name; optionally required reviewers on the `nuget` environment), how to release (`git tag v1.0.0 && git push origin v1.0.0`), what the job checks, versioning (MinVer, `v` prefix, preview suffix rule), and that re-running a failed publish is safe. Root `README.md`: the "NuGet packages" status row becomes "Packed and checked in CI; not published yet ([publishing](docs/publishing.md))", and the Install section lists the ten packages with one line each, keeping "no package is on NuGet yet" until the first publish.

- [ ] **Step 5: Commit** — message: `ci: pack and check packages on every build, publish on v* tags`.

- [ ] **Step 6: Verify in CI** after the PR is open: the `build-and-test` job's pack and check steps pass and the `packages` artifact holds 10 `.nupkg` and 10 `.snupkg` files. (The publish workflow cannot run until a tag is pushed, which this stage does not do.)
