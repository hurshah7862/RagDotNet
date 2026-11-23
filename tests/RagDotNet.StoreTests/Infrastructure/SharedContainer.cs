using DotNet.Testcontainers.Containers;
using RagDotNet.StoreTests.Infrastructure;
using Testcontainers.PostgreSql;
using Testcontainers.Qdrant;

[assembly: AssemblyFixture(typeof(ContainerLifetime))]

namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>
/// One container per store per test run, shared by every binding of that store. It starts on first use, and never when
/// <see cref="StoreGate.IntegrationVariable"/> is unset, so an in-memory or SQLite run stays Docker-free. Any failure to
/// start becomes the reason the store is unavailable, never an exception.
/// </summary>
/// <param name="store">The store's name, for messages.</param>
/// <param name="image">The pinned image.</param>
/// <param name="build">Builds (does not start) a container from an image.</param>
/// <param name="ready">Runs after start: prepares the service and returns the endpoint the store connects to.</param>
internal sealed class SharedContainer<TContainer>(string store, string image, Func<string, TContainer> build, Func<TContainer, Task<string>> ready)
    : IAsyncDisposable
    where TContainer : IContainer
{
    private readonly object _gate = new();
    private Task<(string? Endpoint, string? UnavailableReason)>? _shared;
    private TContainer? _container;
    private int _starts;

    public string Store => store;

    public string Image => image;

    /// <summary>How many shared containers this run started (for tests).</summary>
    internal int Starts => Volatile.Read(ref _starts);

    /// <summary>The shared container's endpoint, or the reason it is not available.</summary>
    public Task<(string? Endpoint, string? UnavailableReason)> GetAsync() =>
        StoreGate.IntegrationEnabled
            ? Shared()
            : Task.FromResult<(string?, string?)>((null, $"{StoreGate.IntegrationVariable} is not set; set it to 1 to run stores that need Docker."));

    private Task<(string? Endpoint, string? UnavailableReason)> Shared()
    {
        lock (_gate) return _shared ??= StartAsync(image, share: true);
    }

    /// <summary>Starts a container from <paramref name="fromImage"/>; an unshared one is stopped again before returning.</summary>
    internal async Task<(string? Endpoint, string? UnavailableReason)> StartAsync(string fromImage, bool share = false)
    {
        TContainer? container = default;
        try
        {
            container = build(fromImage);
            await container.StartAsync().ConfigureAwait(false);
            var endpoint = await ready(container).ConfigureAwait(false);

            if (share)
            {
                _container = container;
                Interlocked.Increment(ref _starts);
            }
            else
            {
                await container.DisposeAsync().ConfigureAwait(false);
            }

            return (endpoint, null);
        }
#pragma warning disable CA1031 // Any failure to start means the store is unavailable; the gate reports it.
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (container is not null) await container.DisposeAsync().ConfigureAwait(false);
            return (null, $"Docker is not available or the {fromImage} container for {store} could not start: {e.GetType().Name}: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is { } container) await container.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The run's containers, one per store that needs Docker.</summary>
internal static class Containers
{
    public static readonly SharedContainer<PostgreSqlContainer> Postgres = new(
        "PgVector",
        "pgvector/pgvector:0.8.7-pg17",
        image => new PostgreSqlBuilder(image).Build(),
        async container =>
        {
            var created = await container.ExecScriptAsync("CREATE EXTENSION IF NOT EXISTS vector;").ConfigureAwait(false);
            if (created.ExitCode != 0)
                throw new InvalidOperationException($"CREATE EXTENSION vector failed: {created.Stderr}");
            return container.GetConnectionString();
        });

    public static readonly SharedContainer<QdrantContainer> Qdrant = new(
        "Qdrant",
        "qdrant/qdrant:v1.20.1",
        image => new QdrantBuilder(image).Build(),
        container => Task.FromResult(container.GetGrpcConnectionString()));
}

/// <summary>Stops every started container at the end of the run. (Testcontainers' reaper covers a crash.)</summary>
public sealed class ContainerLifetime : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Containers.Postgres.DisposeAsync().ConfigureAwait(false);
        await Containers.Qdrant.DisposeAsync().ConfigureAwait(false);
    }
}
