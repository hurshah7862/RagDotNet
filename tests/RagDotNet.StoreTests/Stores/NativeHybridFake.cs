using Microsoft.Extensions.VectorData;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>
/// Test-only: the in-memory store with a native hybrid search bolted on (the Core tests' HybridVectorStore). Not a
/// supported store; it keeps the suite honest about the native hybrid path until SQLite and Qdrant are bound.
/// </summary>
public sealed class NativeHybridFakeFixture : StoreFixture
{
    public override string Name => "NativeHybridFake";

    public override bool NativeHybrid => true;

    public override VectorStore CreateStore() => new HybridVectorStore();
}

public sealed class NativeHybridFakeTests(NativeHybridFakeFixture fixture) : StoreSuite<NativeHybridFakeFixture>(fixture, RagKeyType.String);
