namespace RagDotNet.Pipeline;

/// <summary>Document and chunk counts for one partition.</summary>
/// <param name="Partition">The partition values; empty for the common area (and for an unpartitioned collection).</param>
/// <param name="DocumentCount">Number of documents.</param>
/// <param name="ChunkCount">Number of chunks.</param>
public sealed record PartitionStats(IReadOnlyDictionary<string, string> Partition, int DocumentCount, int ChunkCount);

/// <summary>Store-wide statistics.</summary>
/// <param name="Partitions">Per-partition statistics.</param>
/// <param name="EmbeddingModelIds">The distinct embedding model ids found in the store.</param>
public sealed record RagStats(IReadOnlyList<PartitionStats> Partitions, IReadOnlyList<string> EmbeddingModelIds);

/// <summary>Outcome of a reindex.</summary>
/// <param name="DocumentsReindexed">Documents re-embedded.</param>
/// <param name="DocumentsSkipped">Documents already on the current model.</param>
/// <param name="ChunksReindexed">Chunks re-embedded.</param>
public sealed record ReindexResult(int DocumentsReindexed, int DocumentsSkipped, int ChunksReindexed);

/// <summary>Progress of a reindex.</summary>
/// <param name="DocumentsDone">Documents processed so far, including skipped ones.</param>
/// <param name="CurrentDocumentId">The document just processed.</param>
public sealed record ReindexProgress(int DocumentsDone, string? CurrentDocumentId);
