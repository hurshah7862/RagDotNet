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

    /// <inheritdoc />
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

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
