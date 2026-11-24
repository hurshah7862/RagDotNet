using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using RagDotNet.StoreTests.Infrastructure;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Qdrant;
using Testcontainers.Redis;

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
/// <param name="prepare">Turns the requested image into the one to run, such as by building on it; by default, the image itself.</param>
internal sealed class SharedContainer<TContainer>(
    string store, string image, Func<string, TContainer> build, Func<TContainer, Task<string>> ready, Func<string, Task<string>>? prepare = null)
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
            var runImage = prepare is null ? fromImage : await prepare(fromImage).ConfigureAwait(false);
            container = build(runImage);
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

    public static readonly SharedContainer<RedisContainer> Redis = new(
        "Redis",
        "redis/redis-stack-server:7.4.0-v8",
        image => new RedisBuilder(image).Build(),
        container => Task.FromResult(container.GetConnectionString()));

    public static readonly SharedContainer<MsSqlContainer> SqlServer = new(
        "SqlServer",
        "mcr.microsoft.com/mssql/server:2025-CU11-ubuntu-24.04",
        image => new MsSqlBuilder(image).Build(),
        async container =>
        {
            // The container's connection string points at master, where full-text indexes are not allowed.
            var created = await container.ExecScriptAsync("CREATE DATABASE rag;").ConfigureAwait(false);
            if (created.ExitCode != 0)
                throw new InvalidOperationException($"CREATE DATABASE rag failed: {created.Stderr}");
            return new SqlConnectionStringBuilder(container.GetConnectionString()) { InitialCatalog = "rag" }.ConnectionString;
        },
        WithFullTextSearchAsync);

    /// <summary>Builds Infrastructure/SqlServer/Dockerfile on <paramref name="baseImage"/>: SQL Server with Full-Text Search.</summary>
    private static async Task<string> WithFullTextSearchAsync(string baseImage)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "SqlServer"))
            .WithDockerfile("Dockerfile")
            .WithBuildArgument("BASE_IMAGE", baseImage)
            .WithName("ragdotnet-mssql-fts:2025-cu11")
            .WithCleanUp(false) // keep the built image, so a later run reuses Docker's layer cache instead of rebuilding
            .Build();
        await image.CreateAsync().ConfigureAwait(false);
        return image.FullName;
    }
}

/// <summary>Stops every started container at the end of the run. (Testcontainers' reaper covers a crash.)</summary>
public sealed class ContainerLifetime : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Containers.Postgres.DisposeAsync().ConfigureAwait(false);
        await Containers.Qdrant.DisposeAsync().ConfigureAwait(false);
        await Containers.Redis.DisposeAsync().ConfigureAwait(false);
        await Containers.SqlServer.DisposeAsync().ConfigureAwait(false);
    }
}
