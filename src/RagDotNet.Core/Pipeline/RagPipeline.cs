using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Pipeline;

internal sealed class RagPipeline(RagEngine engine, IAccessScope? accessScope = null) : IRagPipeline
{
    internal RagEngine Engine => engine;

    public async Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Ingestor.IngestAsync(source, scope, options, progress, cancellationToken).ConfigureAwait(false);
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

        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        var results = new IngestResult[list.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, engine.Options.IngestParallelism), CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await engine.Ingestor.IngestAsync(list[i], scope, options, progress, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    public async Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Ingestor.DeleteAsync(documentId, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var scope = await engine.ResolveScopeAsync(accessScope, cancellationToken).ConfigureAwait(false);
        return await engine.Retriever.RetrieveAsync(query, scope, options ?? new RetrievalOptions(), cancellationToken).ConfigureAwait(false);
    }
}
