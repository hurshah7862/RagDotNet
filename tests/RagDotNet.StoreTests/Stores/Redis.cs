using CommunityToolkit.VectorData.Redis;
using Microsoft.Extensions.VectorData;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;
using StackExchange.Redis;

namespace RagDotNet.StoreTests.Stores;

/// <summary>Redis Stack through CommunityToolkit.VectorData.Redis (JSON storage, the default), on the run's shared container.</summary>
public sealed class RedisFixture : StoreFixture
{
    private ConnectionMultiplexer? _redis;

    public override string Name => "Redis";

    // RedisCollection has no native hybrid search: the in-memory keyword index serves it.

    public override async ValueTask InitializeAsync()
    {
        (var endpoint, UnavailableReason) = await Containers.Redis.GetAsync();
        if (endpoint is not null) _redis = await ConnectionMultiplexer.ConnectAsync(endpoint);
    }

    public override VectorStore CreateStore() =>
        new RedisVectorStore((_redis ?? throw new InvalidOperationException("The Redis container is not available.")).GetDatabase());

    public override async ValueTask DisposeAsync()
    {
        if (_redis is not null) await _redis.DisposeAsync();
        await base.DisposeAsync();
    }
}

public sealed class RedisStringKeyTests(RedisFixture fixture) : StoreSuite<RedisFixture>(fixture, RagKeyType.String);

public sealed class RedisGuidKeyTests(RedisFixture fixture) : StoreSuite<RedisFixture>(fixture, RagKeyType.Guid);
