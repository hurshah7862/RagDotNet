using RagDotNet.StoreTests.Infrastructure;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(PostgresContainerLifetime))]

namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>
/// One pgvector container per test run, shared by every PgVector binding. It starts on first use, and never when
/// <see cref="StoreGate.IntegrationVariable"/> is unset, so an in-memory or SQLite run stays Docker-free.
/// </summary>
internal static class PostgresContainer
{
    public const string Image = "pgvector/pgvector:0.8.7-pg17";

    private static readonly Lazy<Task<(string? ConnectionString, string? UnavailableReason)>> Shared = new(() => StartAsync(Image, share: true));
    private static PostgreSqlContainer? _shared;
    private static int _starts;

    /// <summary>How many shared containers this run started (for tests).</summary>
    internal static int Starts => Volatile.Read(ref _starts);

    /// <summary>The shared container's connection string, or the reason it is not available.</summary>
    public static Task<(string? ConnectionString, string? UnavailableReason)> GetAsync() =>
        StoreGate.IntegrationEnabled
            ? Shared.Value
            : Task.FromResult<(string?, string?)>((null, $"{StoreGate.IntegrationVariable} is not set; set it to 1 to run stores that need Docker."));

    /// <summary>Starts a container from <paramref name="image"/>; any failure becomes the reason, never an exception.</summary>
    internal static async Task<(string? ConnectionString, string? UnavailableReason)> StartAsync(string image, bool share = false)
    {
        PostgreSqlContainer? container = null;
        try
        {
            container = new PostgreSqlBuilder(image).Build();
            await container.StartAsync().ConfigureAwait(false);
            var created = await container.ExecScriptAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
            if (created.ExitCode != 0)
                throw new InvalidOperationException($"CREATE EXTENSION vector failed: {created.Stderr}");

            if (share)
            {
                _shared = container;
                Interlocked.Increment(ref _starts);
            }
            else
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }

            return (container.GetConnectionString(), null);
        }
#pragma warning disable CA1031 // Any failure to start means the store is unavailable; the gate reports it.
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (container is not null) await container.DisposeAsync().ConfigureAwait(false);
            return (null, $"Docker is not available or the {image} container could not start: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static async ValueTask StopAsync()
    {
        if (_shared is { } container) await container.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Stops the shared container at the end of the run, if one was started. (Testcontainers' reaper covers a crash.)</summary>
public sealed class PostgresContainerLifetime : IAsyncDisposable
{
    public ValueTask DisposeAsync() => PostgresContainer.StopAsync();
}
