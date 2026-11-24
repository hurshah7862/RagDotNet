using RagDotNet.Access;
using RagDotNet.Documents;

namespace RagDotNet.Hosting;

/// <summary>A document waiting to be ingested in the background, with the access scope its caller had when it was enqueued.</summary>
/// <param name="JobId">The job's id, unique within its queue.</param>
/// <param name="Source">The document to ingest.</param>
/// <param name="Scope">The caller's scope at enqueue time; null means full access. The job runs under this scope, never an ambient one.</param>
public sealed record IngestionJob(string JobId, DocumentSource Source, AccessScope? Scope);
