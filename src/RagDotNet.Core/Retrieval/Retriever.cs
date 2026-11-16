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
using RagDotNet.Keyword;
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

        /// <summary>The keyword search this call runs; None is vector-only.</summary>
        public KeywordPath Path { get; init; }

        /// <summary>The keyword index constraint, for the index path.</summary>
        public KeywordFilter? KeywordFilter { get; init; }

        /// <summary>Whether this call requires hybrid search (<see cref="HybridMode.On"/>).</summary>
        public bool HybridRequired { get; init; }

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

        var mode = o.Hybrid ?? options.Hybrid;
        var path = KeywordPath.None;
        if (mode != HybridMode.Off)
        {
            path = engine.KeywordPath;
            if (path == KeywordPath.None)
            {
                if (mode == HybridMode.On)
                    throw new HybridSearchUnavailableException(options.Hybrid == HybridMode.Off
                        ? "Hybrid search was requested for this call, but RagOptions.Hybrid is Off, so no keyword index is kept. Set RagOptions.Hybrid to Auto or On."
                        : UnavailableMessage);
                // With Off in the options, a call left on Auto is simply vector-only.
                if (options.Hybrid != HybridMode.Off && Interlocked.Exchange(ref _unavailableWarned, 1) == 0)
                    engine.Logger.LogWarning(UnavailableMessage);
            }
        }

        KeywordFilter? keywordFilter = null;
        if (path != KeywordPath.None) // also for native, which may fall back to the index
            keywordFilter =KeywordFilters.Build(scope, o.IncludeCommon, partitioned, o.Filter, options.Metadata, out _);

        return new Prepared(o.TopK, o.MinScore, filter, matchesNothing: false, reindexInProgress: reindexing)
        {
            Path = path,
            KeywordFilter = keywordFilter,
            HybridRequired = mode == HybridMode.On,
        };
    }

    private const string UnavailableMessage =
        "Hybrid search is unavailable: the vector store has no native hybrid search and no keyword index is registered. Retrieval is vector-only. Register one with AddInMemoryKeywordIndex() or AddLuceneKeywordIndex().";

    private int _unavailableWarned;
    private int _minScoreWarned;
    private int _driftWarned;

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
            IReadOnlyList<RetrievedChunk> results = prepared.MatchesNothing ? [] : await SearchCoreAsync(queries, prepared, activity, ct).ConfigureAwait(false);
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

    private async Task<List<RetrievedChunk>> SearchCoreAsync(IReadOnlyList<string> queries, Prepared prepared, Activity? activity, CancellationToken ct)
    {
        var vectors = await EmbedQueriesAsync(queries, ct).ConfigureAwait(false);
        var path = prepared.Path;
        while (true)
        {
            try
            {
                return await SearchPathAsync(queries, vectors, prepared, path, activity, ct).ConfigureAwait(false);
            }
            catch (NativeHybridUnsupportedException ex)
            {
                // A connector that implements hybrid search but cannot serve dynamic records: fall back for the rest of the process.
                await engine.SwitchFromNativeAsync(ex.InnerException!, ct).ConfigureAwait(false);
                path = engine.KeywordPath;
                if (path == KeywordPath.None && prepared.HybridRequired) throw new HybridSearchUnavailableException(UnavailableMessage);
            }
        }
    }

    private async Task<List<RetrievedChunk>> SearchPathAsync(
        IReadOnlyList<string> queries, List<ReadOnlyMemory<float>> vectors, Prepared prepared, KeywordPath path, Activity? activity, CancellationToken ct)
    {
        var options = engine.Options;
        var filter = prepared.Filter;
        var hybrid = path != KeywordPath.None;
        activity?.SetTag("rag.hybrid.path", path.ToString().ToLowerInvariant());
        if (hybrid && prepared.MinScore is not null && Interlocked.Exchange(ref _minScoreWarned, 1) == 0)
            engine.Logger.LogWarning("MinScore is ignored for hybrid results without a reranker, because fused scores are not similarities.");

        // Vector-only fetches TopK exactly as before hybrid search existed; hybrid fetches more from each path before fusion.
        var fetch = hybrid ? Math.Max(prepared.TopK, options.HybridCandidates) : prepared.TopK;
        var keywordCount = 0;
        var rankings = new List<IReadOnlyList<RetrievedChunk>>(queries.Count);
        for (var i = 0; i < queries.Count; i++)
        {
            switch (path)
            {
                case KeywordPath.Native:
                    rankings.Add(await NativeSearchAsync(queries[i], vectors[i], fetch, filter, ct).ConfigureAwait(false));
                    break;
                case KeywordPath.Index:
                    var vectorRanking = await VectorSearchAsync(vectors[i], fetch, filter, null, ct).ConfigureAwait(false);
                    var keywordRanking = await KeywordSearchAsync(queries[i], fetch, prepared, ct).ConfigureAwait(false);
                    keywordCount += keywordRanking.Count;
                    rankings.Add(RankFusion.Reciprocal([vectorRanking, keywordRanking], fetch));
                    break;
                default:
                    rankings.Add(await VectorSearchAsync(vectors[i], fetch, filter, prepared.MinScore, ct).ConfigureAwait(false));
                    break;
            }
        }

        if (path == KeywordPath.Index) activity?.SetTag("rag.keyword.count", keywordCount);

        // Text is fetched once, below, for the final results only.
        var results = queries.Count == 1 ? rankings[0].Take(prepared.TopK).ToList() : RankFusion.Reciprocal(rankings, prepared.TopK).ToList();

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

    /// <summary>One vector search, best first, with similarity scores; <paramref name="minScore"/> drops weaker results.</summary>
    private async Task<List<RetrievedChunk>> VectorSearchAsync(
        ReadOnlyMemory<float> vector, int top, Expression<Func<Dictionary<string, object?>, bool>>? filter, double? minScore, CancellationToken ct)
    {
        var ranking = new List<RetrievedChunk>();
        await foreach (var hit in engine.Chunks.SearchAsync(vector, top, filter, ct).ConfigureAwait(false))
        {
            var score = hit.Score ?? throw new InvalidOperationException("The vector store returned a result without a score.");
            if (engine.Options.DistanceFunction == DistanceFunction.CosineDistance) score = 1 - score;
            if (minScore is { } min && score < min) continue;
            ranking.Add(Map(hit.Record, score));
        }

        ranking.Sort(RankFusion.Compare);
        return ranking;
    }

    /// <summary>
    /// Searches the keyword index and resolves the hits to chunk records in one batch. Hits the store no longer has are
    /// dropped as drift; during a reindex, hits not yet on the configured model are dropped.
    /// </summary>
    private async Task<List<RetrievedChunk>> KeywordSearchAsync(string query, int top, Prepared prepared, CancellationToken ct)
    {
        var hits = await engine.KeywordIndex!.SearchAsync(query, prepared.KeywordFilter!, top, ct).ConfigureAwait(false);
        if (hits.Count == 0) return [];

        var records = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var r in await engine.Chunks.GetAsync(hits.Select(h => h.ChunkKey), includeVectors: false, ct).ConfigureAwait(false))
            records[Map(r, 0).ChunkKey] = r;

        var ranking = new List<RetrievedChunk>(hits.Count);
        var drift = 0;
        foreach (var hit in hits)
        {
            if (!records.TryGetValue(hit.ChunkKey, out var record))
            {
                drift++;
                if (Interlocked.Exchange(ref _driftWarned, 1) == 0)
                    engine.Logger.LogWarning(
                        "The keyword index returned chunk {ChunkKey}, which the vector store does not have; the index is out of step. Run IRagAdmin.RebuildKeywordIndexAsync to fix it.",
                        hit.ChunkKey);
                continue;
            }

            if (prepared.ReindexInProgress && !string.Equals(record.GetValueOrDefault("EmbeddingModelId") as string, engine.EmbeddingModelId, StringComparison.Ordinal))
                continue;
            ranking.Add(Map(record, hit.Score));
        }

        if (drift > 0) RagTelemetry.KeywordDrift.Add(drift);
        return ranking;
    }

    /// <summary>One call to the vector store's own hybrid search; its results are already fused.</summary>
    private async Task<IReadOnlyList<RetrievedChunk>> NativeSearchAsync(
        string query, ReadOnlyMemory<float> vector, int top, Expression<Func<Dictionary<string, object?>, bool>>? filter, CancellationToken ct)
    {
        var keywords = KeywordTokenizer.Terms(query).Distinct(StringComparer.Ordinal).ToList();
        // No keywords (only stop words or punctuation): the vector list alone, fused like any hybrid result.
        if (keywords.Count == 0) return RankFusion.Reciprocal([await VectorSearchAsync(vector, top, filter, null, ct).ConfigureAwait(false)], top);

        var ranking = new List<RetrievedChunk>();
        try
        {
            await foreach (var hit in engine.Chunks.HybridSearchAsync(vector, keywords, top, filter, ct).ConfigureAwait(false))
            {
                var score = hit.Score ?? throw new InvalidOperationException("The vector store returned a hybrid result without a score.");
                ranking.Add(Map(hit.Record, score) with { ScoreKind = ScoreKind.Fused });
            }
        }
        catch (NotSupportedException ex)
        {
            throw new NativeHybridUnsupportedException(ex);
        }

        ranking.Sort(RankFusion.Compare);
        return ranking;
    }

    /// <summary>Carries a NotSupportedException from the native hybrid call only, so other NotSupportedExceptions propagate.</summary>
    private sealed class NativeHybridUnsupportedException(NotSupportedException inner) : Exception(inner.Message, inner);

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
