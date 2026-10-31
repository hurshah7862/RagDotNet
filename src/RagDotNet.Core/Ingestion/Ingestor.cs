using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using RagDotNet.Access;
using RagDotNet.Chunking;
using RagDotNet.Documents;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Ingestion;

internal sealed class Ingestor(RagEngine engine)
{
    public async Task<IngestResult> IngestAsync(
        DocumentSource s, ResolvedScope scope, IngestOptions? o, IProgress<IngestProgress>? p, CancellationToken ct)
    {
        var options = engine.Options;
        var metadata = BuildMetadata(s, scope);
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureCompatibleAsync(ct).ConfigureAwait(false);

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Reading, 0, 0));
        var bytes = await ReadBytesAsync(s, ct).ConfigureAwait(false);
        var contentHash = Hashing.ContentHash(bytes);
        var chunking = o?.Chunking ?? EffectiveChunkingFor(s.ContentType);
        var chunkingHash = Hashing.ChunkingHash(chunking, engine.TokenizerName);

        var metadataHash = Hashing.MetadataHash(metadata);

        var existing = await engine.Manifests.GetAsync(scope.PartitionKey, s.DocumentId, ct).ConfigureAwait(false);
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

        var reader = engine.Readers.Select(s.ContentType, s.FileName);
        ParsedDocument parsed;
        using (var ms = new MemoryStream(bytes, writable: false))
            parsed = await reader.ReadAsync(ms, new DocumentReadContext(s.FileName, s.ContentType), ct).ConfigureAwait(false);

        p?.Report(new IngestProgress(s.DocumentId, IngestStage.Chunking, 0, 0));
        var chunks = await engine.Chunker.ChunkAsync(parsed, chunking, ct).ConfigureAwait(false);
        var total = chunks.Count;

        var warnings = new List<IngestWarning>(parsed.Warnings);
        if (total == 0)
            warnings.Add(new IngestWarning("EmptyDocument", $"Document '{s.DocumentId}' produced no text to index."));

        var vectors = await EmbedAsync(s.DocumentId, chunks, p, ct).ConfigureAwait(false);

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
                ["ChunkIndex"] = c.Index,
                ["Text"] = c.Text,
                ["ContentHash"] = contentHash,
                ["ChunkingHash"] = chunkingHash,
                ["EmbeddingModelId"] = engine.EmbeddingModelId,
                ["PageStart"] = c.PageStart,
                ["PageEnd"] = c.PageEnd,
                ["HeadingPath"] = string.Join(" > ", c.HeadingPath),
                ["SourceName"] = sourceName,
            };
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
                0, sourceName, now, options.ContentStorage, "", highWater);
        await engine.Manifests.UpsertAsync(pending, ct).ConfigureAwait(false);
        await engine.Chunks.UpsertAsync(records, ct).ConfigureAwait(false);
        var committed = new ManifestRecord(
            docKey, s.DocumentId, scope.PartitionKey, partitionValues, contentHash, chunkingHash, engine.EmbeddingModelId,
            total, sourceName, now, options.ContentStorage, metadataHash, highWater);
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
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureCompatibleAsync(ct).ConfigureAwait(false);
        return await DeleteUnguardedAsync(documentId, scope, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes without the embedding-model check; for IRagAdmin.</summary>
    public async Task<bool> DeleteUnguardedAsync(string documentId, ResolvedScope scope, CancellationToken ct)
    {
        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        var existing = await engine.Manifests.GetAsync(scope.PartitionKey, documentId, ct).ConfigureAwait(false);
        if (existing is null) return false;

        // The scope's read filter must authorize the document. A document with no chunks cannot be authorized.
        if (scope.HasScopeFilter && !await IsVisibleAsync(scope, documentId, ct).ConfigureAwait(false))
            return false;

        // Chunks first, so a crash leaves the manifest and a retry finishes the job.
        await DeleteRangeAsync(scope, documentId, 0, Math.Max(existing.ChunkCount, existing.ChunkHighWater), ct).ConfigureAwait(false);
        await engine.Manifests.DeleteAsync(scope.PartitionKey, documentId, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IsVisibleAsync(ResolvedScope scope, string documentId, CancellationToken ct)
    {
        var translated = RagFilterTranslator.Translate(scope.Filter, engine.Options.Metadata, out var matchesNothing);
        if (matchesNothing || translated is null) return false;

        var p = translated.Parameters[0];
        var idCheck = Expression.Equal(
            Expression.Convert(Expression.Property(p, "Item", Expression.Constant("DocumentId")), typeof(string)),
            Expression.Constant(documentId));
        var body = Expression.AndAlso(translated.Body, idCheck);
        var filter = Expression.Lambda<Func<Dictionary<string, object?>, bool>>(body, p);
        return await engine.Chunks.AnyAsync(filter, ct).ConfigureAwait(false);
    }

    private Task DeleteRangeAsync(ResolvedScope scope, string documentId, int from, int to, CancellationToken ct) =>
        to <= from
            ? Task.CompletedTask
            : engine.Chunks.DeleteAsync(Enumerable.Range(from, to - from).Select(i => ChunkKeys.Chunk(scope.PartitionKey, documentId, i)), ct);

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
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, type) in schema.Fields)
        {
            s.Metadata.TryGetValue(name, out var v);
            result[name] = type == typeof(long) && v is int i ? (long)i : v;
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
