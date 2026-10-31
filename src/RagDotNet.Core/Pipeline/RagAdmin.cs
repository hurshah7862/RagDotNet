using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Storage;

namespace RagDotNet.Pipeline;

internal sealed class RagAdmin(RagEngine engine) : IRagAdmin
{
    private const string ProbeText = "dimension probe";

    public async Task<ReindexResult> ReindexAsync(IProgress<ReindexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureContentModeAsync(ct).ConfigureAwait(false);
        var batchSize = Math.Max(1, engine.Options.EmbeddingBatchSize);
        var probed = false;
        int reindexed = 0, skipped = 0, chunkTotal = 0;

        // Materialize first: writing manifests while paging could skip or revisit documents.
        var all = new List<Storage.ManifestRecord>();
        await foreach (var listed in engine.Manifests.ListAsync(r => (int)r["ChunkCount"]! >= 0, ct).ConfigureAwait(false))
            all.Add(listed);

        foreach (var m in all)
        {
            if (m.EmbeddingModelId == engine.EmbeddingModelId)
            {
                skipped++;
            }
            else
            {
                if (!probed)
                {
                    await ProbeAsync(ct).ConfigureAwait(false); // before any write
                    probed = true;
                }

                // Dirty manifests and pending cleanup can leave chunks beyond ChunkCount; re-embed every one that exists.
                var span = Math.Max(m.ChunkCount, m.ChunkHighWater);
                var keys = Enumerable.Range(0, span).Select(i => ChunkKeys.Chunk(m.Partition, m.DocumentId, i)).ToList();
                for (var start = 0; start < keys.Count; start += batchSize)
                {
                    var records = await engine.Chunks.GetAsync(keys.Skip(start).Take(batchSize), false, ct).ConfigureAwait(false);
                    if (records.Count == 0) continue;
                    IReadOnlyDictionary<string, string>? external = null;
                    if (engine.ContentStore is { } contentStore)
                        external = await contentStore.GetAsync(
                            records.Select(r => ChunkKeys.Chunk(m.Partition, m.DocumentId, Convert.ToInt32(r["ChunkIndex"], System.Globalization.CultureInfo.InvariantCulture))).ToList(), ct).ConfigureAwait(false);
                    var inputs = records.Select(r => EmbeddingText.For(HeadingPath(r), RequireText(m, r, external))).ToList();
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

                    await engine.Chunks.UpsertAsync(records, ct).ConfigureAwait(false);
                    chunkTotal += records.Count;
                }

                await engine.Manifests.UpsertAsync(m with { EmbeddingModelId = engine.EmbeddingModelId }, ct).ConfigureAwait(false);
                reindexed++;
            }

            progress?.Report(new ReindexProgress(reindexed + skipped, m.DocumentId));
        }

        engine.ModelGuard.Reset();
        return new ReindexResult(reindexed, skipped, chunkTotal);
    }

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

        var scope = new ResolvedScope(values, ChunkKeys.Partition(values), null);
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

    private static string RequireText(Storage.ManifestRecord m, Dictionary<string, object?> r, IReadOnlyDictionary<string, string>? external)
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
