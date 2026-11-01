using RagDotNet.Filtering;

namespace RagDotNet.Access;

/// <summary>A caller's access scope.</summary>
/// <param name="Partition">Partition values; the keys must match <c>RagOptions.PartitionKeys</c> exactly (empty for a single-tenant app). Partition values override caller metadata and are ANDed into every read.</param>
/// <param name="Filter">Optional extra read filter, ANDed with every retrieval filter.</param>
/// <param name="Permissions">The operations the scope allows. Defaults to all.</param>
public sealed record AccessScope(IReadOnlyDictionary<string, string> Partition, RagFilter? Filter = null, RagPermissions Permissions = RagPermissions.All)
{
    /// <summary>Creates a scope with no partition values, for single-tenant apps.</summary>
    /// <param name="filter">Optional extra read filter.</param>
    /// <param name="permissions">The operations the scope allows.</param>
    /// <returns>A scope with an empty partition.</returns>
    public static AccessScope Unpartitioned(RagFilter? filter = null, RagPermissions permissions = RagPermissions.All) =>
        new(new Dictionary<string, string>(), filter, permissions);
}
