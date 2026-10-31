using RagDotNet.Documents;
using RagDotNet.Ingestion;

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
}
