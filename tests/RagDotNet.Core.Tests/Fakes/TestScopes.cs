using RagDotNet.Access;
using RagDotNet.Filtering;

namespace RagDotNet.Core.Tests.Fakes;

internal sealed class MutableScope : IAccessScope
{
    public AccessScope? Current { get; set; }

    public int Calls { get; private set; }

    public ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return new(Current);
    }
}

internal static class TestScopes
{
    public static AccessScope Tenant(string t, RagFilter? filter = null, RagPermissions permissions = RagPermissions.All) =>
        new(new Dictionary<string, string> { ["tenant"] = t }, filter, permissions);
}
