using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Generation;
using RagDotNet.Ingestion;
using RagDotNet.Retrieval;

namespace RagDotNet.Pipeline;

/// <summary>Ingests documents into the vector store.</summary>
public interface IRagPipeline
{
    /// <summary>
    /// Ingests one document. Unchanged documents are not re-embedded.
    /// With an access scope, the scope must grant <see cref="RagPermissions.Write"/>, the document's metadata must match the
    /// scope's read filter, and an existing document with the same id in the caller's area must also match it: a caller
    /// cannot create or change what it could not read. With partition keys configured, a tenant never changes a document
    /// in the common area; ingesting its id creates a separate document in the tenant's own area.
    /// </summary>
    /// <param name="source">The document.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Metadata is undeclared or has the wrong type.</exception>
    /// <exception cref="UnsupportedDocumentException">No reader supports the document.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant Write, the document's metadata is outside the scope's read filter, or a document with this id exists and is not visible to the caller.</exception>
    Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Ingests several documents with bounded parallelism. Results are in input order.</summary>
    /// <param name="sources">The documents; ids must be unique.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Two sources share a document id.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope refuses one of the documents; see <see cref="IngestAsync"/>.</exception>
    Task<IReadOnlyList<IngestResult>> IngestManyAsync(IEnumerable<DocumentSource> sources, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document's chunks and manifest. With an access scope, the scope must grant <see cref="RagPermissions.Delete"/>,
    /// only a document in the caller's own area can be deleted (a tenant cannot delete a common-area document), and the
    /// document's stored metadata must match the scope's read filter. A document that exists but is not visible to the caller is treated
    /// exactly like a missing one.
    /// </summary>
    /// <param name="documentId">The document id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the document existed and was visible to the caller; false when it is missing or not visible.</returns>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant Delete.</exception>
    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Finds the chunks most similar to a query.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks ordered by descending score.</returns>
    /// <exception cref="ArgumentException">The query is empty or whitespace, or the filter is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RetrievalOptions.TopK"/> is outside 1 to 100.</exception>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question from the ingested documents.</summary>
    /// <param name="question">The question.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; <see cref="RagAnswer.IsAnswered"/> is false, without a model call, when nothing was retrieved.</returns>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    Task<RagAnswer> AskAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question and streams the answer. Yields one update per non-empty text delta, then a final update with the complete answer. When nothing is retrieved, yields only the final update.</summary>
    /// <param name="question">The question.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updates.</returns>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    IAsyncEnumerable<RagAnswerUpdate> AskStreamingAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default);
}
