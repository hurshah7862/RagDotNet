using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Pipeline;

/// <summary>Ingests documents into the vector store.</summary>
public interface IRagPipeline
{
    /// <summary>Ingests one document. Unchanged documents are not re-embedded.</summary>
    /// <param name="source">The document.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Metadata is undeclared or has the wrong type.</exception>
    /// <exception cref="UnsupportedDocumentException">No reader supports the document.</exception>
    Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Ingests several documents with bounded parallelism. Results are in input order.</summary>
    /// <param name="sources">The documents; ids must be unique.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Two sources share a document id.</exception>
    Task<IReadOnlyList<IngestResult>> IngestManyAsync(IEnumerable<DocumentSource> sources, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a document's chunks and manifest.</summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the document existed.</returns>
    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Finds the chunks most similar to a query.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks ordered by descending score.</returns>
    /// <exception cref="ArgumentException">The query is empty or whitespace, or the filter is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RetrievalOptions.TopK"/> is outside 1 to 100.</exception>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);
}
