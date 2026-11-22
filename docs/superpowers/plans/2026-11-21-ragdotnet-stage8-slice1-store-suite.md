# Stage 8 slice 1: shared store suite and CI store job (InMemory) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One shared integration suite, written once against RagDotNet's public API, that later slices run against every vector store; in this slice it runs against the in-memory store (String and Guid keys) in a new CI job on every pull request.

**Architecture:** A new test project `tests/RagDotNet.StoreTests` holds an abstract generic `StoreSuite<TFixture>` (split into partial files by area) and a `StoreFixture` base class each store implements. Concrete classes such as `InMemoryStringKeyTests` bind the suite to a fixture and a key type. A small `StoreGate` decides run / skip / fail, so CI turns a store that can't start into a failure. One Core change: the manifest collection's placeholder vector uses the configured distance function.

**Tech Stack:** .NET 10 SDK (net8.0 + net10.0 targets), xUnit v3 on Microsoft.Testing.Platform, Microsoft.Extensions.VectorData 10.8.2, CommunityToolkit.VectorData.InMemory 1.0.1, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md` (and the main design `docs/superpowers/specs/2026-10-18-ragdotnet-design.md` §8, §9.2).

## Global Constraints

- Branch `stage8-slice1-suite` from `main`; the first commit adds the stage 8 spec (currently untracked in the main checkout) and this plan.
- Build and test in Docker: `./docker/sdk/run.ps1 test --project <proj> ...` (local `dotnet` is blocked by App Control). Quote comma lists in PowerShell.
- The suite drives RagDotNet only through its public API (`RagPipelineBuilder`, `IRagPipeline`, `IRagAdmin`); no `InternalsVisibleTo` for the new project.
- The suite uses the deterministic `FakeEmbeddingGenerator` (64 dimensions); no Ollama, no network.
- In CI a store test that cannot start its store **fails, never skips** (`RAGDOTNET_STORES_REQUIRED=1`).
- Locally, stores needing an external service run only with `RAGDOTNET_INTEGRATION=1`; InMemory needs nothing and always runs *(plan's call)*.
- Every test uses its own fresh collection name: `"rag" + 12 lowercase hex chars` (letters and digits only, starts with a letter, 15 chars), valid for all six stores including the `_documents` suffix Core adds.
- xUnit analyzers: pass `TestContext.Current.CancellationToken` to every async call (xUnit1051).
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; the PR body ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

## Review Focus

1. **A required store that can't start silently skips** → CI must fail with a message naming the store and the reason. Pinned by `StoreGateTests` (Task 2).
2. **The whole store-tests project silently left out of a CI run** (this project has been bitten before) → the CI step uses `--minimum-expected-tests` so a run with too few tests fails. Pinned in Task 7.
3. **A document without a field matches a filter on it** (notably `Eq(bool, false)` against a missing bool) → it never matches. Pinned by the `bare` document in the filter table (Task 4).
4. **Metadata comes back with a different type or instant** (`long` read as `int`, `DateTimeOffset` shifted) → retrieved metadata equals what was ingested. Pinned by `Metadata_round_trips_with_its_type` (Task 4).
5. **Listing stops at a page boundary** → stats, reindex and keyword rebuild see every document when there are more than two pages (100 per page). Pinned by `Listing_sees_every_document_across_pages` with 201 documents (Task 3).

---

### Task 1: Branch, spec commit, and manifest placeholder distance

**Files:**
- Modify: `src/RagDotNet.Core/Storage/RecordDefinitions.cs` (`Manifests`, the `Placeholder` property)
- Test: `tests/RagDotNet.Core.Tests/StorageSchemaTests.cs`

**Interfaces:**
- Produces: `RecordDefinitions.Manifests(o)` gives `Placeholder` the distance function `o.DistanceFunction` (and validates it like `Chunks` does).

- [ ] **Step 1: Create the branch and commit the spec**

```bash
git switch -c stage8-slice1-suite
git add docs/superpowers/specs/2026-11-21-ragdotnet-stage8-stores-design.md docs/superpowers/plans/2026-11-21-ragdotnet-stage8-slice1-store-suite.md
git commit -m "docs: stage 8 store integration design and slice 1 plan"
```

- [ ] **Step 2: Write the failing test** in `StorageSchemaTests`

```csharp
[Theory]
[InlineData(DistanceFunction.CosineSimilarity)]
[InlineData(DistanceFunction.CosineDistance)]
public void Manifest_placeholder_uses_the_configured_distance_function(string distance)
{
    var o = Options();
    o.DistanceFunction = distance;
    var p = Assert.IsType<VectorStoreVectorProperty>(RecordDefinitions.Manifests(o).Properties.Single(x => x.Name == "Placeholder"));
    Assert.Equal(distance, p.DistanceFunction);
}

[Fact]
public void Manifest_definition_rejects_an_unsupported_distance_function()
{
    var o = Options();
    o.DistanceFunction = DistanceFunction.EuclideanDistance;
    Assert.Throws<ArgumentException>(() => RecordDefinitions.Manifests(o));
}
```

- [ ] **Step 3: Run and see the CosineDistance case and the rejection test fail**

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.Core.Tests --framework net10.0 --filter-method "*Manifest_placeholder*" --filter-method "*rejects_an_unsupported_distance*"`
Expected: 2 failures (CosineDistance theory row; no exception thrown).

- [ ] **Step 4: Implement** — in `Manifests`, call `ValidateDistance(o.DistanceFunction)` first and set `DistanceFunction = o.DistanceFunction` on `Placeholder`.

- [ ] **Step 5: Run the whole Core test project on both frameworks**

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.Core.Tests`
Expected: all pass (net8.0 and net10.0).

- [ ] **Step 6: Commit** — `fix(storage): the manifest placeholder vector uses the configured distance function`

---

### Task 2: The store-tests project, fixture, gate and suite skeleton

**Files:**
- Create: `tests/RagDotNet.StoreTests/RagDotNet.StoreTests.csproj`
- Create: `tests/RagDotNet.StoreTests/Infrastructure/StoreFixture.cs`
- Create: `tests/RagDotNet.StoreTests/Infrastructure/StoreGate.cs`
- Create: `tests/RagDotNet.StoreTests/Infrastructure/HybridPathCapture.cs`
- Create: `tests/RagDotNet.StoreTests/Suite/StoreSuite.cs`
- Create: `tests/RagDotNet.StoreTests/Stores/InMemory.cs`
- Create: `tests/RagDotNet.StoreTests/StoreGateTests.cs`
- Modify: `RagDotNet.slnx` (add the project under the tests folder)

**Interfaces:**
- Produces (used by Tasks 3–6 and by later slices):

```csharp
namespace RagDotNet.StoreTests.Infrastructure;

public abstract class StoreFixture : IAsyncLifetime
{
    public abstract string Name { get; }                  // "InMemory", later "SQLite", "PostgreSQL", ...
    public virtual bool NativeHybrid => false;            // expected rag.hybrid.path: "native" when true, else "index"
    public string? UnavailableReason { get; protected set; }  // set by InitializeAsync when the store can't start
    public abstract VectorStore CreateStore();             // a store instance for one test
    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class StoreGate
{
    public const string RequiredVariable = "RAGDOTNET_STORES_REQUIRED";
    public const string IntegrationVariable = "RAGDOTNET_INTEGRATION";
    // null reason → returns normally. reason and required → throws InvalidOperationException
    // "Store '{store}' is required in this run ({RequiredVariable}=1) but could not start: {reason}".
    // reason and not required → Assert.Skip(reason).
    public static void Check(string store, string? unavailableReason, bool required);
    public static bool Required => Environment.GetEnvironmentVariable(RequiredVariable) == "1";
    public static bool IntegrationEnabled => Environment.GetEnvironmentVariable(IntegrationVariable) == "1";
}

namespace RagDotNet.StoreTests.Suite;

public abstract partial class StoreSuite<TFixture> : IClassFixture<TFixture> where TFixture : StoreFixture
{
    protected StoreSuite(TFixture fixture, RagKeyType keyType);   // calls StoreGate.Check(fixture.Name, fixture.UnavailableReason, StoreGate.Required)
    protected TFixture Fixture { get; }
    protected RagKeyType KeyType { get; }
    protected VectorStore Store { get; }            // fixture.CreateStore(), once per test
    protected string Collection { get; }            // "rag" + Guid.NewGuid().ToString("N")[..12]
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    // UseEmbeddingGenerator(embedder ?? new FakeEmbeddingGenerator(64)), UseChatClient(new FakeChatClient()),
    // UseVectorStore(Store, Collection, 64), Configure(o => { o.KeyType = KeyType; o.ModelCheckInterval = TimeSpan.Zero; configure?.Invoke(o); }).
    protected RagPipelineBuilder Builder(Action<RagOptions>? configure = null, IEmbeddingGenerator<string, Embedding<float>>? embedder = null);

    protected static DocumentSource Md(string id, string markdown, IDictionary<string, object?>? metadata = null);  // as TestRag.Md

    // Vector-only retrieve (Hybrid = Off) with Filter Eq("DocumentId", id) and TopK 1000, distinct ChunkKey count.
    protected static Task<int> CountChunksAsync(IRagPipeline rag, string documentId);
}
```

- Concrete classes in `Stores/InMemory.cs`:

```csharp
public sealed class InMemoryFixture : StoreFixture { Name => "InMemory"; CreateStore() => new InMemoryVectorStore(); }
public sealed class InMemoryStringKeyTests(InMemoryFixture f) : StoreSuite<InMemoryFixture>(f, RagKeyType.String);
public sealed class InMemoryGuidKeyTests(InMemoryFixture f) : StoreSuite<InMemoryFixture>(f, RagKeyType.Guid);
```

- `HybridPathCapture : IDisposable` — an `ActivityListener` on `RagTelemetry.ActivitySourceName` that records the `rag.hybrid.path` tag of `rag.retrieve` spans started under its own root activity (same approach as `tests/RagDotNet.Core.Tests/Fakes/TelemetryCapture.cs`, so parallel tests don't see each other's spans). Exposes `IReadOnlyList<string> Paths`.

- [ ] **Step 1: Create the project.** Copy `tests/RagDotNet.Core.Tests/RagDotNet.Core.Tests.csproj` minus the DI/hosting/configuration/diagnostics-testing packages, the TestAssets copy and `Png.cs`; reference only `src/RagDotNet.Core`; `TargetFrameworks` `net8.0;net10.0`. Link the fakes instead of copying them:

```xml
<Compile Include="..\RagDotNet.Core.Tests\Fakes\FakeEmbeddingGenerator.cs" Link="Fakes\FakeEmbeddingGenerator.cs" />
<Compile Include="..\RagDotNet.Core.Tests\Fakes\FakeChatClient.cs" Link="Fakes\FakeChatClient.cs" />
<Compile Include="..\RagDotNet.Core.Tests\Fakes\GatedEmbeddingGenerator.cs" Link="Fakes\GatedEmbeddingGenerator.cs" />
```

Add it to `RagDotNet.slnx` next to `RagDotNet.IntegrationTests`.

- [ ] **Step 2: Write the failing gate tests** in `StoreGateTests.cs`

```csharp
[Fact] public void Available_store_runs() => StoreGate.Check("X", null, required: true);   // no throw

[Fact]
public void Unavailable_required_store_fails_naming_store_and_reason()
{
    var ex = Assert.Throws<InvalidOperationException>(() => StoreGate.Check("Qdrant", "Docker is not running", required: true));
    Assert.Contains("Qdrant", ex.Message);
    Assert.Contains("Docker is not running", ex.Message);
    Assert.Contains("RAGDOTNET_STORES_REQUIRED", ex.Message);
}

[Fact]
public void Unavailable_optional_store_skips_with_the_reason()
{
    var ex = Assert.ThrowsAny<Exception>(() => StoreGate.Check("Qdrant", "Docker is not running", required: false));
    Assert.Contains("Docker is not running", ex.Message);    // xUnit's skip exception carries the reason
    Assert.IsNotType<InvalidOperationException>(ex);
}
```

- [ ] **Step 3: Run; expect a compile failure** (`StoreGate` missing).

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests --framework net10.0`

- [ ] **Step 4: Implement** `StoreFixture`, `StoreGate`, `HybridPathCapture`, `StoreSuite` (setup members only), `InMemory.cs`, plus one smoke test in `StoreSuite.cs`:

```csharp
[Fact]
public async Task Ingest_then_retrieve_finds_the_document()
{
    var rag = Builder().Build();
    var r = await rag.IngestAsync(Md("d1", "alpha beta gamma"), cancellationToken: Ct);
    Assert.Equal(IngestStatus.Created, r.Status);
    Assert.Equal("d1", Assert.Single(await rag.RetrieveAsync("alpha beta gamma", cancellationToken: Ct)).DocumentId);
}
```

- [ ] **Step 5: Run; expect the 3 gate tests and 2 smoke tests (String, Guid) to pass on both frameworks**

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests`

- [ ] **Step 6: Commit** — `test(stores): shared store suite skeleton, store gate, in-memory fixture`

---

### Task 3: Lifecycle and listing

**Files:**
- Create: `tests/RagDotNet.StoreTests/Suite/StoreSuite.Lifecycle.cs`

**Interfaces:**
- Consumes: `StoreSuite` members from Task 2.

Documents with a controlled chunk count use `IngestOptions { Chunking = new() { Chunker = Chunkers.FixedSize, MaxTokens = 16, OverlapRatio = 0 } }` and text of distinct words (`"w0 w1 w2 ..."`), so more words means more chunks. Assertions compare with the `IngestResult.ChunkCount` returned, never a hard-coded count.

- [ ] **Step 1: Write the tests**

| Test | Assertions |
|---|---|
| `Unchanged_reingest_writes_nothing` | second ingest of the same source → `IngestStatus.Unchanged`; the embedder's `CallCount` is the same as after the first ingest |
| `Update_with_fewer_chunks_leaves_no_orphans` | ingest 200 words, then 40 words of the same id → `Updated`, second `ChunkCount` < first, `CountChunksAsync == second ChunkCount` |
| `Update_with_more_chunks_stores_all_of_them` | 40 then 200 words → `CountChunksAsync == second ChunkCount` |
| `Delete_removes_document_and_chunks` | `DeleteDocumentAsync` → true; `CountChunksAsync == 0`; second delete → false; stats `DocumentCount == 0` |
| `Stats_count_documents_and_chunks` | three documents → stats single partition with `DocumentCount == 3`, `ChunkCount ==` sum of ingest counts; `EmbeddingModelIds == ["fake-embed"]` |
| `Listing_sees_every_document_across_pages` | ingest 201 one-line documents `doc000`…`doc200` (`IngestManyAsync`), each with a unique word `u000`…`u200` → stats `DocumentCount == 201`; after `admin.RebuildKeywordIndexAsync`, a `Hybrid = On` retrieve of `u200` returns `doc200` first; `admin.ReindexAsync` with the same model → `DocumentsReindexed + DocumentsSkipped == 201` |

- [ ] **Step 2: Run against InMemory; all must pass.** A failure here is a Core bug: stop, write a failing Core unit test first, fix, then continue (spec §8).

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests`

- [ ] **Step 3: Commit** — `test(stores): lifecycle, high-water cleanup and paged listing in the store suite`

---

### Task 4: Filters on every metadata type

**Files:**
- Create: `tests/RagDotNet.StoreTests/Suite/StoreSuite.Filters.cs`

Schema for every test in this file: `o.Metadata.Add<string>("s").Add<int>("i").Add<long>("l").Add<bool>("b").Add<DateTimeOffset>("d").Add<string[]>("tags")`.

Three documents, all containing the word `common`:

| Id | s | i | l | b | d (UTC) | tags |
|---|---|---|---|---|---|---|
| `full` | `"x"` | 1 | 10_000_000_000 | true | 2026-01-01T00:00Z | `["red","green"]` |
| `other` | `"y"` | 2 | 20_000_000_000 | false | 2026-06-01T00:00Z | `["blue"]` |
| `bare` | — | — | — | — | — | — |

- [ ] **Step 1: Write `Filter_matches_exactly(string name)`** — a `[Theory]` over the case names below (`MemberData` of names; the filters live in a static dictionary in the file). Retrieve `"common"` with `TopK = 10`, `Hybrid = HybridMode.Off`, the case's filter; assert the distinct ordered `DocumentId`s equal the expected set.

| Name | Filter | Expected |
|---|---|---|
| `eq-string` | `Eq("s","x")` | full |
| `eq-int` | `Eq("i",1)` | full |
| `eq-long` | `Eq("l",10_000_000_000L)` | full |
| `eq-bool-true` | `Eq("b",true)` | full |
| `eq-bool-false` | `Eq("b",false)` | other (never `bare`) |
| `eq-date` | `Eq("d", full's date)` | full |
| `eq-documentid` | `Eq("DocumentId","other")` | other |
| `in-string` | `In("s",["x","y"])` | full, other |
| `in-int` | `In("i",[1,2])` | full, other |
| `in-long` | `In("l",[20_000_000_000L])` | other |
| `contains` | `Contains("tags","red")` | full |
| `contains-any` | `ContainsAny("tags",["green","blue"])` | full, other |
| `contains-none` | `Contains("tags","purple")` | none |
| `and` | `Eq("s","x") & Eq("i",1)` | full |
| `and-disjoint` | `Eq("s","x") & Eq("i",2)` | none |
| `or` | `Eq("s","x") \| Eq("i",2)` | full, other |
| `nested` | `Or(And(Eq("b",true), Contains("tags","red")), Eq("l",20_000_000_000L))` | full, other |

- [ ] **Step 2: Write `Metadata_round_trips_with_its_type`** — retrieve `full` and assert each `Metadata[...]` value equals what was ingested and has the same CLR type (`long` stays `long`, `DateTimeOffset` equal as an instant, `string[]` sequence-equal).

- [ ] **Step 3: Run; all pass** (both key types, both frameworks). Same rule as Task 3 for Core bugs.

- [ ] **Step 4: Commit** — `test(stores): filters on every metadata type, including missing values`

---

### Task 5: Access scope

**Files:**
- Create: `tests/RagDotNet.StoreTests/Suite/StoreSuite.Access.cs`
- Create: `tests/RagDotNet.StoreTests/Infrastructure/MutableScope.cs` (same shape as `tests/RagDotNet.Core.Tests/Fakes/TestScopes.cs`: `MutableScope : IAccessScope` with settable `Current`, and `Tenant(string t, RagFilter? filter = null, RagPermissions p = RagPermissions.All)`)

Setup: `o.Metadata.Add<string>("tenant").Add<string[]>("groups"); o.PartitionKeys.Add("tenant")`, builder `.UseAccessScope(scope)`; `scope.Current = null` means full access (writes with no `tenant` go to the common area).

- [ ] **Step 1: Write the tests** — the store-facing subset of `tests/RagDotNet.Core.Tests/AccessScopeTests.cs`, with the same assertions as the test named:

| Test | Mirrors |
|---|---|
| `Same_document_id_in_two_partitions_is_isolated` | `Same_document_id_in_two_partitions_is_isolated` (ingest, retrieve, delete per tenant) |
| `Tenant_sees_own_and_common_but_not_other_tenants` | same name, including `IncludeCommon = false` → own only |
| `Tenant_cannot_delete_a_common_document` | same name |
| `Full_access_sees_every_partition` | `Null_scope_means_full_access_and_sees_every_partition` |
| `Full_access_without_common_sees_only_tenant_partitions` | same name |
| `Scope_filter_on_groups_hides_documents_without_groups` | `Scope_filter_on_groups_hides_documents_without_groups_instead_of_crashing` (scope filter `ContainsAny("groups", ["g1"])`) |
| `Admin_delete_names_the_partition` | `admin.DeleteDocumentAsync("doc1", {tenant=b})` deletes b's copy only; a's still retrievable |
| `Stats_are_per_partition` | tenants a (2 docs), b (1), common (1) → three `PartitionStats` with those counts |

- [ ] **Step 2: Run; all pass.**

- [ ] **Step 3: Commit** — `test(stores): access scope isolation, common area and per-partition stats`

---

### Task 6: Hybrid, reindex and external content

**Files:**
- Create: `tests/RagDotNet.StoreTests/Suite/StoreSuite.Retrieval.cs`

- [ ] **Step 1: Write the tests**

`Exact_term_query_finds_its_chunk_with_hybrid` — pick `target = "w" + n` and a word `twin` from `FakeEmbeddingGenerator.Colliding(target, 64)` (same bucket, so vector search ties them). Ingest `a` = `"{target} filler"` and `b` = `"{twin} filler"`. Under `HybridPathCapture`, retrieve `target` with `Hybrid = HybridMode.On`, `TopK = 2`. Assert the first hit is `a`, and `Paths` is non-empty and every entry equals `Fixture.NativeHybrid ? "native" : "index"`.

`Reindex_moves_every_chunk_to_the_new_model` — ingest `d1`, `d2`, `d3` with `Builder(o => o.EmbeddingModelId = "m1")`. A second builder over the same `Store` and `Collection` with `EmbeddingModelId = "m2"`: retrieve throws `EmbeddingModelMismatchException`; `ReindexAsync` → `DocumentsReindexed == 3`; stats `EmbeddingModelIds == ["m2"]`; retrieve `"alpha"` returns all three.

`Reads_during_reindex_see_only_reembedded_documents` — mirrors `Reads_during_reindex_return_reembedded_documents_then_all` in `tests/RagDotNet.Core.Tests/ReindexCoordinationTests.cs`: `GatedEmbeddingGenerator` pausing on `"alpha two"`; during the pause retrieve returns only `d1`; after the gate opens, `d1` and `d2`.

`External_content_round_trips_and_deletes` — `o.ContentStorage = ContentStorageMode.External`, builder `.UseContentStore(content)` with `content = new InMemoryChunkContentStore()`. Ingest → `content.Count == ChunkCount`; retrieve returns the original text; delete → `content.Count == 0` and `CountChunksAsync == 0`.

- [ ] **Step 2: Run; all pass.**

Run: `./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests`

- [ ] **Step 3: Commit** — `test(stores): hybrid path, reindex and external content in the store suite`

---

### Task 7: CI store job and docs

**Files:**
- Modify: `.github/workflows/ci.yml` (new job `store-tests`)
- Modify: `docs/superpowers/specs/2026-10-18-ragdotnet-design.md:82` and `:568`
- Create: `docs/vector-stores.md`
- Modify: `README.md` (the "More vector stores | Planned" row and the store sentence at line 42)

- [ ] **Step 1: Count the suite.** Run the project and note the total per framework (`./docker/sdk/run.ps1 test --project tests/RagDotNet.StoreTests --framework net8.0`).

- [ ] **Step 2: Add the CI job**

```yaml
  store-tests:
    name: Store integration tests (net8.0)
    runs-on: ubuntu-latest
    timeout-minutes: 30
    env:
      DOTNET_NOLOGO: true
      DOTNET_CLI_TELEMETRY_OPTOUT: true
      RAGDOTNET_INTEGRATION: 1
      # A store that cannot start fails the run instead of skipping.
      RAGDOTNET_STORES_REQUIRED: 1
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
          dotnet-version: 8.0.x
      - name: Build
        run: dotnet build tests/RagDotNet.StoreTests --configuration Release
      # The floor fails the run if tests go missing; raise it whenever a slice adds a store or a test.
      - name: Test stores (net8.0)
        run: dotnet test --project tests/RagDotNet.StoreTests --no-build --configuration Release --framework net8.0 --minimum-expected-tests <count from Step 1>
```

Verify `--minimum-expected-tests` locally first: run with the count (passes) and with count + 1 (fails, exit code non-zero). If the option is not accepted, stop and report; don't drop the guard.

- [ ] **Step 3: Update the main design.** Line 82 becomes: "Unit tests must never need a model, network access, or Docker. Store integration tests run in CI on every pull request and may use Docker (stage 8, see [store design](2026-11-21-ragdotnet-stage8-stores-design.md))." Line 568 adds: "In CI, `RAGDOTNET_STORES_REQUIRED=1` turns a store that can't start into a failure."

- [ ] **Step 4: Write `docs/vector-stores.md`** — the page the spec §8 asks for: a table (store, package, key types, hybrid, status) with the InMemory row filled and the other five listed as "coming in stage 8"; a "Per-store settings" section stating spec §2.2 (settings are documented per store, a wrong one fails at startup naming the setting); a "How stores are tested" section (the shared suite, both key types, CI fails instead of skipping, how to run locally). Link it from README line 42 and change the README row to `| More vector stores | In progress ([vector stores](docs/vector-stores.md)) |`.

- [ ] **Step 5: Run the whole solution** in Docker and record the totals.

Run: `./docker/sdk/run.ps1 test RagDotNet.slnx`
Expected: 0 failed; the total is the previous 1,580 plus the new Core and store tests.

- [ ] **Step 6: Commit, push, open the PR** — commit `ci: run the store suite on every pull request; docs for vector stores`; PR "Stage 8 slice 1: shared store suite and CI store job"; confirm the `store-tests` job ran and reported the expected test count before calling it done.
