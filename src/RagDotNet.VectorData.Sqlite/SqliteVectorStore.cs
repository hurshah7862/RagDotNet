using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite;

/// <summary>
/// A Microsoft.Extensions.VectorData store on SQLite with sqlite-vec, covering what RagDotNet uses: dynamic collections,
/// RagDotNet's filters, exact vector search and FTS5 hybrid search.
/// </summary>
public sealed class SqliteVectorStore : VectorStore
{
    private readonly string _connectionString;
    private readonly string _extensionPath;
    private readonly VectorStoreMetadata _metadata;

    /// <summary>Creates a store on the database the connection string names.</summary>
    /// <param name="connectionString">A Microsoft.Data.Sqlite connection string, for example <c>Data Source=rag.db</c>.</param>
    /// <param name="options">Options; <see cref="SqliteVectorStoreOptions.VecExtensionPath"/> is needed only on platforms the package has no sqlite-vec for.</param>
    public SqliteVectorStore(string connectionString, SqliteVectorStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        _connectionString = builder.ToString();
        _extensionPath = VecExtension.Resolve(options ?? new SqliteVectorStoreOptions());
        _metadata = new VectorStoreMetadata { VectorStoreSystemName = "sqlite", VectorStoreName = builder.DataSource };

        if (IsFile(builder))
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteNonQuery();
        }
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            connection.EnableExtensions(true);
            connection.LoadExtension(_extensionPath);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            connection.EnableExtensions(true);
            connection.LoadExtension(_extensionPath);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool IsFile(SqliteConnectionStringBuilder b) =>
        b.Mode != SqliteOpenMode.Memory && !string.IsNullOrEmpty(b.DataSource) && b.DataSource != ":memory:";

    /// <summary>Not supported: RagDotNet uses dynamic collections only. Use <see cref="GetDynamicCollection"/>.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        throw new NotSupportedException("RagDotNet.VectorData.Sqlite does not support typed collections; use GetDynamicCollection.");

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition) =>
        new SqliteCollection(this, Schema.From(name, definition), _metadata.VectorStoreName);

    /// <inheritdoc />
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var names = await RunAsync("ListCollectionNames", null, async (connection, ct) =>
        {
            await EnsureRegistryAsync(connection, ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT name FROM {Schema.Registry} ORDER BY name";
            var list = new List<string>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(reader.GetString(0));
            return list;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var name in names) yield return name;
    }

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        Schema.ValidateName(name);
        return RunAsync("CollectionExists", name, async (connection, ct) =>
        {
            await EnsureRegistryAsync(connection, ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {Schema.Registry} WHERE name = $n";
            command.Parameters.AddWithValue("$n", name);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        Schema.ValidateName(name);
        return RunAsync("EnsureCollectionDeleted", name, async (connection, ct) =>
        {
            using var tx = connection.BeginTransaction();
            await EnsureRegistryAsync(connection, ct).ConfigureAwait(false);
            await SqliteCollection.ExecAsync(connection, $"DROP TABLE IF EXISTS {Schema.Quote(name + "_fts")}", ct).ConfigureAwait(false);
            await SqliteCollection.ExecAsync(connection, $"DROP TABLE IF EXISTS {Schema.Quote(name)}", ct).ConfigureAwait(false);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"DELETE FROM {Schema.Registry} WHERE name = $n";
                command.Parameters.AddWithValue("$n", name);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            return true;
        }, cancellationToken);
    }

    internal static Task EnsureRegistryAsync(SqliteConnection connection, CancellationToken ct) =>
        SqliteCollection.ExecAsync(connection, $"CREATE TABLE IF NOT EXISTS {Schema.Registry} (name TEXT PRIMARY KEY NOT NULL, definition TEXT NOT NULL)", ct);

    /// <summary>Runs <paramref name="body"/> on an open connection and reports SQLite failures as <see cref="VectorStoreException"/>.</summary>
    internal async Task<T> RunAsync<T>(string operation, string? collection, Func<SqliteConnection, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try
        {
            using var connection = await OpenAsync(ct).ConfigureAwait(false);
            return await body(connection, ct).ConfigureAwait(false);
        }
        catch (SqliteException e)
        {
            throw new VectorStoreException($"SQLite failed during {operation}: {e.Message}", e)
            {
                VectorStoreSystemName = "sqlite",
                VectorStoreName = _metadata.VectorStoreName,
                CollectionName = collection,
                OperationName = operation,
            };
        }
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreMetadata) ? _metadata : null;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Pooled connections keep the file open, and on Windows an open file cannot be deleted.
            using var connection = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(connection);
        }

        base.Dispose(disposing);
    }
}
