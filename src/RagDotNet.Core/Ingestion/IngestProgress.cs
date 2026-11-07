namespace RagDotNet.Ingestion;

/// <summary>The stage an ingestion is in.</summary>
public enum IngestStage
{
    /// <summary>Reading and parsing the document.</summary>
    Reading,

    /// <summary>Splitting the document into chunks.</summary>
    Chunking,

    /// <summary>Embedding chunks.</summary>
    Embedding,

    /// <summary>Writing chunks and the manifest.</summary>
    Storing,

    /// <summary>Done.</summary>
    Completed,
}

/// <summary>A progress report for one document.</summary>
/// <param name="DocumentId">The document id.</param>
/// <param name="Stage">Current stage.</param>
/// <param name="ChunksEmbedded">
/// Chunks embedded so far. During <see cref="IngestStage.Chunking"/> with the semantic chunker, the sentence-level units
/// embedded so far (sentences, whole tables and code blocks, and token-split pieces of longer units). The count starts
/// again from 0 when the <see cref="IngestStage.Embedding"/> stage begins.
/// </param>
/// <param name="TotalChunks">
/// Total chunks, once known (otherwise 0). During <see cref="IngestStage.Chunking"/> with the semantic chunker, the total
/// sentence-level units to embed.
/// </param>
public sealed record IngestProgress(string DocumentId, IngestStage Stage, int ChunksEmbedded, int TotalChunks);
