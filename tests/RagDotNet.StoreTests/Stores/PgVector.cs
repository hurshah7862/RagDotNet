using CommunityToolkit.VectorData.PgVector;
using Microsoft.Extensions.VectorData;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>PostgreSQL with pgvector, through CommunityToolkit.VectorData.PgVector, on the run's shared container.</summary>
public sealed class PgVectorFixture : StoreFixture
{
    private string? _connectionString;

    public override string Name => "PgVector";

    // PostgresCollection implements IKeywordHybridSearchable (PostgreSQL full-text search).
    public override bool NativeHybrid => true;

    public override async ValueTask InitializeAsync()
    {
        (_connectionString, UnavailableReason) = await Containers.Postgres.GetAsync();
    }

    public override VectorStore CreateStore() =>
        new PostgresVectorStore(_connectionString ?? throw new InvalidOperationException("The PgVector container is not available."));
}

public sealed class PgVectorStringKeyTests(PgVectorFixture fixture) : StoreSuite<PgVectorFixture>(fixture, RagKeyType.String);

public sealed class PgVectorGuidKeyTests(PgVectorFixture fixture) : StoreSuite<PgVectorFixture>(fixture, RagKeyType.Guid);
