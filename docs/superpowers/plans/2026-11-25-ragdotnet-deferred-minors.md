# Deferred Minors Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the minors deferred by the Stage 9 slice 2 (DataIngestion) and Stage 10 (packaging) final reviews.

**Architecture:** Two independent areas. In `RagDotNet.DataIngestion`: heading paths found in document order (no collision), reader selection by content type first (as Core's readers do), docs that steer to `HeaderChunker`, and the missing tests. In packaging: symbols pushed in their own step, a stricter package check, and doc corrections.

**Tech Stack:** .NET 10 SDK, xUnit v3 (MTP), Microsoft.Extensions.DataIngestion 10.11.1-preview.1.26569.2, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md` (§4 packages, §5.11 OCR, §10). Findings come from the slice 2 and Stage 10 final reviews.

## Global Constraints

- Public API changes go through `PublicAPI.Unshipped.txt`; none are expected.
- `RagDotNet.DataIngestion` stays a preview package; Core is untouched.
- Unit tests need no model, network or Docker.
- Build and test through Docker (`dn.sh`); local `dotnet` is blocked by App Control.

## Review Focus

1. **A real heading named like a chain** (`# A ## B`, a single heading whose text contains `##`) next to a real chain `# A` → `## B`: each chunk must get the path of the section it came from. Pinned in Task 1.
2. **An upload whose content type disagrees with its extension** (`notes.md` sent as `text/plain`): a DataIngestion reader registered for `.md` and `text/markdown` must not take it; RagDotNet's plain-text reader does. A reader registered with extensions only still matches by extension. Pinned in Task 2.
3. **An image read by OCR through a DataIngestion reader:** its text lands in a chunk and is retrievable. Pinned in Task 3.
4. **A partly failed publish re-run:** a package whose `.nupkg` is already on nuget.org still gets its `.snupkg` pushed. Pinned in Task 4 (workflow shape; it cannot run before a release).
5. **A README relative link written as a reference link or HTML:** the check fails on it. Pinned in Task 4.

---

### Task 1: Heading paths in document order

**Files:**
- Modify: `src/RagDotNet.DataIngestion/DataIngestionChunker.cs` (`ChunkAsync`, `ToIngestionDocument`)
- Modify: `docs/dataingestion.md` (Chunkers section)
- Test: `tests/RagDotNet.DataIngestion.Tests/DataIngestionChunkerTests.cs`

- [ ] **Step 1: Write the failing tests**
  - `Heading_named_like_a_chain_gets_its_own_path`: a `ParsedDocument` with blocks: Heading `# A ## B` (path `["A ## B"]`), Paragraph `first`, Heading `# A` (path `["A"]`), Heading `## B` (path `["A","B"]`), Paragraph `second`; chunk with `HeaderChunker`. Assert the chunk containing `first` has path `["A ## B"]` and the chunk containing `second` has path `["A","B"]`.
  - `SectionChunker_labels_every_chunk_with_the_first_heading`: the `Handbook` document chunked with `SectionChunker`; assert every chunk's path is the first heading's path. This pins documented behaviour, it is not a fix.

- [ ] **Step 2: Run them** — `dn.sh test --project tests/RagDotNet.DataIngestion.Tests -- --filter-method "*chain*"` → FAIL: `second` gets `["A ## B"]` (first registration wins). The SectionChunker test passes from the start (it pins behaviour).

- [ ] **Step 3: Implement.** Replace the context → path dictionary with a list of `(string Key, IReadOnlyList<string> Path)` in document order. In `ChunkAsync`, keep a cursor: for each chunk, take the first entry at or after the cursor whose key equals the context and move the cursor to it; if none is found from the cursor, search the whole list from the start (chunkers that emit out of order); no match gives an empty path.

- [ ] **Step 4: Run the DataIngestion tests** — `dn.sh test --project tests/RagDotNet.DataIngestion.Tests` → all pass.

- [ ] **Step 5: Docs.** In `docs/dataingestion.md` Chunkers: recommend `HeaderChunker`; say `SectionChunker` labels every chunk with the document's first heading, so its chunks all get that path, and `DocumentTokenChunker` joins blocks without a separator and gives no heading paths, so neither is recommended. Replace "found by lookup, so headings containing `#` work" with "found by lookup in document order".

- [ ] **Step 6: Commit** — `fix(dataingestion): match heading paths in document order; steer docs to HeaderChunker`.

### Task 2: Reader selection by content type first

**Files:**
- Modify: `src/RagDotNet.DataIngestion/DataIngestionReader.cs` (`CanRead`), `docs/dataingestion.md` (reader paragraph), the XML doc of `AddDataIngestionReader` if it describes matching
- Test: `tests/RagDotNet.DataIngestion.Tests/DataIngestionReaderTests.cs`, `EndToEndTests.cs`

- [ ] **Step 1: Write the failing tests**
  - `CanRead_uses_the_content_type_when_one_is_given`: reader for `[".md"]`, `["text/markdown"]`: `CanRead("text/plain", "notes.md")` is false; `CanRead("text/markdown; charset=utf-8", "notes.txt")` is true; `CanRead(null, "NOTES.MD")` is true.
  - `CanRead_without_content_types_matches_by_extension`: reader for `[".md"]` and no content types: `CanRead("text/plain", "notes.md")` is true.
  - `Mismatched_upload_goes_to_the_builtin_reader` (EndToEnd): register a `FakeReader` for `.md`/`text/markdown` whose document says `from the fake`; ingest `notes.md` with content type `text/plain` and body `from plain text`; retrieval finds `from plain text`, never `from the fake`.

- [ ] **Step 2: Run them** → the first and third FAIL (extension wins today); the second passes.

- [ ] **Step 3: Implement** `CanRead`: when a content type is given and the reader has content types, the bare content type alone decides; otherwise the extension decides.

- [ ] **Step 4: Run the DataIngestion tests** → all pass.

- [ ] **Step 5: Docs** — the reader paragraph in `docs/dataingestion.md` states the rule: content type when one is given and the reader lists content types, otherwise the file extension, as RagDotNet's own readers do.

- [ ] **Step 6: Commit** — `fix(dataingestion): choose readers by content type first, like Core's readers`.

### Task 3: OCR end to end through a DataIngestion reader

**Files:**
- Test: `tests/RagDotNet.DataIngestion.Tests/EndToEndTests.cs`

- [ ] **Step 1: Write the test** `Image_text_is_read_by_ocr_and_retrievable`: a `FakeReader` returning a document with a paragraph `Quarterly report` and an `IngestionDocumentImage("![chart](x)") { Content = [1, 2, 3], MediaType = "image/png", AlternativeText = "chart" }`; register an `IOcrProvider` fake (private class in the test file, `Id = "fake-ocr/1"`, returning `new OcrResult("Revenue grew 12 percent", 0.95)`) with `AddOcrProvider<T>()`; ingest; assert `IngestResult.Warnings` is empty and `RetrieveAsync("Revenue grew")` returns a chunk containing `Revenue grew 12 percent`.

- [ ] **Step 2: Run it.** This covers existing behaviour, so it may pass at once. Then prove it can fail: make the fake return empty text and confirm the test fails (an `OcrFailed`/empty-text warning or no hit), restore it, and record both runs in the ledger. If it fails on the first run, that is a bug: use systematic debugging and fix it test-first.

- [ ] **Step 3: Commit** — `test(dataingestion): OCR end to end through a DataIngestion reader`.

### Task 4: Packaging hardening

**Files:**
- Modify: `eng/check-packages.cs`, `.github/workflows/publish.yml`, `docs/publishing.md`, `src/RagDotNet.Readers.Pdf.Rendering/README.md`

- [ ] **Step 1: Stricter checks in `eng/check-packages.cs`:**
  - README links: also check reference definitions (`^\s*\[[^\]]+\]:\s*<?(\S+?)>?(\s|$)`, multiline) and HTML attributes (`(?:src|href)\s*=\s*["']([^"']+)["']`), with the same allowed prefixes (`http://`, `https://`, `#`).
  - Symbols: open each `.snupkg` and require `lib/<tfm>/<Id>.pdb` for net8.0 and net10.0.

- [ ] **Step 2: Prove they bite.** Append to `src/RagDotNet.Hosting/README.md` a reference link `[d]: ../docs/x.md` and `<img src="img/x.png">`, pack Hosting into a scratch folder beside a full pack, run the check → FAIL naming both links; restore the README. Copy a `.snupkg`, remove its `lib/net8.0/*.pdb` entry with Python's zipfile into a scratch folder of packages, run the check → FAIL naming the missing pdb. Record outputs in the ledger. Then a clean pack + check → PASS.

- [ ] **Step 3: Symbols in their own push** (`publish.yml`): push `*.nupkg` with `--no-symbols --skip-duplicate`, then push `*.snupkg` in a second step with `--skip-duplicate`, so a re-run pushes symbols even for packages already on nuget.org. Lint with `rhysd/actionlint:1.7.7` → no findings.

- [ ] **Step 4: Docs.**
  - `docs/publishing.md`: the re-run paragraph says symbols are pushed separately and re-runs fill them in; the local-run line says to use an empty folder (old packages beside new ones fail the check as duplicates); the check list mentions the pdb check and the link forms.
  - `src/RagDotNet.Readers.Pdf.Rendering/README.md`: "at 300 DPI by default (`OcrOptions.PageDpi`, 72–1200)" — confirm the property name and range in `src/RagDotNet.Core/Ocr/OcrOptions.cs` first.

- [ ] **Step 5: Commit** — `build(packaging): check symbols and every README link form; push symbols separately`.
