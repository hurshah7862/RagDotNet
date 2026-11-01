# RagDotNet Stage 1.1 — Access Model, Reindex Coordination, Custom Chunkers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Change RagDotNet.Core's public API and behaviour to spec revision 2.1. That means:
- an optional access scope, where no scope means full access, and writes go to the tenant a document names or else to a common area;
- per-user permissions;
- "you can't change what you can't read";
- filters that never crash on missing metadata values;
- a reindex that coordinates with concurrent writes while questions keep working;
- pluggable, named chunkers.

**Architecture:** The work is mostly in existing Core types.
- **Storage:** chunks get a `Partition` field. Manifests store every declared metadata value.
- **Access:** `ScopeResolver` resolves each call to either full access or a validated scope with permissions.
- **Reads:** they filter on `Partition == own OR Partition == ""`.
- **Write checks:** they use an in-memory `RagFilterEvaluator` over the stored manifest's metadata.
- **Reindex:** it writes a marker record into the manifest collection. `EmbeddingModelGuard` reads that marker on a timed cache and decides per operation kind (read or write). During a reindex, reads add `EmbeddingModelId == configured` to the store filter.
- **Chunkers:** a `ChunkerRegistry` maps names to `IChunker`, and a `ChunkOutputGuard` normalizes every chunker's output.

**Tech Stack:** .NET 10 SDK, `net8.0;net10.0`, Microsoft.Extensions.AI 10.11.0, Microsoft.Extensions.VectorData.Abstractions 10.8.2, xUnit v3, CommunityToolkit.VectorData.InMemory 1.0.1.

**Spec:** `docs/superpowers/specs/2026-10-18-ragdotnet-design.md`, revision 2.1. This plan covers §10 stage 1.1 and argues from §5.1, §5.2, §5.3, §5.4, §5.5, §5.8, §5.14 and §6.

## Global Constraints

- Target frameworks: `net8.0;net10.0` for every shipped package. Warnings are errors.
- Core keeps its current dependency list. **No new package references in `src/`.** `TimeProvider` comes from the BCL.
- Every public API change, whether added, removed or changed, is reflected in `src/RagDotNet.Core/PublicAPI.Unshipped.txt`. The PublicApiAnalyzers build must stay clean.
- Every public member has XML docs, including `<exception>` tags for the exceptions it documents. Messages never contain document text.
- Unit tests never need a model, network access or Docker. Use the fakes in `tests/RagDotNet.Core.Tests/Fakes` and `InMemoryVectorStore`.
- Test command: `dotnet test --project tests/RagDotNet.Core.Tests`. Both target frameworks must pass. Build command: `dotnet build` (0 warnings).
- Microsoft.Extensions.VectorData.Abstractions stays at **10.8.2**.
- Filters are never dropped or applied after the store query. Values convert exactly or throw.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Nothing is versioned yet. Breaking public API changes and stage 1 data incompatibility are expected; no migration code.

## Review Focus

These five inputs are the ones a person using the library is most likely to hit and that the spec doesn't spell out. Each has a test in the task that owns the code.

1. A scoped user ingests a DocumentId that exists only in the common area. Expected: a separate tenant document is created, not a refusal, and the common document is unchanged. Test in Task 4.
2. A scope filter on a `string[]` field (for example `ContainsAny("groups", …)`) is checked against a stored document whose field is null. Expected: the document is "not visible", never a `NullReferenceException`. Test in Task 4.
3. A reindex is cancelled and then rerun right away in the same process. Expected: it resumes at once instead of being refused for 5 minutes by its own marker. Test in Task 8.
4. A second instance already configured with the new model fails with a mismatch until a reindex starts. Expected: within `ModelCheckInterval` of the marker appearing, it starts answering (from re-embedded data) without a restart. Test in Task 7.
5. A custom chunker returns duplicate or non-contiguous `Index` values. Expected: the library renumbers 0..n-1, so chunk keys never collide or leave gaps. Test in Task 9.

---

## Decisions this plan makes *(builder's calls, beyond the spec)*

- **Full access with `IncludeCommon = false`:** the read is limited to tenant partitions (`Partition != ""`).
- **Single-tenant apps** (no `PartitionKeys`):
  - A scope is allowed. Its `Partition` must be empty, and `AccessScope.Unpartitioned(filter, permissions)` builds one.
  - The user's own area is the common area (`PartitionKey == ""`), so reads, write checks and delete checks work unchanged. Writes land in the one area.
  - `IncludeCommon = false` has no effect there.
  - The "scope registered but PartitionKeys is empty" validation failure is removed.
- **Full-access ingest** *(owner's decision in spec 2.1)*: a document whose metadata sets **every** partition key with a non-empty value goes to that tenant's area. One that sets **none** goes to the common area. Anything in between throws `ArgumentException`. Full access skips permission and visibility checks.
- **Permission failures throw `RagAccessDeniedException`,** including on delete. A delete of a document that exists but isn't visible returns `false`, and so does a delete of a document that doesn't exist.
- **Stored "visibility" uses an in-memory evaluator:** `RagFilterEvaluator` checks the scope filter against the manifest's stored metadata. A null field never matches `Eq`, `In`, `Contains` or `ContainsAny`.
- **Reindex marker layout:** the marker is a manifest-shaped record with the reserved key `|reindex`. No escaped DocumentId can start with `|`. Its fields:

  | Field | Value |
  |---|---|
  | `ChunkCount` | `-1`, so every existing `ChunkCount >= 0` listing skips it |
  | `EmbeddingModelId` | the target model |
  | `SourceName` | the owner id |
  | `UpdatedAtUnixMs` | the heartbeat |
  | `ContentHash` | `"reindex-marker"` |
  | `DocumentId`, `Partition` | `"|reindex"` |
  | `ContentStorage` | the configured mode |
- **Reindex ownership:**
  - The owner id is the engine's `InstanceId`, a Guid created once per engine. Different processes get different ids; a rerun in the same process resumes its own marker.
  - Within one process, `RagEngine.ReindexGate` (a `SemaphoreSlim(1,1)` taken with a zero timeout) refuses a second concurrent reindex.
  - The heartbeat is refreshed after a document whenever 60 s have passed since the last one. A marker is stale when its heartbeat is more than 5 minutes old.
- **Changes from another process during reindex:** if a document's manifest changed between the re-read and a batch write, that document is abandoned. At the end, if any document is still not on the configured model, the marker is left in place and reindex throws `InvalidOperationException` asking for a rerun.
- **Dimension probe:** reindex always probes the embedding dimensions first, before writing the marker.
- **`ModelCheckInterval`:**
  - `TimeSpan.Zero` means check on every call; negative values fail validation.
  - The guard caches the observed state, whatever the outcome, for the interval. `Reset()` clears it.
  - The content-mode check stays cached forever once it passes.
- **Reads during reindex** *(owner's decision in spec 2.1)*:
  - No exception: the store filter gains `EmbeddingModelId == configured`, so only re-embedded data is searched.
  - One warning per process per marker (keyed on the marker's owner id, re-armed once no marker is seen).
  - The `rag.retrieve` span gets the tag `rag.reindex.in_progress = true`.
- **Null-safe filters:**
  - An omitted `string[]` value is stored as an empty array (`[]`). `Contains`/`ContainsAny` then never see null, and the query keeps the shape stores already translate.
  - Value-type fields (`int`, `long`, `bool`, `DateTimeOffset`) are compared through their nullable type: `(T?)r["meta_x"] == (T?)value`.
  - Stage 8 checks that every supported store accepts the nullable form.
- **Time source:** `RagEngine.Time` is a `TimeProvider`. The builder sets it with an internal `UseTimeProvider`; DI uses `sp.GetService<TimeProvider>() ?? TimeProvider.System`.
- **`ForScope`:**
  - It validates the scope immediately and throws `ArgumentException` for an invalid scope.
  - `RagAdmin` takes the optional `IChatClient`, so a `ForScope` pipeline can answer questions.
- **Chunker constants:** only `Chunkers.StructureAware` (`"structure-aware"`) is added now. `FixedSize` and `Semantic` arrive in stage 4 with their implementations.
- **Built-in chunker:** `StructureAwareChunker` gets Name `"structure-aware"` and Version `"1"`.
- **Chunker output:**
  - The library recomputes every chunk's `TokenCount` with its own tokenizer.
  - Oversized chunks are split with `TokenCounter.SplitByTokens` and keep the original's heading path and pages.
  - A chunker that returns `null`, or a list with a `null` element, fails with `InvalidOperationException` naming the chunker.
- **App chunkers are singletons:** they are resolved from the root provider by the options validator.

## File map

| File | Change | Task |
|---|---|---|
| `src/RagDotNet.Core/Storage/RecordDefinitions.cs` | chunk `Partition`; manifest declares every metadata field | 1 |
| `src/RagDotNet.Core/Storage/ManifestRecord.cs` | `Metadata` dictionary; common-area records | 1 |
| `src/RagDotNet.Core/Storage/ManifestStore.cs` | `FromDynamic` gets the schema; marker get/upsert/delete | 1, 7 |
| `src/RagDotNet.Core/Ingestion/Ingestor.cs` | write `Partition`/manifest metadata; full-access tenant routing and scoped rules; permission and visibility checks; `[]` for omitted arrays; chunker registry; output guard | 1, 2, 4, 6, 9 |
| `src/RagDotNet.Core/Retrieval/Retriever.cs` | key from `Partition`; read filter; `IncludeCommon`; Read permission; read guard and model clause during reindex | 1, 3, 7 |
| `src/RagDotNet.Core/Retrieval/RetrievalOptions.cs` | `IncludeCommon` | 3 |
| `src/RagDotNet.Core/Access/RagPermissions.cs` | new | 2 |
| `src/RagDotNet.Core/Access/RagAccessDeniedException.cs` | new | 2 |
| `src/RagDotNet.Core/Access/AccessScope.cs`, `IAccessScope.cs` | `Permissions`; nullable return | 2 |
| `src/RagDotNet.Core/Access/ScopeResolver.cs` | full access, validation, `ValidateExplicit` for ForScope | 2, 5 |
| `src/RagDotNet.Core/Access/UnscopedDataException.cs` | delete | 2 |
| `src/RagDotNet.Core/Access/ReadFilters.cs` | new: partition + scope + caller read expression; optional model clause | 3, 7 |
| `src/RagDotNet.Core/Filtering/RagFilterEvaluator.cs` | new: in-memory evaluation | 4 |
| `src/RagDotNet.Core/Filtering/RagFilterTranslator.cs` | nullable comparison for value-type fields | 6 |
| `src/RagDotNet.Core/Configuration/RagOptionsValidator.cs` | drop "partitions need a scope"; `ModelCheckInterval`; chunker checks | 2, 7, 9 |
| `src/RagDotNet.Core/Configuration/RagOptions.cs` | `ModelCheckInterval` | 7 |
| `src/RagDotNet.Core/Configuration/RagPipelineBuilder.cs`, `RagServiceCollectionExtensions.cs`, `IRagBuilder.cs`, `RagEngineFactory.cs` | chat to admin; time provider; `AddChunker` | 5, 7, 9 |
| `src/RagDotNet.Core/Pipeline/IRagAdmin.cs`, `RagAdmin.cs` | `ForScope`; common-area delete; reindex coordination | 5, 8 |
| `src/RagDotNet.Core/Pipeline/RagEngine.cs` | `Time`, `InstanceId`, `ReindexGate`, `Chunkers`, `OutputGuard` | 7, 8, 9 |
| `src/RagDotNet.Core/Embedding/EmbeddingModelGuard.cs` | interval cache; marker; read/write modes | 7 |
| `src/RagDotNet.Core/Chunking/*` | `IChunker` Name/Version, `Chunkers`, `ChunkingOptions.Chunker`, delete `ChunkingStrategy`, `ChunkerRegistry`, `ChunkOutputGuard` | 9 |
| `src/RagDotNet.Core/Storage/Hashing.cs` | chunking hash includes chunker name and version | 9 |
| `tests/RagDotNet.Core.Tests/Fakes/TestScopes.cs` | new shared `MutableScope`, `Tenant(...)` | 2 |
| `tests/RagDotNet.Core.Tests/Fakes/ManualTimeProvider.cs` | new | 7 |
| `README.md` | access model, caching note, reindex guidance, custom chunkers | 5, 8, 9 |

---

### Task 1: Storage — chunk `Partition` field and full metadata on manifests

The common area means a chunk's partition can't be rebuilt from `meta_<partitionKey>` values, because they are null for common documents. The write checks in Task 4 need the document's metadata on its manifest.

**Files:**
- Modify: `src/RagDotNet.Core/Storage/RecordDefinitions.cs`, `src/RagDotNet.Core/Storage/ManifestRecord.cs`, `src/RagDotNet.Core/Storage/ManifestStore.cs`, `src/RagDotNet.Core/Ingestion/Ingestor.cs`, `src/RagDotNet.Core/Retrieval/Retriever.cs`, `src/RagDotNet.Core/Pipeline/RagAdmin.cs` (call sites only)
- Test: `tests/RagDotNet.Core.Tests/StorageSchemaTests.cs`

**Interfaces:**
- Produces:
  - Chunk records have `Partition` (string, `IsIndexed = true`), set to the canonical partition string (`""` for the common area).
  - Manifest records declare `meta_<name>` for **every** field in `RagOptions.Metadata`, all `IsIndexed = true`, not only partition keys.
  - `ManifestRecord` gains a trailing parameter `IReadOnlyDictionary<string, object?> Metadata`, holding every declared field (null when omitted). `PartitionValues` stays.
  - `ManifestRecord.FromDynamic(Dictionary<string, object?> r, RagOptions options)` replaces the `IEnumerable<string> partitionKeys` overload. When `Partition == ""`, `PartitionValues` is empty and partition `meta_*` values are not required. Otherwise every partition key must be a non-null string, as today.
  - `ToDynamic` writes `meta_*` from `Metadata`.

- [ ] **Step 1: Write the failing tests** in `StorageSchemaTests.cs`

```csharp
[Fact]
public void Chunk_definition_has_indexed_partition_field()
{
    var p = RecordDefinitions.Chunks(Options()).Properties.OfType<VectorStoreDataProperty>().Single(x => x.Name == "Partition");
    Assert.Equal(typeof(string), p.Type);
    Assert.True(p.IsIndexed);
}

[Fact]
public void Manifest_definition_declares_every_metadata_field()
{
    var o = Options();
    o.Metadata.Add<string[]>("groups");
    var names = RecordDefinitions.Manifests(o).Properties.Select(x => x.Name).ToList();
    Assert.Contains("meta_tenant", names);
    Assert.Contains("meta_groups", names);
}

[Fact]
public async Task Ingested_chunks_and_manifest_carry_partition_and_metadata()
{
    // Unscoped pipeline with tenant + groups declared; ingest Md("d1", "alpha", {groups: ["g1"]}).
    // Read the raw chunk via store.GetDynamicCollection("test", RecordDefinitions.Chunks(options)) and the raw
    // manifest via "test_documents"; assert:
    Assert.Equal("", chunk["Partition"]);
    Assert.Equal(new[] { "g1" }, (string[])manifest["meta_groups"]!);
    Assert.Null(manifest["meta_tenant"]);
}

[Fact]
public void Common_area_manifest_round_trips_without_partition_values()
{
    var o = Options();
    var m = new ManifestRecord("d1", "d1", "", new Dictionary<string, string>(), "h", "c", "m", 1, "d1.md", 0,
        ContentStorageMode.VectorStore, "mh", 1, new Dictionary<string, object?> { ["tenant"] = null });
    var back = ManifestRecord.FromDynamic(m.ToDynamic(o.KeyType), o);
    Assert.Empty(back.PartitionValues);
    Assert.Null(back.Metadata["tenant"]);
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class *StorageSchemaTests`
Expected: they fail to compile (no `Metadata` parameter, no `FromDynamic(…, RagOptions)`).

- [ ] **Step 3: Implement**
  - In `RecordDefinitions`, add the chunk `Partition` property right after `DocumentId`. In the manifest definition, loop over `o.Metadata.Fields` instead of `o.PartitionKeys`.
  - Extend `ManifestRecord` and `FromDynamic` as described in Interfaces. `FromDynamic` reads each declared `meta_*` with `TryGetValue` (missing means null).
  - In `Ingestor`, add `["Partition"] = scope.PartitionKey` to chunk records. Pass the built `metadata` dictionary into both manifest constructions (pending and committed).
  - In `Retriever.Map`, build the chunk key from `(string)r["Partition"]`. If it is missing or not a string, throw `InvalidOperationException("Retrieved record has no Partition field; it was written by an older version. Re-ingest into a new collection.")`.
  - Update every `FromDynamic` caller in `ManifestStore`.

- [ ] **Step 4: Run the full suite and confirm it passes**

Run: `dotnet test --project tests/RagDotNet.Core.Tests`
Expected: all tests pass on both frameworks.

- [ ] **Step 5: Commit**

```bash
git add src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "feat(core): chunk Partition field and declared metadata on manifests"
```

---

### Task 2: Access types, optional scope, full access and the common area

**Files:**
- Create: `src/RagDotNet.Core/Access/RagPermissions.cs`, `src/RagDotNet.Core/Access/RagAccessDeniedException.cs`, `tests/RagDotNet.Core.Tests/Fakes/TestScopes.cs`
- Modify: `src/RagDotNet.Core/Access/AccessScope.cs`, `IAccessScope.cs`, `ScopeResolver.cs`, `src/RagDotNet.Core/Configuration/RagOptionsValidator.cs`, `RagPipelineBuilder.cs`, `src/RagDotNet.Core/Ingestion/Ingestor.cs` (`BuildMetadata`), `PublicAPI.Unshipped.txt`
- Delete: `src/RagDotNet.Core/Access/UnscopedDataException.cs`
- Test: `tests/RagDotNet.Core.Tests/AccessScopeTests.cs`. Also update every test's private `IAccessScope` implementation to the new signature: `AdminAndModelSafetyTests`, `DependencyInjectionTests`, `ExternalContentTests`, `GenerationTests`, `PipelineBuilderTests`, `RetrievalTests`.

**Interfaces:**
- Produces (public):
  - `[Flags] public enum RagPermissions { None = 0, Read = 1, Write = 2, Delete = 4, All = Read | Write | Delete }`
  - `public sealed record AccessScope(IReadOnlyDictionary<string, string> Partition, RagFilter? Filter = null, RagPermissions Permissions = RagPermissions.All)`
  - `IAccessScope.GetScopeAsync(CancellationToken) → ValueTask<AccessScope?>`. Returning `null` means full access.
  - `public sealed class RagAccessDeniedException : Exception`, with `string Operation` and `RagPermissions? MissingPermission`. It has the three standard constructors plus `(string operation, RagPermissions? missingPermission, string message)`.
- Produces (internal):
  - `internal sealed record ResolvedScope(IReadOnlyDictionary<string,string> Partition, string PartitionKey, RagFilter? UserFilter, RagPermissions Permissions, bool IsFullAccess)`
  - `static ResolvedScope FullAccess` has an empty partition, `""` as key, a null filter, `All` permissions and `IsFullAccess = true`.
  - `UserFilter` is the scope's own `Filter` only; partition equality is no longer folded in, because Task 3 builds the partition condition.
- Produces (public): `public static AccessScope Unpartitioned(RagFilter? filter = null, RagPermissions permissions = RagPermissions.All)` on `AccessScope`. It returns a scope with an empty `Partition`, for single-tenant apps.
- Removed:
  - `UnscopedDataException` and the unscoped-data check in `ScopeResolver`;
  - the `requireScopeForPartitions` parameter;
  - the "Partition keys … require an access scope" validation failure;
  - the "An access scope is registered but PartitionKeys is empty" validation failure.
- `ScopeResolver` validation:
  - With `PartitionKeys` configured, the existing rules apply (every key set, non-empty).
  - With none configured, a non-empty `Partition` throws `InvalidOperationException("The access scope sets partition values, but no PartitionKeys are configured. Use AccessScope.Unpartitioned for a single-tenant app.")`. An empty one resolves to `PartitionKey = ""` with `IsFullAccess = false`.
- Full-access target area: `Ingestor` gets `private ResolvedScope TargetFor(DocumentSource s, ResolvedScope scope)`.
  - Scoped calls return `scope` unchanged.
  - With full access, every partition key set to a non-empty string gives that partition. The result keeps `IsFullAccess = true`, so no permission or visibility checks apply.
  - With full access and no partition key set, the result is `FullAccess` (the common area). Anything else throws `ArgumentException`.
  - The lock, keys, manifest and stamped metadata all use the target.

- [ ] **Step 1: Add shared test helpers** in `Fakes/TestScopes.cs`

```csharp
internal sealed class MutableScope : IAccessScope
{
    public AccessScope? Current { get; set; }
    public int Calls { get; private set; }
    public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) { Calls++; return new(Current); }
}

internal static class TestScopes
{
    public static AccessScope Tenant(string t, RagFilter? filter = null, RagPermissions permissions = RagPermissions.All) =>
        new(new Dictionary<string, string> { ["tenant"] = t }, filter, permissions);
}
```

Remove the private copies in `AccessScopeTests`. Switch the other test files' private scopes to `ValueTask<AccessScope?>`.

- [ ] **Step 2: Write the failing tests** in `AccessScopeTests.cs`. Delete `Partition_keys_without_a_scope_throw_at_build` and `Enabling_scope_over_unscoped_data_throws_UnscopedDataException`.

```csharp
[Fact]
public async Task Partition_keys_without_a_scope_build_and_write_to_the_common_area()
{
    var (rag, _, _, _, _) = Create(o => { o.Metadata.Add<string>("tenant"); o.PartitionKeys.Add("tenant"); });
    await rag.IngestAsync(Md("c1", "alpha beta"), cancellationToken: Ct);
    var hits = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
    Assert.Equal("c1:0", Assert.Single(hits).ChunkKey); // common-area key has no partition prefix
}

[Fact]
public async Task Null_scope_means_full_access_and_sees_every_partition()
{
    var (rag, scope) = CreateScoped();
    scope.Current = TestScopes.Tenant("a");
    await rag.IngestAsync(Md("a1", "alpha beta"), cancellationToken: Ct);
    scope.Current = null;
    await rag.IngestAsync(Md("c1", "alpha gamma"), cancellationToken: Ct);
    var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
    Assert.Equal(["a1", "c1"], hits.Select(h => h.DocumentId).Order());
}

[Fact]
public async Task Full_access_ingest_naming_a_tenant_goes_to_that_tenant()
{
    var (rag, scope) = CreateScoped();
    scope.Current = null;
    await rag.IngestAsync(Md("t1", "alpha beta", new Dictionary<string, object?> { ["tenant"] = "a" }), cancellationToken: Ct);
    var hits = await rag.RetrieveAsync("alpha beta", cancellationToken: Ct);
    Assert.Equal("tenant=a|t1:0", Assert.Single(hits).ChunkKey);
}

[Fact]
public async Task Full_access_ingest_with_partial_or_empty_partition_values_throws()
{
    // Two partition keys (tenant, region): metadata with only tenant → ArgumentException naming "region";
    // tenant = "" → ArgumentException. Nothing is stored in either case.
}

[Fact]
public void Access_scope_defaults_to_all_permissions() =>
    Assert.Equal(RagPermissions.All, new AccessScope(new Dictionary<string, string> { ["tenant"] = "a" }).Permissions);
```

Keep `Empty_partition_throws`, `Partition_keys_not_matching_options_throw` and `Invalid_partition_values_throw`; they still apply to a non-null scope in a multi-tenant app.

Single-tenant tests:

```csharp
[Fact]
public async Task Single_tenant_scope_applies_permissions_without_a_partition()
{
    var scope = new MutableScope { Current = AccessScope.Unpartitioned(permissions: RagPermissions.Read) };
    var (rag, _, _, _, _) = Create(builder: b => b.UseAccessScope(scope)); // no PartitionKeys
    await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct));
    scope.Current = AccessScope.Unpartitioned();
    await rag.IngestAsync(Md("d1", "alpha"), cancellationToken: Ct);
    Assert.Equal("d1:0", Assert.Single(await rag.RetrieveAsync("alpha", cancellationToken: Ct)).ChunkKey);
}

[Fact]
public async Task Single_tenant_scope_with_partition_values_throws()
{
    // no PartitionKeys; scope returns Tenant("a") → InvalidOperationException mentioning "Unpartitioned".
}
```

In `DependencyInjectionTests`, the two tests asserting the "PartitionKeys" message for a scope without partition keys become `Scope_without_partition_keys_is_allowed_for_single_tenant_apps`, which expects no exception at startup or build.

- [ ] **Step 3: Run them and confirm they fail**

Run: `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class *AccessScopeTests`
Expected: they fail to compile (new types are missing) until Step 4.

- [ ] **Step 4: Implement**
  - `ScopeResolver.ResolveAsync`: a null provider or a null result returns `ResolvedScope.FullAccess`. Otherwise run the existing partition validation and return the scoped record with `UserFilter = s.Filter` and `Permissions = s.Permissions`.
  - `ScopeResolver` no longer needs `ManifestStore`.
  - Existing code that read `scope.Filter` or `HasScopeFilter` moves to `UserFilter`. Until Task 3, `Retriever` builds its filter as partition-equality AND `UserFilter`, so the suite stays green.
  - `Ingestor.TargetFor` (see Interfaces). For partial values throw `ArgumentException($"Full-access ingest must set all partition keys [{keys}] or none; '{missing}' is missing.")`. For an empty value throw `ArgumentException($"Partition value for '{key}' must not be empty.")`.
  - Remove the "partitions need a scope" and "scope needs PartitionKeys" rules from the validator and the builder.

- [ ] **Step 5: Run the full suite and confirm it passes**

Run: `dotnet test --project tests/RagDotNet.Core.Tests`
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "feat(core)!: optional access scope with full access, common area and RagPermissions"
```

---

### Task 3: Reads — own partition or common, `IncludeCommon`, Read permission

**Files:**
- Create: `src/RagDotNet.Core/Access/ReadFilters.cs`
- Modify: `src/RagDotNet.Core/Retrieval/Retriever.cs`, `src/RagDotNet.Core/Retrieval/RetrievalOptions.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/RagDotNet.Core.Tests/AccessScopeTests.cs`

**Interfaces:**
- Consumes: `ResolvedScope` (Task 2) and the chunk `Partition` field (Task 1).
- Produces:
  - `RetrievalOptions.IncludeCommon` (`bool`, default `true`).
  - `internal static class ReadFilters` with `Expression<Func<Dictionary<string, object?>, bool>>? Build(ResolvedScope scope, bool includeCommon, RagFilter? callerFilter, MetadataSchema schema, out bool matchesNothing)`. The result is the store filter for a read:
    - Scoped: `(Partition == scope.PartitionKey || (includeCommon && Partition == "")) && UserFilter && callerFilter`.
    - Full access: `includeCommon ? (callerFilter only) : (Partition != "" && callerFilter)`.
    - `matchesNothing` is true when the translator reports it for the combined `RagFilter`.
  - Build the partition clause directly as an expression over `r["Partition"]`, using the same `Convert(Indexer)` shape the translator uses, and combine it with `Expression.AndAlso` on the translator's lambda body (one shared parameter).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Tenant_sees_own_and_common_but_not_other_tenants()
{
    var (rag, scope) = CreateScoped();
    scope.Current = null;                     await rag.IngestAsync(Md("c1", "alpha common"), cancellationToken: Ct);
    scope.Current = TestScopes.Tenant("a");   await rag.IngestAsync(Md("a1", "alpha tenant a"), cancellationToken: Ct);
    scope.Current = TestScopes.Tenant("b");   await rag.IngestAsync(Md("b1", "alpha tenant b"), cancellationToken: Ct);

    scope.Current = TestScopes.Tenant("a");
    var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
    Assert.Equal(["a1", "c1"], hits.Select(h => h.DocumentId).Order());

    var own = await rag.RetrieveAsync("alpha", new() { TopK = 10, IncludeCommon = false }, Ct);
    Assert.Equal(["a1"], own.Select(h => h.DocumentId));
}

[Fact]
public async Task Scope_filter_applies_to_the_common_area_too()
{
    // CreateScoped with o.Metadata.Add<string[]>("groups"); full-access ingest c1 {groups:["hr"]} and c2 {groups:["eng"]};
    // tenant "a" with Filter = RagFilter.ContainsAny("groups", ["eng"]) retrieves only c2.
}

[Fact]
public async Task Same_document_id_in_tenant_and_common_both_appear()
{
    // Full-access ingest "d" ("alpha common"); tenant a ingests "d" ("alpha tenant"). Tenant a retrieves 2 hits with
    // DocumentId "d" and different ChunkKeys ("d:0" and "tenant=a|d:0").
}

[Fact]
public async Task Existing_unscoped_data_becomes_the_common_area_when_a_scope_is_turned_on()
{
    // Ingest "old" through a pipeline with no scope, then build a scoped pipeline over the same store (tenant "a").
    // The scoped pipeline retrieves "old", and its delete of "old" returns false (common is read-only to tenants).
}

[Fact]
public async Task Full_access_without_common_sees_only_tenant_partitions()
{
    // c1 common, a1 written by full access with metadata tenant = "a"; scope null; IncludeCommon = false → only a1;
    // tenant a (scoped) also sees a1.
}

[Fact]
public async Task Reads_require_the_read_permission()
{
    var (rag, scope) = CreateScoped();
    scope.Current = TestScopes.Tenant("a", permissions: RagPermissions.Write);
    var ex = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
    Assert.Equal(RagPermissions.Read, ex.MissingPermission);
    await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.AskAsync("alpha", cancellationToken: Ct));
}
```

- [ ] **Step 2: Run them and confirm they fail.** Same command as Task 2. Expected: compile failure on `IncludeCommon`, then assertion failures.

- [ ] **Step 3: Implement.** `Retriever.RetrieveCoreAsync` does three things in order:
  1. Checks the Read permission first and throws `RagAccessDeniedException("retrieve", RagPermissions.Read, "The access scope does not grant Read.")`.
  2. Replaces its filter composition with `ReadFilters.Build`.
  3. Returns `[]` on `matchesNothing` without querying the store.

- [ ] **Step 4: Run the full suite and confirm it passes.** Same command. Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "feat(core): reads match own partition or common area; IncludeCommon; Read permission"
```

---

### Task 4: Writes — permissions and "you can't change what you can't read"

**Files:**
- Create: `src/RagDotNet.Core/Filtering/RagFilterEvaluator.cs`
- Modify: `src/RagDotNet.Core/Ingestion/Ingestor.cs`, `src/RagDotNet.Core/Filtering/RagFilterTranslator.cs` (expose `Simplify` and `ConvertValue` as `internal`), `src/RagDotNet.Core/Pipeline/IRagPipeline.cs` (XML docs)
- Test: `tests/RagDotNet.Core.Tests/AccessScopeTests.cs`, `tests/RagDotNet.Core.Tests/RagFilterTests.cs`

**Interfaces:**
- Consumes: `ManifestRecord.Metadata` (Task 1), `ResolvedScope.Permissions`, `IsFullAccess` and `UserFilter` (Task 2).
- Produces:
  - `internal static class RagFilterEvaluator` with `bool Matches(RagFilter? filter, MetadataSchema schema, string documentId, IReadOnlyDictionary<string, object?> metadata)`.
  - A null filter is `true`.
  - It validates and converts exactly like the translator: it reuses `Simplify` (a `null` result means it matches nothing) and `ConvertValue`.
  - `DocumentId` resolves to the `documentId` argument.
  - A null or missing field value never matches `Eq`, `In`, `Contains` or `ContainsAny`.
  - Value comparison uses `Equals` after conversion. `Contains` uses ordinal comparison.

Write rules in `Ingestor`; full-access scopes skip all of them:

| Operation | Rule, in order |
|---|---|
| Ingest | 1. `Write` permission, or throw `RagAccessDeniedException("ingest", RagPermissions.Write, …)` before reading the source.<br>2. Once metadata is built (partition stamped), `RagFilterEvaluator.Matches(UserFilter, …, s.DocumentId, metadata)`, or throw `RagAccessDeniedException("ingest", null, "The document's metadata is outside the caller's read filter.")`.<br>3. Under the document lock, after the manifest read and **before** the unchanged check: if `existing` is not null and `!Matches(UserFilter, …, existing.DocumentId, existing.Metadata)`, throw `RagAccessDeniedException("ingest", null, "A document with this id exists and is not visible to the caller.")`. |
| Pipeline delete | 1. `Delete` permission, or throw `RagAccessDeniedException("delete", RagPermissions.Delete, …)`.<br>2. Under the lock, a manifest that is missing or not visible returns `false`.<br>3. Remove `IsVisibleAsync` (the chunk query) entirely. |

- [ ] **Step 1: Write the failing evaluator tests** in `RagFilterTests.cs`

```csharp
[Fact]
public void Evaluator_null_array_field_never_matches()
{
    var schema = new MetadataSchema().Add<string[]>("groups");
    var values = new Dictionary<string, object?> { ["groups"] = null };
    Assert.False(RagFilterEvaluator.Matches(RagFilter.ContainsAny("groups", ["hr"]), schema, "d", values));
}

[Fact]
public void Evaluator_matches_like_the_translator()
{
    var schema = new MetadataSchema().Add<long>("n").Add<string[]>("groups").Add<string>("kind");
    var v = new Dictionary<string, object?> { ["n"] = 5L, ["groups"] = new[] { "hr" }, ["kind"] = "memo" };
    Assert.True(RagFilterEvaluator.Matches(RagFilter.Eq("n", 5) & RagFilter.Contains("groups", "hr"), schema, "d", v));
    Assert.True(RagFilterEvaluator.Matches(RagFilter.Eq("DocumentId", "d"), schema, "d", v));
    Assert.False(RagFilterEvaluator.Matches(RagFilter.In("kind", []), schema, "d", v));
    Assert.Throws<ArgumentException>(() => RagFilterEvaluator.Matches(RagFilter.Eq("n", 5.5), schema, "d", v));
}
```

- [ ] **Step 2: Write the failing write-rule tests** in `AccessScopeTests.cs`. Use `CreateScoped(o => o.Metadata.Add<string[]>("groups"))` and `Groups(...)`.

```csharp
[Fact]
public async Task Read_only_user_cannot_ingest_or_delete()
{
    var (rag, scope) = CreateScoped();
    scope.Current = TestScopes.Tenant("a", permissions: RagPermissions.Read);
    var e1 = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
    Assert.Equal(RagPermissions.Write, e1.MissingPermission);
    var e2 = await Assert.ThrowsAsync<RagAccessDeniedException>(() => rag.DeleteDocumentAsync("d", Ct));
    Assert.Equal(RagPermissions.Delete, e2.MissingPermission);
}

[Fact]
public async Task Ingest_over_a_hidden_document_is_refused_and_leaves_it_unchanged()
{
    // tenant a, no filter: ingest "d" {groups:["hr"]} text "alpha hr". Then tenant a with Filter ContainsAny(groups,["eng"])
    // ingests "d" {groups:["eng"]} → RagAccessDeniedException; with the unfiltered scope, retrieve still returns "alpha hr".
}

[Fact]
public async Task Ingest_whose_metadata_falls_outside_the_read_filter_is_refused()
{
    // tenant a, Filter ContainsAny(groups,["eng"]): ingest "n" {groups:["hr"]} → RagAccessDeniedException; nothing stored.
}

[Fact]
public async Task Empty_document_is_visible_through_its_manifest_metadata()
{
    // replaces Delete_of_an_empty_document_under_a_scope_filter_returns_false:
    // tenant a, Filter ContainsAny(groups,["eng"]): ingest Md("e", "", {groups:["eng"]}) (0 chunks) → then re-ingest with text
    // succeeds (Updated), and DeleteDocumentAsync("e") returns true.
}

[Fact]
public async Task Stored_document_with_null_array_field_is_hidden_not_a_crash()
{
    // Review Focus 2: tenant a unfiltered ingests "d" with no groups; tenant a filtered ContainsAny(groups,["eng"]) →
    // ingest "d" throws RagAccessDeniedException (not NullReferenceException); delete "d" returns false.
}

[Fact]
public async Task Scoped_ingest_of_a_common_document_id_creates_a_separate_tenant_document()
{
    // Review Focus 1: full-access ingest "d" ("alpha common"); tenant a ingests "d" ("alpha tenant") → IngestStatus.Created;
    // full-access retrieve (TopK 10) returns both, and the common chunk text is still "alpha common".
}

[Fact]
public async Task Single_tenant_user_can_change_what_they_can_read_and_not_what_they_cannot()
{
    // no PartitionKeys; full-access ingest "hr" {groups:["hr"]}; scope Unpartitioned(ContainsAny(groups,["eng"])):
    // ingest "e" {groups:["eng"]} → Created; delete "e" → true; ingest "hr" → RagAccessDeniedException; delete "hr" → false.
}

[Fact]
public async Task Tenant_cannot_delete_a_common_document()
{
    // full-access ingest "c"; tenant a (All permissions) DeleteDocumentAsync("c") → false; full-access retrieve still finds "c".
}
```

Keep the existing delete-filter tests (`Delete_with_a_scope_filter_that_excludes_the_document_returns_false`, etc.); they must still pass.

- [ ] **Step 3: Run them and confirm they fail.** Command: `dotnet test --project tests/RagDotNet.Core.Tests`. Expected: failures in the new tests only.

- [ ] **Step 4: Implement** the evaluator and the write rules in the table above.

- [ ] **Step 5: Run the full suite and confirm it passes.** Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "feat(core): write permissions; refuse changes to documents the caller cannot read"
```

---

### Task 5: Admin — `ForScope`, common-area delete, stats; access docs

**Files:**
- Modify: `src/RagDotNet.Core/Pipeline/IRagAdmin.cs`, `RagAdmin.cs`, `src/RagDotNet.Core/Access/ScopeResolver.cs`, `src/RagDotNet.Core/Configuration/RagPipelineBuilder.cs`, `RagServiceCollectionExtensions.cs`, `src/RagDotNet.Core/Pipeline/RagStats.cs` (docs), `src/RagDotNet.Core/Content/ChunkContent.cs` (docs), `PublicAPI.Unshipped.txt`, `README.md`
- Test: `tests/RagDotNet.Core.Tests/AdminAndModelSafetyTests.cs`, `tests/RagDotNet.Core.Tests/DependencyInjectionTests.cs`

**Interfaces:**
- Produces:
  - `IRagAdmin.ForScope(AccessScope scope) → IRagPipeline`. It throws `ArgumentNullException`, and `ArgumentException` when the scope fails the same validation `ScopeResolver` applies. It ignores any registered `IAccessScope`.
  - `internal ResolvedScope ScopeResolver.ValidateExplicit(AccessScope)` wraps the validation errors in `ArgumentException`. The pipeline is `new RagPipeline(engine, new FixedAccessScope(scope), chat)`, with a private `FixedAccessScope` class in `RagAdmin.cs`.
  - `RagAdmin(RagEngine engine, IChatClient? chat = null)`. The builder passes `_chat`; DI passes `sp.GetService<IChatClient>()`.
  - `IRagAdmin.DeleteDocumentAsync(documentId, partition)`: a null or empty `partition` targets the common area. A non-empty one must match `PartitionKeys` exactly (`ArgumentException`), as today.
  - `GetStatsAsync` reports the common area as a `PartitionStats` with an empty `Partition` dictionary (the doc text says so).

- [ ] **Step 1: Write the failing tests** in `AdminAndModelSafetyTests.cs`

```csharp
[Fact]
public async Task ForScope_writes_into_the_named_tenant_partition()
{
    // Partitioned builder with NO scope; admin.ForScope(TestScopes.Tenant("a")).IngestAsync(Md("d1","alpha"));
    // the unscoped pipeline retrieves it with ChunkKey "tenant=a|d1:0"; stats show {tenant=a}: 1 doc.
}

[Fact]
public void ForScope_rejects_an_invalid_scope() =>
    // empty partition, and keys not matching PartitionKeys → ArgumentException
    ...;

[Fact]
public async Task ForScope_pipeline_can_ask_with_the_builder_chat_client()
{
    // FakeChatClient scripted reply "See [1]." → answer.IsAnswered and one citation.
}

[Fact]
public async Task Admin_delete_with_null_partition_targets_the_common_area()
{
    // full-access ingest "c" and ForScope(a) ingest "c"; admin.DeleteDocumentAsync("c", null) → true;
    // the tenant copy remains (ForScope(a) retrieve finds it); stats list only {tenant=a}.
}

[Fact]
public async Task Stats_report_the_common_area_as_an_empty_partition()
{
    // one common and one tenant doc → Partitions contains an entry with empty Partition and DocumentCount 1.
}
```

Update `Admin_delete_targets_named_partition`. A `null` partition with partition keys configured no longer throws; it is the common-area delete above. In `DependencyInjectionTests`, add `ForScope_from_DI_admin_ignores_the_registered_scope`: `AddAccessScope<HolderScope>()` with tenant "b", and `ForScope(Tenant("a"))` writes to "a".

- [ ] **Step 2: Run them and confirm they fail.** Expected: compile failure on `ForScope`.

- [ ] **Step 3: Implement** as described in Interfaces.

- [ ] **Step 4: Update `README.md`** with an "Access control" section that replaces the stage 1 text. It covers:
  - no scope or a `null` scope means full access; writes go to the tenant the document's metadata names, or else to the common area;
  - single-tenant vs multi-tenant: no `PartitionKeys` means single-tenant, with no tenant to name; `AccessScope.Unpartitioned` gives read filters and permissions without a partition;
  - `AccessScope` partition, filter and permissions, with a read-only example;
  - tenants see their own documents plus common ones, and `IncludeCommon`;
  - the common area is read-only to tenants;
  - `IRagAdmin.ForScope` for writes that must apply a tenant user's read filter and permissions;
  - the accepted existence leak on refused writes, and how to avoid it with per-group id prefixes.

  Also add a short "Caching" section. It explains how to wrap the app's `IChatClient` and `IEmbeddingGenerator` with Microsoft.Extensions.AI's caching wrappers (`UseDistributedCache`). It explains why that is safe: the prompt contains the retrieved documents, so new documents or different access miss the cache. It says semantic caching is not provided.

- [ ] **Step 5: Run the full suite and confirm it passes.** Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests README.md
git commit -m "feat(core): IRagAdmin.ForScope; admin delete of common documents; access docs"
```

---

### Task 6: Null-safe filters on missing metadata values

Stage 1 crashes when a filter touches a field a document has no value for:
- `NullReferenceException` for `int`, `long`, `bool` and `DateTimeOffset`;
- `ArgumentNullException` for `string[]`.

This was measured on 2026-10-31 with the InMemory store. Scope filters such as `ContainsAny("groups", user.Groups)` over documents without groups crash the same way.

**Files:**
- Modify: `src/RagDotNet.Core/Filtering/RagFilterTranslator.cs`, `src/RagDotNet.Core/Ingestion/Ingestor.cs` (`BuildMetadata`), `src/RagDotNet.Core/Retrieval/RetrievedChunk.cs` (XML doc on `Metadata`: omitted arrays come back empty)
- Test: `tests/RagDotNet.Core.Tests/RagFilterTests.cs`, `tests/RagDotNet.Core.Tests/AccessScopeTests.cs`

**Interfaces:**
- Produces:
  - `BuildMetadata` stores `Array.Empty<string>()` for an omitted or null `string[]` field. The metadata hash is computed after this, so null and `[]` count as the same document.
  - `RagFilterTranslator.BuildEq`: for value-type fields it compares `Expression.Convert(indexer, typeof(Nullable<T>))` with `Expression.Constant(converted, typeof(Nullable<T>))`. String comparisons don't change.
  - `BuildContains` doesn't change, because arrays are never null after the write change.

- [ ] **Step 1: Write the failing tests** in `RagFilterTests.cs`

```csharp
[Theory]
[InlineData("level")]
[InlineData("big")]
[InlineData("flag")]
[InlineData("when")]
[InlineData("groups")]
[InlineData("kind")]
public async Task Filter_on_a_missing_value_does_not_match_and_does_not_throw(string field)
{
    var (rag, _, _, _, _) = Create(o =>
    {
        o.Metadata.Add<int>("level"); o.Metadata.Add<long>("big"); o.Metadata.Add<bool>("flag");
        o.Metadata.Add<DateTimeOffset>("when"); o.Metadata.Add<string[]>("groups"); o.Metadata.Add<string>("kind");
    });
    await rag.IngestAsync(Md("d1", "alpha beta"), cancellationToken: Ct); // every field omitted
    RagFilter f = field switch
    {
        "level" => RagFilter.Eq("level", 3),
        "big" => RagFilter.Eq("big", 3L),
        "flag" => RagFilter.Eq("flag", true),
        "when" => RagFilter.Eq("when", DateTimeOffset.UnixEpoch),
        "groups" => RagFilter.ContainsAny("groups", ["hr"]),
        _ => RagFilter.Eq("kind", "memo"),
    };
    Assert.Empty(await rag.RetrieveAsync("alpha", new() { Filter = f }, Ct));
}

[Fact]
public async Task Present_values_still_match_after_the_nullable_comparison()
{
    // d1 {level: 3, flag: true, when: UnixEpoch, groups: ["hr"]} and d2 with nothing set;
    // Eq(level,3), Eq(flag,true), Eq(when,UnixEpoch), Contains(groups,"hr"), In(level,[1,3]) each return exactly d1.
}

[Fact]
public async Task Omitted_array_metadata_is_returned_as_empty()
{
    // ingest without groups → RetrievedChunk.Metadata["groups"] is an empty string[] (not null).
}
```

In `AccessScopeTests.cs`:

```csharp
[Fact]
public async Task Scope_filter_on_groups_hides_documents_without_groups_instead_of_crashing()
{
    // full-access ingest c1 (no groups) and c2 {groups:["eng"]}; tenant a with Filter ContainsAny(groups,["eng"])
    // retrieves only c2.
}
```

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test --project tests/RagDotNet.Core.Tests -- --filter-class *RagFilterTests`
Expected: the missing-value theory fails for `level`, `big`, `flag`, `when` and `groups` with `NullReferenceException` or `ArgumentNullException`. `kind` passes.

- [ ] **Step 3: Implement** the two changes described in Interfaces.

- [ ] **Step 4: Run the full suite and confirm it passes.**

- [ ] **Step 5: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "fix(core): filters on missing metadata values no longer throw"
```

---

### Task 7: Model guard — `ModelCheckInterval`, reindex marker, reads limited to re-embedded data

**Files:**
- Create: `tests/RagDotNet.Core.Tests/Fakes/ManualTimeProvider.cs`
- Modify: `src/RagDotNet.Core/Embedding/EmbeddingModelGuard.cs`, `src/RagDotNet.Core/Storage/ManifestStore.cs`, `src/RagDotNet.Core/Storage/ChunkKeys.cs` (internal const), `src/RagDotNet.Core/Configuration/RagOptions.cs`, `RagOptionsValidator.cs`, `RagEngineFactory.cs`, `RagPipelineBuilder.cs`, `RagServiceCollectionExtensions.cs`, `src/RagDotNet.Core/Pipeline/RagEngine.cs`, `src/RagDotNet.Core/Ingestion/Ingestor.cs`, `src/RagDotNet.Core/Retrieval/Retriever.cs`, `src/RagDotNet.Core/Access/ReadFilters.cs`, `PublicAPI.Unshipped.txt`
- Test: `tests/RagDotNet.Core.Tests/ReindexCoordinationTests.cs` (new)

**Interfaces:**
- Produces (public):
  - `RagOptions.ModelCheckInterval` (`TimeSpan`, default `TimeSpan.FromSeconds(5)`). A negative value fails validation with "ModelCheckInterval must not be negative."
- Produces (internal):
  - `ChunkKeys.ReindexMarkerKey = "|reindex"`.
  - `internal sealed record ReindexMarker(string TargetModelId, string OwnerId, long HeartbeatUnixMs)`.
  - `ManifestStore.GetMarkerAsync(ct) → ReindexMarker?`, `UpsertMarkerAsync(ReindexMarker, ct)` and `DeleteMarkerAsync(ct)`, using the field layout in "Decisions" above.
  - `FindOtherModelAsync` adds `&& (int)m["ChunkCount"]! >= 0`, so it never reports the marker.
  - `RagEngine.Time` (`TimeProvider`). `RagEngineFactory.Create(..., TimeProvider? time = null)`.
  - Internal `RagPipelineBuilder.UseTimeProvider(TimeProvider)`, which respects freezing.
  - `enum GuardMode { Read, Write }` and `EmbeddingModelGuard.EnsureAsync(GuardMode mode, CancellationToken ct) → ValueTask<bool>`, which replaces `EnsureCompatibleAsync`. It returns `true` while a reindex to the configured model is in progress.
  - `Ingestor.IngestCoreAsync` and `Ingestor.DeleteAsync` call it with `Write`.
  - `Retriever` calls it with `Read`. When the result is `true`, the retriever does three things:
    - passes `requireModelId: engine.EmbeddingModelId` to `ReadFilters.Build`. That new optional parameter, `string? requireModelId = null`, ANDs `(string)r["EmbeddingModelId"] == requireModelId` into the expression;
    - logs the once-per-marker warning `"A reindex to embedding model {ModelId} is in progress; answers include only documents already re-embedded."`;
    - tags the span.
  - Guard state is `(ReindexMarker? Marker, string? OtherModelId, DateTimeOffset FetchedAt)`. It is re-fetched when `Time.GetUtcNow() - FetchedAt >= ModelCheckInterval` or after `Reset()`.

Guard decision rules (C = configured model, T = marker target):

| State | Read | Write |
|---|---|---|
| Marker, T == C | allowed, limited to chunks on C (returns `true`) | allowed |
| Marker, T != C | `EmbeddingModelMismatchException(T, C)` | `EmbeddingModelMismatchException(T, C)` |
| No marker, other model found | `EmbeddingModelMismatchException(stored, C)` | same |
| No marker, none found | allowed | allowed |

- [ ] **Step 1: Add `ManualTimeProvider`**: `public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider`, with `GetUtcNow()` returning the current value and an `Advance(TimeSpan)` method.

- [ ] **Step 2: Write the failing tests** in `ReindexCoordinationTests.cs`. Each test builds pipelines over one `InMemoryVectorStore`. Seed with model `"m1"`, then build an engine configured for `"m2"`. To write a marker directly, use the internal `ManifestStore` through `((RagPipeline)rag).Engine.Manifests`.

```csharp
[Fact]
public async Task Marker_for_the_configured_model_limits_reads_to_reembedded_data_and_allows_writes()
{
    // seed d1 ("alpha one") with m1; engine m2; Engine.Manifests.UpsertMarkerAsync(new("m2", "other-owner", now))
    var r = await rag.IngestAsync(Md("d2", "alpha two"), cancellationToken: Ct);
    Assert.Equal(IngestStatus.Created, r.Status);
    var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
    Assert.Equal(["d2"], hits.Select(h => h.DocumentId)); // d1 is still on m1: skipped, never compared
    Assert.True(await rag.DeleteDocumentAsync("d2", Ct));
}

[Fact]
public async Task Reads_during_reindex_log_one_warning()
{
    // FakeLogger (Microsoft.Extensions.Diagnostics.Testing) via UseLoggerFactory; marker present; two retrieves →
    // exactly one warning containing "re-embedded".
}

[Fact]
public async Task Marker_for_another_model_fails_every_call_with_mismatch()
{
    // marker target "m3", engine m2 → retrieve, ingest and delete all throw EmbeddingModelMismatchException
}

[Fact]
public async Task Marker_is_not_counted_as_a_document()
{
    // marker present → admin.GetStatsAsync() partitions/ids unaffected (no "|reindex" entry, no extra doc count)
}

[Fact]
public async Task Cached_state_expires_after_ModelCheckInterval()
{
    // Review Focus 4: seed d1 with m1; engine B ("m2", ManualTimeProvider, interval 5 s) retrieves →
    // EmbeddingModelMismatchException (state cached). Upsert a marker (target m2) directly.
    // B retrieves at +1 s → still EmbeddingModelMismatchException; at +6 s → succeeds (empty: nothing on m2 yet).
}

[Fact]
public async Task Zero_interval_checks_every_call()
{
    // interval Zero: seed with m1, engine m2 → mismatch; upsert marker → the very next retrieve succeeds.
}

[Fact]
public void Negative_interval_fails_validation() =>
    Assert.Throws<OptionsValidationException>(() => Create(o => o.ModelCheckInterval = TimeSpan.FromSeconds(-1)));
```

- [ ] **Step 3: Run them and confirm they fail.** Expected: compile failure, because the new members are missing.

- [ ] **Step 4: Implement** as described above. Admin delete and stats call no model guard, so they keep working during a mismatch (stage 1 already does this).

- [ ] **Step 5: Run the full suite and confirm it passes.** Existing guard tests (`Switching_model_id_throws_mismatch_…`, `Null_stored_model_id_fails_closed`) must still pass.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests
git commit -m "feat(core): timed model check and reindex marker; reads during reindex use re-embedded data"
```

---

### Task 8: Reindex coordination — marker lifecycle, document locks, re-read before write

**Files:**
- Modify: `src/RagDotNet.Core/Pipeline/RagAdmin.cs`, `src/RagDotNet.Core/Pipeline/RagEngine.cs`, `src/RagDotNet.Core/Pipeline/IRagAdmin.cs` (docs), `README.md`
- Test: `tests/RagDotNet.Core.Tests/ReindexCoordinationTests.cs`, `tests/RagDotNet.Core.Tests/AdminAndModelSafetyTests.cs`

**Interfaces:**
- Consumes: the marker API, `RagEngine.Time` and `GuardMode` (Task 7); `DocumentLocks` (stage 1).
- Produces: `RagEngine.InstanceId` (`string`, `Guid.NewGuid().ToString("N")` at construction) and `RagEngine.ReindexGate` (`SemaphoreSlim(1, 1)`). `ReindexResult` and `ReindexProgress` don't change.

Algorithm for `RagAdmin.ReindexAsync`, in order:

1. If `!ReindexGate.Wait(0)`, throw `InvalidOperationException("A reindex is already running in this process.")`. Release it in `finally`.
2. `EnsureReadyAsync`, then `EnsureContentModeAsync`.
3. Probe dimensions (existing `ProbeAsync`). This happens before any write.
4. Read the marker:
   - It is **foreign and fresh** when `OwnerId != InstanceId` and `now - heartbeat <= 5 min`. Then throw `InvalidOperationException($"Another reindex (to '{T}') holds the marker; it is considered abandoned after 5 minutes without a heartbeat.")`.
   - Otherwise, upsert the marker `(C, InstanceId, now)` and read it back. If the owner isn't `InstanceId`, throw the same error (another process won the race).
5. Call `engine.ModelGuard.Reset()` so this process immediately sees its own marker.
6. Materialize the manifest list (`ChunkCount >= 0`), as stage 1 does.
7. For each listed manifest on a model other than C:
   - Acquire `DocumentLocks` for (`Partition`, `DocumentId`).
   - Re-read the manifest as `fresh`. If it is null or on C, count it as skipped.
   - Otherwise, re-embed batches over `[0, max(ChunkCount, ChunkHighWater))` of `fresh`, as stage 1 does.
   - **Before each batch upsert,** re-read the manifest. If it is null, or its `ContentHash` or `UpdatedAtUnixMs` differs from `fresh`, abandon the document: write nothing more for it and log a warning (`"Document {DocumentId} changed during reindex; skipped."`).
   - After the last batch, upsert `fresh with { EmbeddingModelId = C }` and count it as reindexed.
   - Refresh the heartbeat whenever 60 s have passed since the last refresh.
8. Call `FindOtherModelAsync(C)`. If a document is still on another model, leave the marker, `Reset()` the guard, and throw `InvalidOperationException("Some documents changed during reindex and are not yet on the configured model. Run ReindexAsync again.")`.
9. Delete the marker, `Reset()` the guard, and return the result.

On an exception or cancellation in steps 6 and 7, the marker stays (it is not deleted in `finally`).

- [ ] **Step 1: Write the failing tests** in `ReindexCoordinationTests.cs`

```csharp
[Fact]
public async Task Reads_during_reindex_return_reembedded_documents_then_all()
{
    // GatedEmbeddingGenerator pauses on d2's batch (d1 already done); start admin.ReindexAsync(); await gate.Reached;
    // rag.RetrieveAsync (TopK 10) returns d1 only; release gate; await reindex; retrieve returns d1 and d2; marker gone.
}

[Fact]
public async Task Ingest_of_the_document_being_reindexed_waits_for_it()
{
    // gate pauses on d1's batch; start reindex; await Reached; start rag.IngestAsync(Md("d1", "changed")) → not completed
    // after a short Task.WhenAny delay (100 ms); release gate; both complete; d1 retrieves "changed" and stats show only m2.
}

[Fact]
public async Task Write_from_another_process_during_reindex_is_not_overwritten()
{
    // Engine A (admin, m2) reindexes with a gate on d1's batch. Engine B (separate builder over the same store, m2,
    // ModelCheckInterval Zero) ingests Md("d1", "from B") while A is paused. Release the gate. A completes (d1 already on m2
    // after B's write), and retrieving d1 returns "from B".
}

[Fact]
public async Task Cancelled_reindex_resumes_immediately_in_the_same_process()
{
    // Review Focus 3: FlakyEmbedder fails mid-way → ReindexAsync throws; retrieve returns only re-embedded documents;
    // second ReindexAsync on the same admin succeeds without waiting; retrieve returns every document.
}

[Fact]
public async Task Fresh_foreign_marker_refuses_reindex_and_stale_one_is_taken_over()
{
    // ManualTimeProvider; upsert marker ("m2","foreign", now). ReindexAsync → InvalidOperationException.
    // Advance 5 min + 1 s → ReindexAsync succeeds and the marker is gone.
}

[Fact]
public async Task Concurrent_reindex_in_one_process_is_refused()
{
    // gate pauses the first ReindexAsync; the second throws InvalidOperationException("…already running…").
}

[Fact]
public async Task Dimension_mismatch_throws_before_the_marker_is_written()
{
    // generator with 32 dims, collection 64 → InvalidOperationException; GetMarkerAsync() is null.
}
```

Update `AdminAndModelSafetyTests.Interrupted_reindex_resumes`: after the failed run, retrieval no longer throws. It returns only the re-embedded document (d1). `FlakyEmbedder.FailOnCall` counts still start with the probe.

- [ ] **Step 2: Run them and confirm they fail.** Expected: new tests fail (no marker written, no locking).

- [ ] **Step 3: Implement** the algorithm above.

- [ ] **Step 4: Update `README.md`** "Switching embedding models" to cover:
  - the reindex marker;
  - questions keep working during reindex, but only over documents already re-embedded (so run it off-peak), and writes keep going;
  - resuming after an interruption;
  - the 5-minute takeover;
  - multi-instance guidance: pause other writers during reindex, restart every instance after switching models, and change detection within `ModelCheckInterval`.

- [ ] **Step 5: Run the full suite and confirm it passes.** Run it twice, to catch flaky timing in the gate tests.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests README.md
git commit -m "feat(core): reindex coordinates with writes via marker, document locks and re-reads"
```

---

### Task 9: Custom chunkers — named registry, chunking hash, output guarantees

**Files:**
- Create: `src/RagDotNet.Core/Chunking/Chunkers.cs`, `src/RagDotNet.Core/Chunking/ChunkerRegistry.cs`, `src/RagDotNet.Core/Chunking/ChunkOutputGuard.cs`, `tests/RagDotNet.Core.Tests/CustomChunkerTests.cs`
- Modify: `src/RagDotNet.Core/Chunking/IChunker.cs`, `ChunkingOptions.cs`, `StructureAwareChunker.cs`, `src/RagDotNet.Core/Storage/Hashing.cs`, `src/RagDotNet.Core/Ingestion/Ingestor.cs`, `src/RagDotNet.Core/Pipeline/RagEngine.cs`, `src/RagDotNet.Core/Configuration/RagEngineFactory.cs`, `RagPipelineBuilder.cs`, `IRagBuilder.cs`, `RagServiceCollectionExtensions.cs`, `RagOptionsValidator.cs`, `PublicAPI.Unshipped.txt`, `README.md`, plus tests that reference `ChunkingStrategy` (`StructureAwareChunkerTests`)
- Delete: `src/RagDotNet.Core/Chunking/ChunkingStrategy.cs`

**Interfaces:**
- Produces (public):
  - `IChunker { string Name { get; } string Version { get; } Task<IReadOnlyList<Chunk>> ChunkAsync(ParsedDocument, ChunkingOptions, CancellationToken); }`. `Strategy` is removed.
  - `public static class Chunkers { public const string StructureAware = "structure-aware"; }`
  - `ChunkingOptions.Chunker` (`string`, default `Chunkers.StructureAware`). `Strategy` is removed.
  - `StructureAwareChunker.Name` is `"structure-aware"` and `Version` is `"1"`. Remove its `Strategy` check.
  - `RagPipelineBuilder.AddChunker(IChunker chunker)` respects freezing and throws `ArgumentNullException` for null.
  - `IRagBuilder.AddChunker<TChunker>() where TChunker : class, IChunker` registers a singleton `IChunker`.
- Produces (internal):
  - `ChunkerRegistry(IEnumerable<IChunker> appChunkers, IChunker builtIn)` with `IChunker Get(string name)`. `Get` throws `ArgumentException($"No chunker named '{name}' is registered. Register it with AddChunker.")`. Name matching is ordinal and case-sensitive.
  - `RagEngine.Chunkers` replaces `RagEngine.Chunker`. `RagEngineFactory.Create` gains `IEnumerable<IChunker> appChunkers`.
  - `ChunkOutputGuard.Normalize(IChunker chunker, IReadOnlyList<Chunk>? output, ChunkingOptions options, TokenCounter tokens) → IReadOnlyList<Chunk>` does the following, in order:
    1. A null list or null element throws `InvalidOperationException($"Chunker '{chunker.Name}' returned a null chunk list or a null chunk.")`.
    2. Drop chunks whose text is null or whitespace.
    3. Split chunks over `options.MaxTokens` (counted by `tokens`) with `SplitByTokens`, keeping `HeadingPath`, `PageStart` and `PageEnd`, and dropping whitespace pieces.
    4. Renumber `Index` 0..n-1 in output order.
    5. Set `TokenCount = tokens.Count(text)`.
  - `Hashing.ChunkingHash(ChunkingOptions o, IChunker chunker, string tokenizerName)`. The hashed string is `$"{chunker.Name}|{chunker.Version}|{o.MaxTokens}|{o.OverlapRatio:R}|{tokenizerName}|embed:{EmbeddingText.FormatVersion}"`.
- Validation (`RagOptionsValidator.Validate` gains `IReadOnlyList<IChunker> appChunkers`; the DI validator gets `IEnumerable<IChunker>` injected):
  - Each app chunker has a non-blank `Name` and `Version`.
  - No two chunkers share a name, including `"structure-aware"`. Failure: `"Chunker name '{name}' is registered more than once."`
  - `Chunking.Chunker` and every `ChunkingByContentType[*].Chunker` name a registered chunker. Failure: `"Chunking.Chunker '{name}' is not a registered chunker."` (or the content-type variant).
- Per call: `IngestOptions.Chunking.Chunker` is resolved by `ChunkerRegistry.Get`, which throws `ArgumentException` before any write.

- [ ] **Step 1: Write the failing tests** in `CustomChunkerTests.cs`. The test chunker is `LineChunker` (Name `"lines"`, configurable `Version`), which emits one chunk per non-empty line of the document's blocks.

```csharp
[Fact]
public async Task Custom_chunker_selected_by_default_option()
{
    var (rag, _, _, _, _) = Create(o => o.Chunking = new() { Chunker = "lines" }, b => b.AddChunker(new LineChunker()));
    var r = await rag.IngestAsync(Md("d", "one\n\ntwo\n\nthree"), cancellationToken: Ct);
    Assert.Equal(3, r.ChunkCount);
}

[Fact]
public async Task Per_call_chunker_overrides_and_unknown_name_throws_before_writing()
{
    // default structure-aware; IngestOptions { Chunking = new() { Chunker = "lines" } } → 3 chunks.
    // Chunker = "nope" → ArgumentException; stats show no extra document.
}

[Fact]
public async Task Bumping_chunker_version_rechunks_on_next_ingest()
{
    // ingest with LineChunker v1 → Created; same builder setup with v2 over the same store → same bytes → Updated (not Unchanged)
}

[Fact]
public async Task Output_is_normalized_blank_dropped_oversized_split_renumbered()
{
    // Review Focus 5: a ScriptedChunker returns indexes [5, 5, 9] with texts ["a", "   ", <600-token text>], MaxTokens 512
    // → stored chunks are indexes 0..n-1 contiguous (n == 1 + number of split pieces), no blank chunk, every TokenCount <= 512.
}

[Fact]
public async Task Chunker_returning_null_fails_naming_it()
{
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rag.IngestAsync(Md("d", "x"), cancellationToken: Ct));
    Assert.Contains("'nulls'", ex.Message);
}

[Theory]
[InlineData("structure-aware")]
[InlineData("lines")]
public void Duplicate_chunker_name_fails_at_build(string second) =>
    // AddChunker(new LineChunker()) plus a chunker named `second` → OptionsValidationException mentioning "more than once"
    ...;

[Fact]
public void Unknown_default_chunker_fails_at_build_and_at_DI_startup() =>
    // builder: Chunking.Chunker = "nope" → OptionsValidationException; DI: AddRag(...) without AddChunker, resolving
    // IOptions<RagOptions>.Value → OptionsValidationException
    ...;

[Fact]
public void DI_AddChunker_registers_a_singleton_used_by_the_pipeline() => ...;
```

- [ ] **Step 2: Run them and confirm they fail.** Expected: compile failure (`Chunker`, `AddChunker`).

- [ ] **Step 3: Implement.** The Ingestor flow is: resolve the chunker, compute the hash with it, chunk, then `ChunkOutputGuard.Normalize`. Remove `ChunkingStrategy` everywhere, including tests.

- [ ] **Step 4: Update `README.md`** with a "Chunking" section covering:
  - the default `structure-aware` chunker and its settings;
  - choosing a chunker per default, per content type and per call;
  - writing an `IChunker` (Name, Version, bump the version to re-chunk);
  - the output guarantees the library enforces;
  - registration with `AddChunker`.

- [ ] **Step 5: Run the full suite and confirm it passes; run the build and confirm 0 warnings.**

Run: `dotnet build` and `dotnet test --project tests/RagDotNet.Core.Tests`
Expected: 0 warnings and all tests pass. Then run `dotnet run --project samples/RagDotNet.Samples.Console -- --help` (or the sample's no-model path) to confirm it still compiles and starts.

- [ ] **Step 6: Commit**

```bash
git add -A src/RagDotNet.Core tests/RagDotNet.Core.Tests README.md samples
git commit -m "feat(core)!: named custom chunkers with AddChunker; chunker name and version in the chunking hash"
```

---

## Self-review notes

- **Spec coverage, §10 stage 1.1 list:**

  | Spec item | Task |
  |---|---|
  | Optional scope, common area, full-access tenant routing | 2 |
  | `RagPermissions` and write checks | 2, 4 |
  | `ForScope` | 5 |
  | `IncludeCommon` | 3 |
  | Manifest metadata | 1 |
  | `ModelCheckInterval` | 7 |
  | Null-safe filters | 6 |
  | Marker, reindex locks, reads limited during reindex | 7, 8 |
  | Custom chunkers | 9 |
  | Caching note | 5 (README) |

  Every replaced stage 1 behaviour is removed or changed in a named task:

  | Stage 1 behaviour | Task |
  |---|---|
  | "Partitions need a scope" | 2 |
  | `UnscopedDataException` | 2 |
  | Partition-equality reads | 3 |
  | Admin empty-partition rejection | 5 |
  | Reindex without locks | 8 |
  | Process-lifetime model cache | 7 |
  | Filters crashing on missing values | 6 |
- **Out of scope here,** each in its own stage: §5.12 `IngestionJob`, pause/resume and optional JSON for scopes (stage 9); `Chunkers.FixedSize`/`Semantic` (stage 4); keyword-index scope filters (stage 7); the nullable filter shape on real stores (stage 8).
