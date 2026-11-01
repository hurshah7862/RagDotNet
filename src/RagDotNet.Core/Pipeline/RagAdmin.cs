using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Storage;

namespace RagDotNet.Pipeline;

internal sealed class RagAdmin(RagEngine engine, IChatClient? chat = null) : IRagAdmin
{
    private sealed class FixedAccessScope(AccessScope scope) : IAccessScope
    {
        public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) => new(scope);
    }

    public IRagPipeline ForScope(AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        engine.Scope.ValidateExplicit(scope);
        return new RagPipeline(engine, new FixedAccessScope(scope), chat);
    }

    private const string ProbeText = "dimension probe";

    /// <summary>A marker whose heartbeat is older than this is abandoned and may be taken over.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>The owner refreshes its heartbeat after a document once this much time has passed since the last refresh.</summary>
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(60);

    public async Task<ReindexResult> ReindexAsync(IProgress<ReindexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        // 1. One reindex per process.
        if (!engine.ReindexGate.Wait(0))
            throw new InvalidOperationException("A reindex is already running in this process.");
        try
        {
            // 2-3. Ready and content-mode checks, then the dimension probe, all before any write.
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            await engine.ModelGuard.EnsureContentModeAsync(ct).ConfigureAwait(false);
            await ProbeAsync(ct).ConfigureAwait(false);

            // 4. Take the marker. From here on an exception leaves it in place: reads keep filtering to re-embedded data,
            // and a rerun in this process resumes it at once.
            var configured = engine.EmbeddingModelId;
            var lastBeat = engine.Time.GetUtcNow();
            var existing = await engine.Manifests.GetMarkerAsync(ct).ConfigureAwait(false);
            if (existing is not null && IsHeldByAnother(existing, lastBeat)) throw MarkerHeld(existing.TargetModelId);
            await engine.Manifests.UpsertMarkerAsync(new ReindexMarker(configured, engine.InstanceId, lastBeat.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
            var won = await engine.Manifests.GetMarkerAsync(ct).ConfigureAwait(false);
            if (won is null || !string.Equals(won.OwnerId, engine.InstanceId, StringComparison.Ordinal))
                throw MarkerHeld(won?.TargetModelId ?? configured); // another process won the race

            // 5. This process sees its own marker immediately.
            engine.ModelGuard.Reset();

            // 6. Materialize first: writing manifests while paging could skip or revisit documents.
            var all = new List<ManifestRecord>();
            await foreach (var listed in engine.Manifests.ListAsync(r => (int)r["ChunkCount"]! >= 0, ct).ConfigureAwait(false))
                all.Add(listed);

            // Other instances may hold a cached "no marker" state for up to one check interval. Wait it out before the first
            // vector on the configured model exists, so their reads already filter by model when it appears.
            var interval = engine.Options.ModelCheckInterval;
            if (interval > TimeSpan.Zero && all.Exists(m => !string.Equals(m.EmbeddingModelId, configured, StringComparison.Ordinal)))
                await Task.Delay(interval, engine.Time, ct).ConfigureAwait(false);

            // 7. Re-embed every listed document not on the configured model, under its document lock.
            var batchSize = Math.Max(1, engine.Options.EmbeddingBatchSize);
            int reindexed = 0, skipped = 0, chunkTotal = 0;
            foreach (var listed in all)
            {
                if (string.Equals(listed.EmbeddingModelId, configured, StringComparison.Ordinal))
                {
                    skipped++;
                }
                else
                {
                    using var documentLock = await engine.DocumentLocks.AcquireAsync(listed.Partition, listed.DocumentId, ct).ConfigureAwait(false);
                    var (done, chunks) = await ReembedAsync(listed, batchSize, ct).ConfigureAwait(false);
                    chunkTotal += chunks;
                    if (done) reindexed++;
                    else skipped++;
                }

                var now = engine.Time.GetUtcNow();
                var sinceBeat = now - lastBeat;
                if (sinceBeat < TimeSpan.Zero || sinceBeat >= HeartbeatEvery) // a backward clock step refreshes too
                {
                    var current = await engine.Manifests.GetMarkerAsync(ct).ConfigureAwait(false);
                    if (current is not null && !string.Equals(current.OwnerId, engine.InstanceId, StringComparison.Ordinal))
                        throw MarkerHeld(current.TargetModelId); // taken over after this run stalled: stop writing
                    await engine.Manifests.UpsertMarkerAsync(new ReindexMarker(configured, engine.InstanceId, now.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
                    lastBeat = now;
                }

                progress?.Report(new ReindexProgress(reindexed + skipped, listed.DocumentId));
            }

            // 8. A document changed by another process during the run may still be on another model.
            if ((await engine.Manifests.FindOtherModelAsync(configured, ct).ConfigureAwait(false)).Found)
            {
                engine.ModelGuard.Reset();
                throw new InvalidOperationException("Some documents changed during reindex and are not yet on the configured model. Run ReindexAsync again.");
            }

            // 9. Done.
            await engine.Manifests.DeleteMarkerAsync(ct).ConfigureAwait(false);
            engine.ModelGuard.Reset();
            return new ReindexResult(reindexed, skipped, chunkTotal);
        }
        finally
        {
            engine.ReindexGate.Release();
        }
    }

    /// <summary>
    /// Re-embeds one document; the caller holds its document lock. Returns whether this run moved it to the configured model,
    /// and how many chunks it wrote. A document that changed under it is abandoned without further writes.
    /// </summary>
    private async Task<(bool Reindexed, int Chunks)> ReembedAsync(ManifestRecord listed, int batchSize, CancellationToken ct)
    {
        var fresh = await engine.Manifests.GetAsync(listed.Partition, listed.DocumentId, ct).ConfigureAwait(false);
        if (fresh is null || string.Equals(fresh.EmbeddingModelId, engine.EmbeddingModelId, StringComparison.Ordinal))
            return (false, 0);

        // Dirty manifests and pending cleanup can leave chunks beyond ChunkCount; re-embed every one that exists.
        var written = 0;
        var span = Math.Max(fresh.ChunkCount, fresh.ChunkHighWater);
        var keys = Enumerable.Range(0, span).Select(i => ChunkKeys.Chunk(fresh.Partition, fresh.DocumentId, i)).ToList();
        for (var start = 0; start < keys.Count; start += batchSize)
        {
            var records = await engine.Chunks.GetAsync(keys.Skip(start).Take(batchSize), false, ct).ConfigureAwait(false);
            if (records.Count == 0) continue;
            IReadOnlyDictionary<string, string>? external = null;
            if (engine.ContentStore is { } contentStore)
                external = await contentStore.GetAsync(
                    records.Select(r => ChunkKeys.Chunk(fresh.Partition, fresh.DocumentId, Convert.ToInt32(r["ChunkIndex"], System.Globalization.CultureInfo.InvariantCulture))).ToList(), ct).ConfigureAwait(false);
            var inputs = records.Select(r => EmbeddingText.For(HeadingPath(r), RequireText(fresh, r, external))).ToList();
            var result = await engine.Embedder.GenerateAsync(inputs, cancellationToken: ct).ConfigureAwait(false);
            if (result.Count != inputs.Count)
                throw new InvalidOperationException($"The embedding generator returned {result.Count} embeddings for {inputs.Count} inputs.");
            for (var i = 0; i < records.Count; i++)
            {
                if (result[i].Vector.Length != engine.Options.EmbeddingDimensions)
                    throw new InvalidOperationException($"The embedding generator returned a {result[i].Vector.Length}-dimensional vector but {engine.Options.EmbeddingDimensions} dimensions are configured.");
                records[i]["EmbeddingModelId"] = engine.EmbeddingModelId;
                records[i]["Embedding"] = result[i].Vector;
            }

            // Another process may have rewritten the document since the re-read; its chunks must not be overwritten.
            var check = await engine.Manifests.GetAsync(fresh.Partition, fresh.DocumentId, ct).ConfigureAwait(false);
            if (check is null || check.ContentHash != fresh.ContentHash || check.UpdatedAtUnixMs != fresh.UpdatedAtUnixMs)
            {
                engine.Logger.LogWarning("Document {DocumentId} changed during reindex; skipped.", fresh.DocumentId);
                return (false, written);
            }

            await engine.Chunks.UpsertAsync(records, ct).ConfigureAwait(false);
            written += records.Count;
        }

        await engine.Manifests.UpsertAsync(fresh with { EmbeddingModelId = engine.EmbeddingModelId }, ct).ConfigureAwait(false);
        return (true, written);
    }

    /// <summary>True when another owner's heartbeat is at most 5 minutes old. A heartbeat in the future (a clock step) counts as fresh.</summary>
    private bool IsHeldByAnother(ReindexMarker marker, DateTimeOffset now) =>
        !string.Equals(marker.OwnerId, engine.InstanceId, StringComparison.Ordinal)
        && now - DateTimeOffset.FromUnixTimeMilliseconds(marker.HeartbeatUnixMs) <= StaleAfter;

    private static InvalidOperationException MarkerHeld(string targetModelId) =>
        new($"Another reindex (to '{targetModelId}') holds the marker; it is considered abandoned after 5 minutes without a heartbeat.");

    public async Task<bool> DeleteDocumentAsync(string documentId, IReadOnlyDictionary<string, string>? partition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        IReadOnlyDictionary<string, string> values = partition ?? new Dictionary<string, string>();
        if (values.Count > 0)
        {
            var expected = new HashSet<string>(engine.Options.PartitionKeys, StringComparer.Ordinal);
            if (!expected.SetEquals(values.Keys))
                throw new ArgumentException(
                    $"The partition keys [{string.Join(", ", values.Keys.OrderBy(k => k, StringComparer.Ordinal))}] do not match the configured partition keys [{string.Join(", ", expected.OrderBy(k => k, StringComparer.Ordinal))}].",
                    nameof(partition));
        }

        var scope = new ResolvedScope(values, ChunkKeys.Partition(values), null, RagPermissions.All, false);
        return await engine.Ingestor.DeleteUnguardedAsync(documentId, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RagStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await engine.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var groups = new Dictionary<string, (IReadOnlyDictionary<string, string> Values, int Docs, int Chunks)>(StringComparer.Ordinal);
        var models = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (var m in engine.Manifests.ListAsync(r => (int)r["ChunkCount"]! >= 0, cancellationToken).ConfigureAwait(false))
        {
            models.Add(m.EmbeddingModelId);
            groups.TryGetValue(m.Partition, out var g);
            groups[m.Partition] = (m.PartitionValues, g.Docs + 1, g.Chunks + m.ChunkCount);
        }

        var partitions = groups.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PartitionStats(kv.Value.Values, kv.Value.Docs, kv.Value.Chunks))
            .ToList();
        return new RagStats(partitions, models.ToList());
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        var probe = await engine.Embedder.GenerateAsync([ProbeText], cancellationToken: ct).ConfigureAwait(false);
        if (probe.Count != 1)
            throw new InvalidOperationException("The embedding generator did not return exactly one embedding for the dimension probe.");
        if (probe[0].Vector.Length != engine.Options.EmbeddingDimensions)
            throw new InvalidOperationException(
                $"The embedding generator returned {probe[0].Vector.Length} dimensions, but {engine.Options.EmbeddingDimensions} are configured. Reindexing into a different dimension requires a new collection.");
    }

    private static string RequireText(ManifestRecord m, Dictionary<string, object?> r, IReadOnlyDictionary<string, string>? external)
    {
        var index = Convert.ToInt32(r["ChunkIndex"], System.Globalization.CultureInfo.InvariantCulture);
        var text = external is not null
            ? (external.TryGetValue(ChunkKeys.Chunk(m.Partition, m.DocumentId, index), out var e) ? e : null)
            : r.TryGetValue("Text", out var t) ? t as string : null;
        return !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidOperationException(
            $"Chunk {index} of document '{m.DocumentId}' has no stored text and cannot be re-embedded.");
    }

    private static IReadOnlyList<string> HeadingPath(Dictionary<string, object?> r) =>
        r.TryGetValue("HeadingPath", out var h) && h is string s && s.Length > 0 ? s.Split(" > ") : [];
}
