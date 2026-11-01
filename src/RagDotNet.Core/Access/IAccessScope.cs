namespace RagDotNet.Access;

/// <summary>Supplies the access scope (tenant partition, read filter and permissions) for the current caller.</summary>
public interface IAccessScope
{
    /// <summary>Gets the scope. The pipeline calls this once per public call.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scope for the current caller, or <see langword="null"/> for full access.</returns>
    ValueTask<AccessScope?> GetScopeAsync(CancellationToken cancellationToken);
}
