using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;
using RagDotNet.Storage;
using RagDotNet.StoreTests.Infrastructure;
using RagDotNet.StoreTests.Suite;
using RagDotNet.VectorData.Sqlite;

namespace RagDotNet.StoreTests.Stores;

/// <summary>RagDotNet.VectorData.Sqlite on a temp file per test. No service needed, so it always runs.</summary>
public sealed class SqliteFixture : StoreFixture
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragdotnet-stores-sqlite-" + Guid.NewGuid().ToString("N"));

    public override string Name => "SQLite";

    public override bool NativeHybrid => true;

    public override ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return ValueTask.CompletedTask;
    }

    public override VectorStore CreateStore() => new SqliteVectorStore($"Data Source={Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db")}");

    public override ValueTask DisposeAsync()
    {
        // Pooled connections hold the files open, and on Windows open files cannot be deleted.
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return base.DisposeAsync();
    }
}

public sealed class SqliteStringKeyTests(SqliteFixture fixture) : StoreSuite<SqliteFixture>(fixture, RagKeyType.String);

public sealed class SqliteGuidKeyTests(SqliteFixture fixture) : StoreSuite<SqliteFixture>(fixture, RagKeyType.Guid);
