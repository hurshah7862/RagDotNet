using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.VectorData;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>The in-memory store: the reference every other store must match.</summary>
public sealed class InMemoryFixture : StoreFixture
{
    public override string Name => "InMemory";

    public override VectorStore CreateStore() => new InMemoryVectorStore();
}

public sealed class InMemoryStringKeyTests(InMemoryFixture fixture) : StoreSuite<InMemoryFixture>(fixture, RagKeyType.String);

public sealed class InMemoryGuidKeyTests(InMemoryFixture fixture) : StoreSuite<InMemoryFixture>(fixture, RagKeyType.Guid);
