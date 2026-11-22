namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>Decides whether a store's tests run, skip, or fail because the store could not start.</summary>
public static class StoreGate
{
    /// <summary>Set to 1 (CI) to make a store that cannot start fail its tests instead of skipping them.</summary>
    public const string RequiredVariable = "RAGDOTNET_STORES_REQUIRED";

    /// <summary>Set to 1 to run stores that need an external service such as Docker.</summary>
    public const string IntegrationVariable = "RAGDOTNET_INTEGRATION";

    public static bool Required => Environment.GetEnvironmentVariable(RequiredVariable) == "1";

    public static bool IntegrationEnabled => Environment.GetEnvironmentVariable(IntegrationVariable) == "1";

    /// <summary>Returns when the store is available; otherwise fails (required) or skips the test with the reason.</summary>
    public static void Check(string store, string? unavailableReason, bool required)
    {
        if (Decide(store, unavailableReason, required) is { } skip) Assert.Skip(skip);
    }

    /// <summary>
    /// Null when the store is available. When it is not: throws if the store is required, else returns the skip reason.
    /// (Kept apart from <see cref="Check"/> because xUnit records any Assert.Skip as a skip, even one a test catches.)
    /// </summary>
    public static string? Decide(string store, string? unavailableReason, bool required)
    {
        if (unavailableReason is null) return null;
        if (required)
            throw new InvalidOperationException(
                $"Store '{store}' is required in this run ({RequiredVariable}=1) but could not start: {unavailableReason}");
        return $"{store}: {unavailableReason}";
    }
}
