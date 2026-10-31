using RagDotNet.Filtering;

namespace RagDotNet.Access;

/// <summary>A caller's access scope.</summary>
/// <param name="Partition">Partition values; the keys must match <c>RagOptions.PartitionKeys</c> exactly. Partition values override caller metadata and are ANDed into every read.</param>
/// <param name="Filter">Optional extra read filter, ANDed with every retrieval filter.</param>
public sealed record AccessScope(IReadOnlyDictionary<string, string> Partition, RagFilter? Filter = null);
