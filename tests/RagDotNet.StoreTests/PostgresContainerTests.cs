using Npgsql;
using RagDotNet.StoreTests.Infrastructure;

namespace RagDotNet.StoreTests;

public sealed class PostgresContainerTests
{
    [Fact]
    public async Task Integration_off_starts_nothing()
    {
        if (StoreGate.IntegrationEnabled) Assert.Skip($"Checks the run without {StoreGate.IntegrationVariable}.");

        var (connectionString, reason) = await PostgresContainer.GetAsync();

        Assert.Null(connectionString);
        Assert.Contains(StoreGate.IntegrationVariable, reason);
        Assert.Equal(0, PostgresContainer.Starts);
    }

    [Fact]
    public async Task Unstartable_container_names_the_cause()
    {
        if (!StoreGate.IntegrationEnabled) Assert.Skip($"Needs Docker: set {StoreGate.IntegrationVariable}=1.");

        var (connectionString, reason) = await PostgresContainer.StartAsync("ragdotnet-test/does-not-exist:0");

        Assert.Null(connectionString);
        Assert.Contains("Docker", reason);
        Assert.Contains("does-not-exist", reason);
    }

    [Fact]
    public async Task Bindings_share_one_container()
    {
        if (!StoreGate.IntegrationEnabled) Assert.Skip($"Needs Docker: set {StoreGate.IntegrationVariable}=1.");

        var first = await PostgresContainer.GetAsync();
        var second = await PostgresContainer.GetAsync();

        Assert.Null(first.UnavailableReason);
        Assert.Equal(first.ConnectionString, second.ConnectionString);
        Assert.Equal(1, PostgresContainer.Starts);
        await using var connection = new NpgsqlConnection(first.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", connection);
        Assert.Equal("0.8.7", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
