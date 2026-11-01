using System.Globalization;
using System.Linq.Expressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using RagDotNet.Access;
using RagDotNet.Configuration;
using RagDotNet.Diagnostics;
using RagDotNet.Embedding;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Retrieval;

internal sealed class Retriever(RagEngine engine)
{
    /// <summary>The checked state a search needs: the final store filter, or that the scope and filter match nothing.</summary>
    internal sealed class Prepared
    {
        internal Prepared(int topK, double? minScore, Expression<Func<Dictionary<string, object?>, bool>>? filter, bool matchesNothing, bool reindexInProgress)
        {
            TopK = topK;
            MinScore = minScore;
            Filter = filter;
            MatchesNothing = matchesNothing;
            ReindexInProgress = reindexInProgress;
        }

        /// <summary>The validated TopK, captured once so a search never re-reads the caller's options after the chat calls.</summary>
        public int TopK { get; }

        /// <summary>The MinScore captured at prepare time.</summary>
        public double? MinScore { get; }

        public Expression<Func<Dictionary<string, object?>, bool>>? Filter { get; }

        /// <summary>True when nothing can match: the search returns no results without touching the store or the model.</summary>
        public bool MatchesNothing { get; }

        public bool ReindexInProgress { get; }
    }

    /// <summary>Checks and prepares and then searches. Used when the queries are already known.</summary>
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(IReadOnlyList<string> queries, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        ValidateQueries(queries);
        var prepared = await PrepareAsync(scope, o, ct).ConfigureAwait(false);
        return await SearchAsync(queries, prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs every check that does not depend on the queries — options, the Read permission, the read filter, store
    /// readiness and the embedding model guard — before any query planning spends chat calls.
    /// </summary>
    public async Task<Prepared> PrepareAsync(ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await PrepareCoreAsync(scope, o, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The search span has not started (it must not enclose the chat calls), so a failed check gets its own short
            // rag.retrieve span and duration measurement; a successful prepare records neither.
            var elapsed = Stopwatch.GetElapsedTime(started);
            using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve");
            activity?.SetStartTime(DateTime.UtcNow - elapsed);
            RagTelemetry.Fail(activity, ex);
            RagTelemetry.RetrieveDuration.Record(elapsed.TotalSeconds);
            throw;
        }
    }

    private async Task<Prepared> PrepareCoreAsync(ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (o.TopK is < 1 or > 100)
            throw new ArgumentOutOfRangeException("TopK", o.TopK, "TopK must be between 1 and 100.");

        if (!scope.Permissions.HasFlag(RagPermissions.Read))
            throw new RagAccessDeniedException("retrieve", RagPermissions.Read, "retrieve: The access scope does not grant Read.");

        var options = engine.Options;
        var partitioned = options.PartitionKeys.Count > 0;
        var filter = ReadFilters.Build(scope, o.IncludeCommon, partitioned, o.Filter, options.Metadata, out var matchesNothing);
        if (matchesNothing) return new Prepared(o.TopK, o.MinScore, null, matchesNothing: true, reindexInProgress: false);

        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        var reindexing = await engine.ModelGuard.EnsureAsync(GuardMode.Read, ct).ConfigureAwait(false);
        if (reindexing)
        {
            // A reindex to the configured model is in progress: search only chunks already re-embedded.
            filter = ReadFilters.Build(scope, o.IncludeCommon, partitioned, o.Filter, options.Metadata, out _, requireModelId: engine.EmbeddingModelId);
            if (engine.ModelGuard.TakeReindexWarning())
                engine.Logger.LogWarning(
                    "A reindex to embedding model {ModelId} is in progress; answers include only documents already re-embedded.", engine.EmbeddingModelId);
        }

        return new Prepared(o.TopK, o.MinScore, filter, matchesNothing: false, reindexInProgress: reindexing);
    }

    /// <summary>Searches the store with already-planned queries and the state returned by <see cref="PrepareAsync"/>.</summary>
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(IReadOnlyList<string> queries, Prepared prepared, CancellationToken ct)
    {
        ValidateQueries(queries);
        ArgumentNullException.ThrowIfNull(prepared);
        using var activity = RagTelemetry.ActivitySource.StartActivity("rag.retrieve");
        var started = Stopwatch.GetTimestamp();
        activity?.SetTag("rag.top_k", prepared.TopK);
        activity?.SetTag("rag.query.count", queries.Count);
        if (activity is not null && engine.Options.EnableSensitiveData) activity.SetTag("rag.query", string.Join("\n", queries));
        if (prepared.ReindexInProgress) activity?.SetTag("rag.reindex.in_progress", true);
        try
        {
            IReadOnlyList<RetrievedChunk> results = prepared.MatchesNothing ? [] : await SearchCoreAsync(queries, prepared, ct).ConfigureAwait(false);
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

    private static void ValidateQueries(IReadOnlyList<string> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0) throw new ArgumentException("At least one query is required.", nameof(queries));
        if (queries.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Queries must not be null, empty or whitespace.", nameof(queries));
    }

    private async Task<List<RetrievedChunk>> SearchCoreAsync(IReadOnlyList<string> queries, Prepared prepared, CancellationToken ct)
    {
        var options = engine.Options;
        var filter = prepared.Filter;
        var vectors = await EmbedQueriesAsync(queries, ct).ConfigureAwait(false);

        var rankings = new List<IReadOnlyList<RetrievedChunk>>(queries.Count);
        foreach (var vector in vectors)
        {
            var ranking = new List<RetrievedChunk>();
            await foreach (var hit in engine.Chunks.SearchAsync(vector, prepared.TopK, filter, ct).ConfigureAwait(false))
            {
                var score = hit.Score ?? throw new InvalidOperationException("The vector store returned a result without a score.");
                if (options.DistanceFunction == DistanceFunction.CosineDistance) score = 1 - score;
                if (prepared.MinScore is { } min && score < min) continue;
                ranking.Add(Map(hit.Record, score));
            }

            ranking.Sort(RankFusion.Compare);
            rankings.Add(ranking);
        }

        // Text is fetched once, below, for the final results only.
        var results = queries.Count == 1 ? rankings[0].ToList() : RankFusion.Reciprocal(rankings, prepared.TopK).ToList();

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

    /// <summary>Embeds the queries in batches of <see cref="RagOptions.EmbeddingBatchSize"/>, as ingestion does, keeping their order.</summary>
    private async Task<List<ReadOnlyMemory<float>>> EmbedQueriesAsync(IReadOnlyList<string> queries, CancellationToken ct)
    {
        var batchSize = Math.Max(1, engine.Options.EmbeddingBatchSize);
        var dims = engine.Options.EmbeddingDimensions;
        var vectors = new List<ReadOnlyMemory<float>>(queries.Count);
        for (var start = 0; start < queries.Count; start += batchSize)
        {
            var batch = queries.Skip(start).Take(batchSize).ToList();
            var embeddings = await engine.Embedder.GenerateAsync(batch, cancellationToken: ct).ConfigureAwait(false);
            if (embeddings.Count != batch.Count)
                throw new InvalidOperationException($"The embedding generator returned {embeddings.Count} embeddings for {batch.Count} queries.");
            foreach (var embedding in embeddings)
            {
                if (embedding.Vector.Length != dims)
                    throw new InvalidOperationException($"The embedding generator returned {embedding.Vector.Length} dimensions, but {dims} are configured.");
                vectors.Add(embedding.Vector);
            }
        }

        return vectors;
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
