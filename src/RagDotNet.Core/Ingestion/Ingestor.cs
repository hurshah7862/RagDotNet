using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Content;
using RagDotNet.Diagnostics;
using RagDotNet.Documents;
using RagDotNet.Embedding;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Ingestion;

internal sealed class Ingestor(RagEngine engine)
{
    public async Task<IngestResult> IngestAsync(
        DocumentSource s, ResolvedScope scope, IngestOptions? o, IProgress<IngestProgress>? p, CancellationToken ct)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.ingest");
        activity?.SetTag("rag.document.id", s.DocumentId);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await IngestCoreAsync(s, scope, o, p, ct).ConfigureAwait(false);
            activity?.SetTag("rag.ingest.status", result.Status.ToString());
            activity?.SetTag("rag.chunk.count", result.ChunkCount);
            RagTelemetry.IngestChunks.Record(result.ChunkCount);
            return result;
        }
        catch (Exception ex)
        {
            RagTelemetry.Fail(activity, ex);
            throw;
        }
        finally
        {
            RagTelemetry.IngestDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<IngestResult> IngestCoreAsync(
        DocumentSource s, ResolvedScope scope, IngestOptions? o, IProgress<IngestProgress>? p, CancellationToken ct)
    {
        var options = engine.Options;
        if (!scope.IsFullAccess && !scope.Permissions.HasFlag(RagPermissions.Write))
            throw new RagAccessDeniedException("ingest", RagPermissions.Write, "The access scope does not grant Write.");

        scope = TargetFor(s, scope);
        var metadata = BuildMetadata(s, scope);
        if (!scope.IsFullAccess && !RagFilterEvaluator.Matches(scope.UserFilter, options.Metadata, s.DocumentId, metadata))
            throw new RagAccessDeniedException("ingest", null, "The document's metadata is outside the caller's read filter.");
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureAsync(GuardMode.Write, ct).ConfigureAwait(false);

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Reading, 0, 0));
        var bytes = await ReadBytesAsync(s, ct).ConfigureAwait(false);
        var contentHash = Hashing.ContentHash(bytes);
        var chunking = o?.Chunking ?? EffectiveChunkingFor(s.ContentType);
        var chunkingHash = Hashing.ChunkingHash(chunking, engine.TokenizerName);

        var metadataHash = Hashing.MetadataHash(metadata);

        // Held from the manifest read through the final manifest write, so concurrent ingests or deletes
        // of the same document in this process cannot leave chunks the manifest does not cover.
        using var documentLock = await engine.DocumentLocks.AcquireAsync(scope.PartitionKey, s.DocumentId, ct).ConfigureAwait(false);
        var existing = await engine.Manifests.GetAsync(scope.PartitionKey, s.DocumentId, ct).ConfigureAwait(false);
        if (existing is not null && !IsVisible(scope, existing))
            throw new RagAccessDeniedException("ingest", null, "A document with this id exists and is not visible to the caller.");

        if (existing is not null && existing.ContentHash == contentHash && existing.ChunkingHash == chunkingHash
            && existing.MetadataHash == metadataHash)
        {
            // Finish cleanup left behind by an interrupted earlier run.
            if (existing.ChunkHighWater > existing.ChunkCount)
            {
                await DeleteRangeAsync(scope, s.DocumentId, existing.ChunkCount, existing.ChunkHighWater, ct).ConfigureAwait(false);
                await engine.Manifests.UpsertAsync(existing with { ChunkHighWater = existing.ChunkCount }, ct).ConfigureAwait(false);
            }

            p?.Report(new IngestProgress(s.DocumentId, IngestStage.Completed, existing.ChunkCount, existing.ChunkCount));
            return new IngestResult(s.DocumentId, IngestStatus.Unchanged, existing.ChunkCount, []);
        }

        var ingestActivity = Activity.Current;
        ParsedDocument parsed;
        using (RagTelemetry.ActivitySource.StartActivity("rag.read"))
        {
            var reader = engine.Readers.Select(s.ContentType, s.FileName);
            using var ms = new MemoryStream(bytes, writable: false);
            parsed = await reader.ReadAsync(ms, new DocumentReadContext(s.FileName, s.ContentType), ct).ConfigureAwait(false);
        }

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Chunking, 0, 0));
        IReadOnlyList<Chunk> chunks;
        using (RagTelemetry.ActivitySource.StartActivity("rag.chunk"))
            chunks = await engine.Chunker.ChunkAsync(parsed, chunking, ct).ConfigureAwait(false);
        var total = chunks.Count;

        var warnings = new List<IngestWarning>(parsed.Warnings);
        if (total == 0)
            warnings.Add(new IngestWarning("EmptyDocument", $"Document '{s.DocumentId}' produced no text to index."));

        List<ReadOnlyMemory<float>> vectors;
        using (RagTelemetry.ActivitySource.StartActivity("rag.embed"))
            vectors = await EmbedAsync(s.DocumentId, chunks, p, ct).ConfigureAwait(false);

        using var storeActivity = RagTelemetry.ActivitySource.StartActivity("rag.store");

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Storing, total, total));
        var sourceName = s.Title ?? s.FileName;
        var records = new List<Dictionary<string, object?>>(total);
        for (var i = 0; i < total; i++)
        {
            var c = chunks[i];
            var key = ChunkKeys.Chunk(scope.PartitionKey, s.DocumentId, c.Index);
            var r = new Dictionary<string, object?>
            {
                ["Key"] = options.KeyType == RagKeyType.Guid ? ChunkKeys.ToGuid(key) : key,
                ["DocumentId"] = s.DocumentId,
                ["Partition"] = scope.PartitionKey,
                ["ChunkIndex"] = c.Index,
                ["ContentHash"] = contentHash,
                ["ChunkingHash"] = chunkingHash,
                ["EmbeddingModelId"] = engine.EmbeddingModelId,
                ["PageStart"] = c.PageStart,
                ["PageEnd"] = c.PageEnd,
                ["HeadingPath"] = string.Join(" > ", c.HeadingPath),
                ["SourceName"] = sourceName,
            };
            if (engine.ContentStore is null) r["Text"] = c.Text;
            foreach (var (name, value) in metadata) r[RecordDefinitions.MetaName(name)] = value;
            r["Embedding"] = vectors[i];
            records.Add(r);
        }

        // Crash-convergent order. (a) record the high-water mark under the old hashes so any chunk we
        // may write stays tracked; (b) chunks; (c) manifest with the new hashes; (d) stale chunks; (e) clear the mark.
        var partitionValues = new Dictionary<string, string>(scope.Partition);
        var docKey = ChunkKeys.Document(scope.PartitionKey, s.DocumentId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var highWater = Math.Max(Math.Max(existing?.ChunkHighWater ?? 0, existing?.ChunkCount ?? 0), total);
        var pending = existing is not null
            ? existing with { ContentHash = "", ChunkHighWater = highWater } // dirty marker: never matches a real hash
            : new ManifestRecord(docKey, s.DocumentId, scope.PartitionKey, partitionValues, "", "", engine.EmbeddingModelId,
                0, sourceName, now, options.ContentStorage, "", highWater, metadata);
        await engine.Manifests.UpsertAsync(pending, ct).ConfigureAwait(false);
        if (engine.ContentStore is { } contentStore && total > 0)
        {
            // Text first: a vector record never exists without its text.
            var contents = chunks.Select(c => new ChunkContent(
                ChunkKeys.Chunk(scope.PartitionKey, s.DocumentId, c.Index), s.DocumentId, partitionValues, c.Index,
                c.Text, c.PageStart, c.PageEnd, c.HeadingPath)).ToList();
            using (RagTelemetry.ActivitySource.StartActivity("rag.content", ActivityKind.Internal, ingestActivity?.Context ?? default))
                await contentStore.UpsertAsync(contents, ct).ConfigureAwait(false);
        }

        await engine.Chunks.UpsertAsync(records, ct).ConfigureAwait(false);
        var committed = new ManifestRecord(
            docKey, s.DocumentId, scope.PartitionKey, partitionValues, contentHash, chunkingHash, engine.EmbeddingModelId,
            total, sourceName, now, options.ContentStorage, metadataHash, highWater, metadata);
        await engine.Manifests.UpsertAsync(committed, ct).ConfigureAwait(false);
        await DeleteRangeAsync(scope, s.DocumentId, total, highWater, ct).ConfigureAwait(false);
        if (highWater > total)
            await engine.Manifests.UpsertAsync(committed with { ChunkHighWater = total }, ct).ConfigureAwait(false);

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Completed, total, total));
        engine.Logger.LogDebug("Ingested document {DocumentId}: {ChunkCount} chunks", s.DocumentId, total);
        return new IngestResult(s.DocumentId, existing is null || (existing.ContentHash.Length == 0 && existing.ChunkCount == 0) ?IngestStatus.Created : IngestStatus.Updated, total, warnings);
    }

    public async Task<bool> DeleteAsync(string documentId, ResolvedScope scope, CancellationToken ct)
    {
        if (!scope.IsFullAccess && !scope.Permissions.HasFlag(RagPermissions.Delete))
            throw new RagAccessDeniedException("delete", RagPermissions.Delete, "The access scope does not grant Delete.");

        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureAsync(GuardMode.Write, ct).ConfigureAwait(false);
        return await DeleteUnguardedAsync(documentId, scope, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes without the embedding-model check; for IRagAdmin.</summary>
    public async Task<bool> DeleteUnguardedAsync(string documentId, ResolvedScope scope, CancellationToken ct)
    {
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureContentModeAsync(ct).ConfigureAwait(false);
        using var documentLock = await engine.DocumentLocks.AcquireAsync(scope.PartitionKey, documentId, ct).ConfigureAwait(false);
        var existing = await engine.Manifests.GetAsync(scope.PartitionKey, documentId, ct).ConfigureAwait(false);
        // A document the caller cannot read is reported exactly like a missing one.
        if (existing is null || !IsVisible(scope, existing)) return false;

        // Chunks first, so a crash leaves the manifest and a retry finishes the job.
        await DeleteRangeAsync(scope, documentId, 0, Math.Max(existing.ChunkCount, existing.ChunkHighWater), ct).ConfigureAwait(false);
        await engine.Manifests.DeleteAsync(scope.PartitionKey, documentId, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>True when the scope's read filter matches the stored manifest metadata. Full access sees everything.</summary>
    private bool IsVisible(ResolvedScope scope, ManifestRecord existing) =>
        scope.IsFullAccess || RagFilterEvaluator.Matches(scope.UserFilter, engine.Options.Metadata, existing.DocumentId, existing.Metadata);

    private async Task DeleteRangeAsync(ResolvedScope scope, string documentId, int from, int to, CancellationToken ct)
    {
        if (to <= from) return;
        var keys = Enumerable.Range(from, to - from).Select(i => ChunkKeys.Chunk(scope.PartitionKey, documentId, i)).ToList();
        if (engine.ContentStore is { } contentStore)
            await contentStore.DeleteAsync(keys, ct).ConfigureAwait(false);
        await engine.Chunks.DeleteAsync(keys, ct).ConfigureAwait(false);
    }

    private ChunkingOptions EffectiveChunkingFor(string? contentType)
    {
        if (contentType is not null
            && engine.Options.ChunkingByContentType.TryGetValue(contentType.Split(';')[0].Trim(), out var byType))
            return byType;
        return engine.Options.Chunking;
    }

    private static async Task<byte[]> ReadBytesAsync(DocumentSource s, CancellationToken ct)
    {
        if (s.FilePath is not null) return await File.ReadAllBytesAsync(s.FilePath, ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.Stream!.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    private async Task<List<ReadOnlyMemory<float>>> EmbedAsync(
        string documentId, IReadOnlyList<Chunk> chunks, IProgress<IngestProgress>? p, CancellationToken ct)
    {
        var batchSize = Math.Max(1, engine.Options.EmbeddingBatchSize);
        var dims = engine.Options.EmbeddingDimensions;
        var vectors = new List<ReadOnlyMemory<float>>(chunks.Count);
        p?.Report(new IngestProgress(documentId, IngestStage.Embedding, 0, chunks.Count));
        for (var start = 0; start < chunks.Count; start += batchSize)
        {
            var batch = chunks.Skip(start).Take(batchSize).Select(c => EmbeddingText.For(c.HeadingPath, c.Text)).ToList();
            var result = await engine.Embedder.GenerateAsync(batch, cancellationToken: ct).ConfigureAwait(false);
            if (result.Count != batch.Count)
                throw new InvalidOperationException($"The embedding generator returned {result.Count} embeddings for {batch.Count} inputs.");
            foreach (var e in result)
            {
                if (e.Vector.Length != dims)
                    throw new InvalidOperationException($"The embedding generator returned a {e.Vector.Length}-dimensional vector but {dims} dimensions are configured.");
                vectors.Add(e.Vector);
            }

            p?.Report(new IngestProgress(documentId, IngestStage.Embedding, vectors.Count, chunks.Count));
        }

        return vectors;
    }

    /// <summary>Scoped calls write to their own area. Full access writes to the tenant named by the document's metadata, or to the common area when it names none.</summary>
    private ResolvedScope TargetFor(DocumentSource s, ResolvedScope scope)
    {
        if (!scope.IsFullAccess) return scope;
        var keys = engine.Options.PartitionKeys;
        if (keys.Count == 0) return ResolvedScope.FullAccess;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!s.Metadata.TryGetValue(key, out var v) || v is null) continue;
            if (v is not string str)
                throw new ArgumentException($"Partition value for '{key}' must be a string.", nameof(s));
            if (str.Length == 0)
                throw new ArgumentException($"Partition value for '{key}' must not be empty.", nameof(s));
            values[key] = str;
        }

        if (values.Count == 0) return ResolvedScope.FullAccess;
        if (values.Count != keys.Count)
        {
            var missing = keys.First(k => !values.ContainsKey(k));
            throw new ArgumentException(
                $"Full-access ingest must set all partition keys [{string.Join(", ", keys)}] or none; '{missing}' is missing.", nameof(s));
        }

        return new ResolvedScope(values, ChunkKeys.Partition(values), null, RagPermissions.All, true);
    }

    /// <summary>Validates caller metadata and returns a value for every declared field (null when omitted), with partition values stamped.</summary>
    private Dictionary<string, object?> BuildMetadata(DocumentSource s, ResolvedScope scope)
    {
        var schema = engine.Options.Metadata;
        foreach (var (key, value) in s.Metadata)
        {
            if (!schema.Fields.TryGetValue(key, out var type))
                throw new ArgumentException($"Metadata key '{key}' is not declared. Declare it with RagOptions.Metadata.Add<T>().", nameof(s));
            if (value is not null && !type.IsInstanceOfType(value) && !(type == typeof(long) && value is int))
                throw new ArgumentException($"Metadata key '{key}' must be of type {type.Name} but was {value.GetType().Name}.", nameof(s));
            if (value is string[] array && array.Any(e => e is null))
                throw new ArgumentException($"Metadata key '{key}' must not contain null elements.", nameof(s));
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, type) in schema.Fields)
        {
            s.Metadata.TryGetValue(name, out var v);
            result[name] = type == typeof(long) && v is int i ? (long)i
                : type == typeof(string[]) ? v ?? Array.Empty<string>() // omitted arrays are stored empty so Contains never sees null
                : v;
        }

        foreach (var (k, v) in scope.Partition)
        {
            if (!schema.Fields.TryGetValue(k, out var type) || type != typeof(string))
                throw new InvalidOperationException($"Partition key '{k}' must be declared as a string metadata field.");
            result[k] = v; // the partition value always overrides caller metadata
        }

        return result;
    }
}
