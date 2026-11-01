using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using RagDotNet.Access;
using RagDotNet.Diagnostics;
using RagDotNet.Embedding;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Retrieval;

internal sealed class Retriever(RagEngine engine)
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(IReadOnlyList<string> queries, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve");
        var started = Stopwatch.GetTimestamp();
        activity?.SetTag("rag.top_k", o?.TopK);
        activity?.SetTag("rag.query.count", queries.Count);
        if (activity is not null && engine.Options.EnableSensitiveData) activity.SetTag("rag.query", string.Join("\n", queries));
        try
        {
            var results = await RetrieveCoreAsync(queries, scope, o!, activity, ct).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<RetrievedChunk>> RetrieveCoreAsync(IReadOnlyList<string> queries, ResolvedScope scope, RetrievalOptions o, Activity? activity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0) throw new ArgumentException("At least one query is required.", nameof(queries));
        if (queries.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Queries must not be null, empty or whitespace.", nameof(queries));
        ArgumentNullException.ThrowIfNull(o);
        if (o.TopK is < 1 or > 100)
            throw new ArgumentOutOfRangeException("TopK", o.TopK, "TopK must be between 1 and 100.");

        if (!scope.Permissions.HasFlag(RagPermissions.Read))
            throw new RagAccessDeniedException("retrieve", RagPermissions.Read, "retrieve: The access scope does not grant Read.");

        var options = engine.Options;
        var partitioned = options.PartitionKeys.Count > 0;
        var filter = ReadFilters.Build(scope, o.IncludeCommon, partitioned, o.Filter, options.Metadata, out var matchesNothing);
        if (matchesNothing) return [];

        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        if (await engine.ModelGuard.EnsureAsync(GuardMode.Read, ct).ConfigureAwait(false))
        {
            // A reindex to the configured model is in progress: search only chunks already re-embedded.
            filter = ReadFilters.Build(scope, o.IncludeCommon, partitioned, o.Filter, options.Metadata, out _, requireModelId: engine.EmbeddingModelId);
            if (engine.ModelGuard.TakeReindexWarning())
                engine.Logger.LogWarning(
                    "A reindex to embedding model {ModelId} is in progress; answers include only documents already re-embedded.", engine.EmbeddingModelId);
            activity?.SetTag("rag.reindex.in_progress", true);
        }
        var embeddings = await engine.Embedder.GenerateAsync(queries, cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count != queries.Count)
            throw new InvalidOperationException($"The embedding generator returned {embeddings.Count} embeddings for {queries.Count} queries.");

        var rankings = new List<IReadOnlyList<RetrievedChunk>>(queries.Count);
        foreach (var embedding in embeddings)
        {
            var vector = embedding.Vector;
            if (vector.Length != options.EmbeddingDimensions)
                throw new InvalidOperationException($"The embedding generator returned {vector.Length} dimensions, but {options.EmbeddingDimensions} are configured.");

            var ranking = new List<RetrievedChunk>();
            await foreach (var hit in engine.Chunks.SearchAsync(vector, o.TopK, filter, ct).ConfigureAwait(false))
            {
                var score = hit.Score ?? throw new InvalidOperationException("The vector store returned a result without a score.");
                if (options.DistanceFunction == DistanceFunction.CosineDistance) score = 1 - score;
                if (o.MinScore is { } min && score < min) continue;
                ranking.Add(Map(hit.Record, score));
            }

            ranking.Sort(RankFusion.Compare);
            rankings.Add(ranking);
        }

        // Text is fetched once, below, for the final results only.
        var results = queries.Count == 1 ? rankings[0].ToList() : RankFusion.Reciprocal(rankings, o.TopK).ToList();

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

        results.Sort(RankFusion.Compare);
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
