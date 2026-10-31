using RagDotNet.Configuration;
using RagDotNet.Filtering;
using RagDotNet.Storage;

namespace RagDotNet.Access;

internal sealed record ResolvedScope(IReadOnlyDictionary<string, string> Partition, string PartitionKey, RagFilter? Filter, bool HasScopeFilter = false)
{
    public static ResolvedScope None { get; } = new(new Dictionary<string, string>(), "", null);
}

/// <remarks>Shared and thread-safe; holds only the unscoped-data check cache. The scope provider is passed per call.</remarks>
internal sealed class ScopeResolver(RagOptions options, ManifestStore manifests)
{
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private volatile bool _unscopedCheckPassed;

    public async ValueTask<ResolvedScope> ResolveAsync(IAccessScope? scope, CancellationToken ct)
    {
        if (scope is null) return ResolvedScope.None;

        var s = await scope.GetScopeAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The access scope returned null.");
        var resolved = Validate(s);
        await EnsureNoUnscopedDataAsync(ct).ConfigureAwait(false);
        return resolved;
    }

    private ResolvedScope Validate(AccessScope s)
    {
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

        var partitionKey = ChunkKeys.Partition(partition);
        var parts = partition.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => RagFilter.Eq(kv.Key, kv.Value))
            .ToList();
        if (s.Filter is not null) parts.Add(s.Filter);
        RagFilter filter = parts.Count == 1 ? parts[0] : new RagFilter.AndFilter(parts);
        return new ResolvedScope(partition, partitionKey, filter, s.Filter is not null);
    }

    private async Task EnsureNoUnscopedDataAsync(CancellationToken ct)
    {
        if (_unscopedCheckPassed) return;
        await _checkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_unscopedCheckPassed) return;
            await manifests.EnsureExistsAsync(ct).ConfigureAwait(false);
            if (await manifests.AnyAsync(r => (string)r["Partition"]! == "", ct).ConfigureAwait(false))
                throw new UnscopedDataException();
            _unscopedCheckPassed = true; // only success is cached
        }
        finally
        {
            _checkLock.Release();
        }
    }
}
