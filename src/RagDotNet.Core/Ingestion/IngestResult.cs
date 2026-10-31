using RagDotNet.Documents;

namespace RagDotNet.Ingestion;

/// <summary>What happened to a document during ingestion.</summary>
public enum IngestStatus
{
    /// <summary>The document was new.</summary>
    Created,

    /// <summary>The document existed and its content or chunking changed.</summary>
    Updated,

    /// <summary>The document existed and nothing changed; nothing was embedded or written.</summary>
    Unchanged,
}

/// <summary>The outcome of ingesting one document.</summary>
/// <param name="DocumentId">The document id.</param>
/// <param name="Status">What happened.</param>
/// <param name="ChunkCount">Number of chunks the document now has.</param>
/// <param name="Warnings">Non-fatal problems found while ingesting.</param>
public sealed record IngestResult(string DocumentId, IngestStatus Status, int ChunkCount, IReadOnlyList<IngestWarning> Warnings);
