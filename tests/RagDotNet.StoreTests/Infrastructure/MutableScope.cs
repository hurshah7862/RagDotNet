using RagDotNet.Access;
using RagDotNet.Filtering;

namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>An access scope the test switches between calls; null means full access.</summary>
internal sealed class MutableScope : IAccessScope
{
    public AccessScope? Current { get; set; }

    public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken) => new(Current);

    public static AccessScope Tenant(string tenant, RagFilter? filter = null, RagPermissions permissions = RagPermissions.All) =>
        new(new Dictionary<string, string> { ["tenant"] = tenant }, filter, permissions);
}
