using Npgsql;
using Qdrant.Client;
using StackExchange.Redis;
using RagDotNet.StoreTests.Infrastructure;

namespace RagDotNet.StoreTests;

public sealed class SharedContainerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Stores => new("PgVector", "Qdrant", "Redis");

    private static (Func<Task<(string? Endpoint, string? UnavailableReason)>> Get, Func<string, Task<(string? Endpoint, string? UnavailableReason)>> Start, Func<int> Starts) Container(string store) =>
        store switch
        {
            "PgVector" => (Containers.Postgres.GetAsync, image => Containers.Postgres.StartAsync(image), () => Containers.Postgres.Starts),
            "Qdrant" => (Containers.Qdrant.GetAsync, image => Containers.Qdrant.StartAsync(image), () => Containers.Qdrant.Starts),
            "Redis" => (Containers.Redis.GetAsync, image => Containers.Redis.StartAsync(image), () => Containers.Redis.Starts),
            _ => throw new ArgumentOutOfRangeException(nameof(store)),
        };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Integration_off_starts_nothing(string store)
    {
        if (StoreGate.IntegrationEnabled) Assert.Skip($"Checks the run without {StoreGate.IntegrationVariable}.");
        var container = Container(store);

        var (endpoint, reason) = await container.Get();

        Assert.Null(endpoint);
        Assert.Contains(StoreGate.IntegrationVariable, reason);
        Assert.Equal(0, container.Starts());
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Unstartable_container_names_the_cause(string store)
    {
        if (!StoreGate.IntegrationEnabled) Assert.Skip($"Needs Docker: set {StoreGate.IntegrationVariable}=1.");

        var (endpoint, reason) = await Container(store).Start("ragdotnet-test/does-not-exist:0");

        Assert.Null(endpoint);
        Assert.Contains("Docker", reason);
        Assert.Contains("does-not-exist", reason);
        Assert.Contains(store, reason);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Bindings_share_one_container(string store)
    {
        if (!StoreGate.IntegrationEnabled) Assert.Skip($"Needs Docker: set {StoreGate.IntegrationVariable}=1.");
        var container = Container(store);

        var first = await container.Get();
        StoreGate.Check(store, first.UnavailableReason, StoreGate.Required); // Docker down: skip locally, fail when required
        var second = await container.Get();

        Assert.Equal(first.Endpoint, second.Endpoint);
        Assert.Equal(1, container.Starts());
        var version = await ServiceVersionAsync(store, first.Endpoint!);
        if (store == "Redis") Assert.StartsWith("7.4", version);
        else Assert.Equal(store == "PgVector" ? "0.8.7" : "1.20.1", version);
    }

    private static async Task<string?> ServiceVersionAsync(string store, string endpoint)
    {
        if (store == "Redis")
        {
            await using var redis = await ConnectionMultiplexer.ConnectAsync(endpoint);
            return redis.GetServer(redis.GetEndPoints()[0]).Version.ToString();
        }

        if (store == "Qdrant")
        {
            using var client = new QdrantClient(new Uri(endpoint));
            return (await client.HealthAsync(Ct)).Version;
        }

        await using var connection = new NpgsqlConnection(endpoint);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", connection);
        return (string?)await command.ExecuteScalarAsync(Ct);
    }
}
