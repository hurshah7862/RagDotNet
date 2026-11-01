using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using RagDotNet.Access;
using RagDotNet.Diagnostics;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Retrieval;

internal sealed class Retriever(RagEngine engine)
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve");
        var started = Stopwatch.GetTimestamp();
        activity?.SetTag("rag.top_k", o?.TopK);
        if (activity is not null && engine.Options.EnableSensitiveData) activity.SetTag("rag.query", query);
        try
        {
            var results = await RetrieveCoreAsync(query, scope, o!, ct).ConfigureAwait(false);
            activity?.SetTag("rag.result.count", results.Count);
            RagTelemetry.RetrieveCount.Record(results.Count);
            return results;
        }
        catch (Exception ex)
        {
            RagTelemetry.Fail(activity, ex);
            throw;
        }
        finally
        {
            RagTelemetry.RetrieveDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<IReadOnlyList<RetrievedChunk>> RetrieveCoreAsync(string query, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(o);
        if (o.TopK is < 1 or > 100)
            throw new ArgumentOutOfRangeException("TopK", o.TopK, "TopK must be between 1 and 100.");

        if (!scope.Permissions.HasFlag(RagPermissions.Read))
            throw new RagAccessDeniedException("retrieve", RagPermissions.Read, "The access scope does not grant Read.");

        var options = engine.Options;
        var filter = ReadFilters.Build(scope, o.IncludeCommon, options.PartitionKeys.Count > 0, o.Filter, options.Metadata, out var matchesNothing);
        if (matchesNothing) return [];

        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        await engine.ModelGuard.EnsureCompatibleAsync(ct).ConfigureAwait(false);
        var embeddings = await engine.Embedder.GenerateAsync([query], cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count != 1)
            throw new InvalidOperationException("The embedding generator did not return exactly one embedding for the query.");
        var vector = embeddings[0].Vector;
        if (vector.Length != options.EmbeddingDimensions)
            throw new InvalidOperationException($"The embedding generator returned {vector.Length} dimensions, but {options.EmbeddingDimensions} are configured.");

        var results = new List<RetrievedChunk>();
        await foreach (var hit in engine.Chunks.SearchAsync(vector, o.TopK, filter, ct).ConfigureAwait(false))
        {
            var score = hit.Score ?? throw new InvalidOperationException("The vector store returned a result without a score.");
            if (options.DistanceFunction == DistanceFunction.CosineDistance) score = 1 - score;
            if (o.MinScore is { } min && score < min) continue;
            results.Add(Map(hit.Record, score));
        }

        if (engine.ContentStore is { } contentStore && results.Count > 0)
        {
            // One batch call for the candidates that already passed the scope filter and MinScore.
            var texts = await contentStore.GetAsync(results.Select(c => c.ChunkKey).ToList(), ct).ConfigureAwait(false);
            var kept = new List<RetrievedChunk>(results.Count);
            foreach (var c in results)
            {
                if (texts.TryGetValue(c.ChunkKey, out var text) && !string.IsNullOrWhiteSpace(text))
                    kept.Add(c with { Text = text });
                else
                {
                    engine.RecordMissingContent(1);
                    engine.Logger.LogWarning("Dropped chunk {ChunkKey} of document {DocumentId}: the content store returned no text for it.", c.ChunkKey, c.DocumentId);
                }
            }

            results = kept;
        }

        results.Sort((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.DocumentId, b.DocumentId);
            return c != 0 ? c : a.ChunkIndex.CompareTo(b.ChunkIndex);
        });
        return results;
    }

    private RetrievedChunk Map(Dictionary<string, object?> r, double score)
    {
        var options = engine.Options;
        var documentId = (string)r["DocumentId"]!;
        var chunkIndex = Convert.ToInt32(r["ChunkIndex"], CultureInfo.InvariantCulture);

        if (!r.TryGetValue("Partition", out var pv) || pv is not string partition)
            throw new InvalidOperationException("Retrieved record has no Partition field; it was written by an older version. Re-ingest into a new collection.");
        var key = ChunkKeys.Chunk(partition, documentId, chunkIndex);

        var metadata = new Dictionary<string, object?>();
        foreach (var name in options.Metadata.Fields.Keys)
            if (r.TryGetValue(RagFilterTranslator.MetaPrefix + name, out var v)) metadata[name] = v;

        var heading = r.TryGetValue("HeadingPath", out var h) ? h as string : null;
        IReadOnlyList<string> path = string.IsNullOrEmpty(heading) ? [] : heading.Split(" > ");

        return new RetrievedChunk(
            key,
            documentId,
            chunkIndex,
            r.TryGetValue("Text", out var t) ? t as string ?? "" : "",
            score,
            ScoreKind.Similarity,
            r.TryGetValue("PageStart", out var ps) ? (int?)ps : null,
            r.TryGetValue("PageEnd", out var pe) ? (int?)pe : null,
            path,
            r.TryGetValue("SourceName", out var sn) ? sn as string ?? "" : "",
            metadata);
    }
}
