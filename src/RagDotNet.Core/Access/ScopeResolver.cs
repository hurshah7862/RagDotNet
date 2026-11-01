using RagDotNet.Configuration;
using RagDotNet.Filtering;
using RagDotNet.Storage;

namespace RagDotNet.Access;

internal sealed record ResolvedScope(
    IReadOnlyDictionary<string, string> Partition, string PartitionKey, RagFilter? UserFilter, RagPermissions Permissions, bool IsFullAccess)
{
    public static ResolvedScope FullAccess { get; } =
        new(new Dictionary<string, string>(), "", null, RagPermissions.All, true);

    /// <summary>Partition equality ANDed with the scope's own filter; null when neither applies. Full access has no partition condition.</summary>
    public RagFilter? PartitionAndUserFilter()
    {
        var parts = new List<RagFilter>();
        if (!IsFullAccess)
            parts.AddRange(Partition.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => RagFilter.Eq(kv.Key, kv.Value)));
        if (UserFilter is not null) parts.Add(UserFilter);
        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new RagFilter.AndFilter(parts),
        };
    }
}

/// <remarks>Shared and thread-safe; stateless. The scope provider is passed per call.</remarks>
internal sealed class ScopeResolver(RagOptions options)
{
    public async ValueTask<ResolvedScope> ResolveAsync(IAccessScope? scope, CancellationToken ct)
    {
        if (scope is null) return ResolvedScope.FullAccess;

        var s = await scope.GetScopeAsync(ct).ConfigureAwait(false);
        return s is null ? ResolvedScope.FullAccess : Validate(s);
    }

    private ResolvedScope Validate(AccessScope s)
    {
        if (options.PartitionKeys.Count == 0)
        {
            if (s.Partition is { Count: > 0 })
                throw new InvalidOperationException(
                    "The access scope sets partition values, but no PartitionKeys are configured. Use AccessScope.Unpartitioned for a single-tenant app.");
            return new ResolvedScope(new Dictionary<string, string>(), "", s.Filter, s.Permissions, false);
        }

        if (s.Partition is null || s.Partition.Count == 0)
            throw new InvalidOperationException("The access scope has an empty partition. A scope must set a value for every partition key.");

        var expected = new HashSet<string>(options.PartitionKeys, StringComparer.Ordinal);
        var actual = new HashSet<string>(s.Partition.Keys, StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new InvalidOperationException(
                $"The access scope partition keys [{string.Join(", ", actual.OrderBy(k => k, StringComparer.Ordinal))}] do not match the configured partition keys [{string.Join(", ", expected.OrderBy(k => k, StringComparer.Ordinal))}].");

        var partition = new Dictionary<string, string>(s.Partition, StringComparer.Ordinal);
        foreach (var (k, v) in partition)
        {
            if (string.IsNullOrEmpty(v))
                throw new InvalidOperationException($"The access scope partition value for '{k}' must not be null or empty.");
            if (!options.Metadata.Fields.TryGetValue(k, out var type) || type != typeof(string))
                throw new InvalidOperationException($"Partition key '{k}' must be declared as a string metadata field.");
        }

        return new ResolvedScope(partition, ChunkKeys.Partition(partition), s.Filter, s.Permissions, false);
    }
}
