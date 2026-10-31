using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagDotNet.Access;
using RagDotNet.Filtering;
using RagDotNet.Pipeline;
using RagDotNet.Storage;

namespace RagDotNet.Retrieval;

internal sealed class Retriever(RagEngine engine)
{
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, ResolvedScope scope, RetrievalOptions o, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(o);
        if (o.TopK is < 1 or > 100)
            throw new ArgumentOutOfRangeException("TopK", o.TopK, "TopK must be between 1 and 100.");

        var options = engine.Options;
        RagFilter? effective = (scope.Filter, o.Filter) switch
        {
            (null, var f) => f,
            (var s, null) => s,
            (var s, var f) => new RagFilter.AndFilter([s, f]),
        };
        var filter = RagFilterTranslator.Translate(effective, options.Metadata, out var matchesNothing);
        if (matchesNothing) return [];

        await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
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

        var partition = new Dictionary<string, string>();
        foreach (var k in options.PartitionKeys)
        {
            if (!r.TryGetValue(RagFilterTranslator.MetaPrefix + k, out var pv) || pv is not string ps0)
                throw new InvalidOperationException($"Retrieved record is missing partition value '{k}'; cannot build its chunk key.");
            partition[k] = ps0;
        }
        var key = ChunkKeys.Chunk(ChunkKeys.Partition(partition), documentId, chunkIndex);

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
