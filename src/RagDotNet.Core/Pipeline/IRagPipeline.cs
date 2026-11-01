// The conversation overloads differ from the string overloads in the type of their first argument, so a call is never ambiguous.
#pragma warning disable RS0026 // Do not add multiple overloads with optional parameters
using Microsoft.Extensions.AI;
using RagDotNet.Access;
using RagDotNet.Documents;
using RagDotNet.Embedding;
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
    /// <exception cref="ArgumentException">
    /// Metadata is undeclared or has the wrong type; the chunker named in <paramref name="options"/> is not registered (thrown
    /// before anything is written); or, with full access, the document's metadata sets only some partition keys, or an empty
    /// or non-string partition value.
    /// </exception>
    /// <exception cref="UnsupportedDocumentException">No reader supports the document.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant Write, the document's metadata is outside the scope's read filter, or a document with this id exists and is not visible to the caller.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<IngestResult> IngestAsync(DocumentSource source, IngestOptions? options = null, IProgress<IngestProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ingests several documents with bounded parallelism. Results are in input order. The batch is not atomic: when one
    /// document fails, documents already ingested stay written.
    /// </summary>
    /// <param name="sources">The documents; ids must be unique.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="progress">Progress receiver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Two sources share a document id, or one document fails a check of <see cref="IngestAsync"/>.</exception>
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
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<bool> DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default);

    /// <summary>Finds the chunks most similar to a query.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks ordered by descending score.</returns>
    /// <exception cref="ArgumentException">The query is empty or whitespace, or the filter is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RetrievalOptions.TopK"/> is outside 1 to 100.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the chunks most similar to the last question of a conversation. When the conversation has history (limited to
    /// <see cref="Configuration.RagOptions.MaxHistoryMessages"/>), the question is first rewritten as a standalone question with the chat client.
    /// </summary>
    /// <param name="conversation">The conversation; the last message must be a user message with text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Chunks ordered by descending score.</returns>
    /// <exception cref="ArgumentException">The conversation is null, empty, or does not end with a user message that has text; or the filter is invalid.</exception>
    /// <exception cref="InvalidOperationException">The conversation has history and no chat client is configured. Call <c>UseChatClient</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="RetrievalOptions.TopK"/> is outside 1 to 100.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(IList<ChatMessage> conversation, RetrievalOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question from the ingested documents.</summary>
    /// <param name="question">The question.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; <see cref="RagAnswer.IsAnswered"/> is false, without a model call, when nothing was retrieved.</returns>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<RagAnswer> AskAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the last question of a conversation. History (limited to <see cref="Configuration.RagOptions.MaxHistoryMessages"/>)
    /// is used to rewrite the question for retrieval and is sent to the model before the documents; it does not count against the context token budget.
    /// </summary>
    /// <param name="conversation">The conversation; the last message must be a user message with text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer; <see cref="RagAnswer.IsAnswered"/> is false, without an answer call, when nothing was retrieved.</returns>
    /// <exception cref="ArgumentException">The conversation is null, empty, or does not end with a user message that has text.</exception>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    Task<RagAnswer> AskAsync(IList<ChatMessage> conversation, AskOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question and streams the answer. Yields one update per non-empty text delta, then a final update with the complete answer. When nothing is retrieved, yields only the final update.</summary>
    /// <param name="question">The question.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updates.</returns>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    IAsyncEnumerable<RagAnswerUpdate> AskStreamingAsync(string question, AskOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Answers the last question of a conversation and streams the answer. See <see cref="AskAsync(IList{ChatMessage}, AskOptions?, CancellationToken)"/>.</summary>
    /// <param name="conversation">The conversation; the last message must be a user message with text.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updates.</returns>
    /// <exception cref="ArgumentException">The conversation is null, empty, or does not end with a user message that has text.</exception>
    /// <exception cref="InvalidOperationException">No chat client is configured. Call <c>UseChatClient</c>.</exception>
    /// <exception cref="RagAccessDeniedException">The access scope does not grant <see cref="RagPermissions.Read"/>.</exception>
    /// <exception cref="EmbeddingModelMismatchException">The store holds data from another embedding model, or a reindex to another model is in progress. Run <see cref="IRagAdmin.ReindexAsync"/>.</exception>
    IAsyncEnumerable<RagAnswerUpdate> AskStreamingAsync(IList<ChatMessage> conversation, AskOptions? options = null, CancellationToken cancellationToken = default);
}
