using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Pipeline;

internal sealed class RagPipeline(RagEngine engine) : IRagPipeline
{
    internal RagEngine Engine => engine;

    public Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return engine.Ingestor.IngestAsync(source, ResolvedScope.None, options, progress, cancellationToken);
    }

    public async Task<IReadOnlyList<IngestResult>> IngestManyAsync(IEnumerable<DocumentSource> sources, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (!seen.Add(s.DocumentId))
                throw new ArgumentException($"Duplicate document id '{s.DocumentId}'.", nameof(sources));
        }

        var results = new IngestResult[list.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, engine.Options.IngestParallelism), CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await engine.Ingestor.IngestAsync(list[i], ResolvedScope.None, options, progress, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    public Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return engine.Ingestor.DeleteAsync(documentId, ResolvedScope.None, cancellationToken);
    }

    public Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return engine.Retriever.RetrieveAsync(query, ResolvedScope.None, options ?? new RetrievalOptions(), cancellationToken);
    }
}
