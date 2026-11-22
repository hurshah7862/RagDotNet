using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite;

/// <summary>A dynamic collection: one table, plus an FTS5 table when a property is full-text indexed.</summary>
internal sealed partial class SqliteCollection
    : VectorStoreCollection<object, Dictionary<string, object?>>, IKeywordHybridSearchable<Dictionary<string, object?>>
{
    private const int KeyBatch = 500;

    private readonly SqliteVectorStore _store;
    private readonly Schema _schema;
    private readonly VectorStoreCollectionMetadata _metadata;

    internal SqliteCollection(SqliteVectorStore store, Schema schema, string? storeName)
    {
        _store = store;
        _schema = schema;
        _metadata = new VectorStoreCollectionMetadata { VectorStoreSystemName = "sqlite", VectorStoreName = storeName, CollectionName = schema.Name };
    }

    public override string Name => _schema.Name;

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        _store.CollectionExistsAsync(Name, cancellationToken);

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        RunAsync("EnsureCollectionExists", async (connection, ct) =>
        {
            using var tx = connection.BeginTransaction();
            await SqliteVectorStore.EnsureRegistryAsync(connection, ct).ConfigureAwait(false);
            await ExecAsync(connection, _schema.CreateTableSql(), ct).ConfigureAwait(false);

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"SELECT name FROM pragma_table_info({SqlString(_schema.Name)})";
                using var reader = await info.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) existing.Add(reader.GetString(0));
            }

            foreach (var c in _schema.Data.Where(c => !existing.Contains(c.Storage)))
                await ExecAsync(connection, $"ALTER TABLE {_schema.Table} ADD COLUMN {c.Quoted} {Schema.SqlType(c.Kind)}", ct).ConfigureAwait(false);
            foreach (var sql in _schema.CreateIndexSql())
                await ExecAsync(connection, sql, ct).ConfigureAwait(false);
            await EnsureFullTextAsync(connection, ct).ConfigureAwait(false);

            using (var reg = connection.CreateCommand())
            {
                reg.CommandText = $"INSERT INTO {Schema.Registry} (name, definition) VALUES ($n, $d) ON CONFLICT(name) DO UPDATE SET definition = excluded.definition";
                reg.Parameters.AddWithValue("$n", _schema.Name);
                reg.Parameters.AddWithValue("$d", _schema.DefinitionJson());
                await reg.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            return true;
        }, cancellationToken);

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) =>
        _store.EnsureCollectionDeletedAsync(Name, cancellationToken);

    public override async Task<Dictionary<string, object?>?> GetAsync(object key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rows = await ReadKeysAsync([key], options?.IncludeVectors ?? false, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public override async IAsyncEnumerable<Dictionary<string, object?>> GetAsync(IEnumerable<object> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var batch in keys.Chunk(KeyBatch))
            foreach (var row in await ReadKeysAsync(batch, options?.IncludeVectors ?? false, cancellationToken).ConfigureAwait(false))
                yield return row;
    }

    private Task<List<Dictionary<string, object?>>> ReadKeysAsync(IReadOnlyList<object> keys, bool includeVectors, CancellationToken cancellationToken) =>
        RunAsync("Get", async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {_schema.SelectList(includeVectors)} FROM {_schema.Table} WHERE {_schema.Key.Quoted} IN ({KeyParameters(command, keys)})";
            return await ReadAllAsync(command, includeVectors, ct).ConfigureAwait(false);
        }, cancellationToken);

    public override Task DeleteAsync(object key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return DeleteAsync([key], cancellationToken);
    }

    public override Task DeleteAsync(IEnumerable<object> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var batches = keys.Chunk(KeyBatch).ToList();
        if (batches.Count == 0) return Task.CompletedTask;
        return RunAsync("Delete", async (connection, ct) =>
        {
            using var tx = connection.BeginTransaction();
            foreach (var batch in batches)
            {
                using var command = connection.CreateCommand();
                var keyList = KeyParameters(command, batch);
                await DeleteFullTextAsync(connection, command, keyList, ct).ConfigureAwait(false);
                command.CommandText = $"DELETE FROM {_schema.Table} WHERE {_schema.Key.Quoted} IN ({keyList})";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            return true;
        }, cancellationToken);
    }

    public override Task UpsertAsync(Dictionary<string, object?> record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return UpsertAsync([record], cancellationToken);
    }

    public override Task UpsertAsync(IEnumerable<Dictionary<string, object?>> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        // Convert first, so a bad record fails with ArgumentException before anything is written.
        var rows = records.Select(ToRow).ToList();
        if (rows.Count == 0) return Task.CompletedTask;
        return RunAsync("Upsert", async (connection, ct) =>
        {
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            var columns = _schema.Data.Prepend(_schema.Key).Append(_schema.Vector).ToList();
            var parameters = columns.Select((c, i) => command.Parameters.Add("$p" + i, SqliteType.Text)).ToList();
            command.CommandText =
                $"INSERT INTO {_schema.Table} ({string.Join(", ", columns.Select(c => c.Quoted))}) VALUES ({string.Join(", ", parameters.Select(p => p.ParameterName))}) "
                + $"ON CONFLICT({_schema.Key.Quoted}) DO UPDATE SET {string.Join(", ", columns.Skip(1).Select(c => $"{c.Quoted} = excluded.{c.Quoted}"))} RETURNING rowid";
            foreach (var row in rows)
            {
                for (var i = 0; i < row.Length; i++)
                {
                    parameters[i].SqliteType = row[i] is byte[] ? SqliteType.Blob : row[i] is long ? SqliteType.Integer : SqliteType.Text;
                    parameters[i].Value = row[i];
                }

                var rowid = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
                await UpsertFullTextAsync(connection, rowid, row, ct).ConfigureAwait(false);
            }

            tx.Commit();
            return true;
        }, cancellationToken);
    }

    /// <summary>Column values in key, data..., vector order.</summary>
    private object[] ToRow(Dictionary<string, object?> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var row = new object[_schema.Data.Count + 2];
        row[0] = record.TryGetValue(_schema.Key.Name, out var key) && key is not null
            ? _schema.Key.ToDb(key)
            : throw new ArgumentException($"The record has no value for the key property '{_schema.Key.Name}'.", nameof(record));
        for (var i = 0; i < _schema.Data.Count; i++)
            row[i + 1] = _schema.Data[i].ToDb(record.GetValueOrDefault(_schema.Data[i].Name));
        row[^1] = _schema.VectorBytes(record.GetValueOrDefault(_schema.Vector.Name));
        return row;
    }

    public override async IAsyncEnumerable<Dictionary<string, object?>> GetAsync(Expression<Func<Dictionary<string, object?>, bool>> filter, int top, FilteredRecordRetrievalOptions<Dictionary<string, object?>>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Skip);
        var (where, parameters) = FilterTranslator.Translate(filter, _schema, "f");
        var orderBy = OrderBy(options);
        var includeVectors = options.IncludeVectors;

        var rows = await RunAsync("Get", async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {_schema.SelectList(includeVectors)} FROM {_schema.Table} WHERE {where} ORDER BY {orderBy} LIMIT @top OFFSET @skip";
            command.Parameters.AddRange(parameters);
            command.Parameters.AddWithValue("@top", top);
            command.Parameters.AddWithValue("@skip", options.Skip);
            return await ReadAllAsync(command, includeVectors, ct).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows) yield return row;
    }

    /// <summary>The ORDER BY list: each sort's column, then rowid so ties come back in a stable order.</summary>
    private string OrderBy(FilteredRecordRetrievalOptions<Dictionary<string, object?>> options)
    {
        if (options.OrderBy is null) return "rowid";
        var sorts = options.OrderBy(new FilteredRecordRetrievalOptions<Dictionary<string, object?>>.OrderByDefinition()).Values;
        var parts = sorts.Select(s =>
        {
            var column = FilterTranslator.Field(s.PropertySelector.Body, s.PropertySelector.Parameters[0], _schema)
                ?? throw new NotSupportedException("RagDotNet.VectorData.Sqlite orders only by a record field, like r => r[\"Key\"].");
            return $"{column.Quoted} {(s.Ascending ? "ASC" : "DESC")}";
        });
        return string.Join(", ", parts.Append("rowid"));
    }

    public override IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<Dictionary<string, object?>>? options = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> HybridSearchAsync<TInput>(TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<Dictionary<string, object?>>? options = null, CancellationToken cancellationToken = default)
        where TInput : notnull =>
        throw new NotImplementedException();

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata) ? _metadata : null;
    }

    // Full-text sync; the FTS5 table arrives in Task 5.
    private static Task EnsureFullTextAsync(SqliteConnection connection, CancellationToken ct) => Task.CompletedTask;

    private static Task UpsertFullTextAsync(SqliteConnection connection, long rowid, object[] row, CancellationToken ct) => Task.CompletedTask;

    private static Task DeleteFullTextAsync(SqliteConnection connection, SqliteCommand keyed, string keyList, CancellationToken ct) => Task.CompletedTask;

    private string KeyParameters(SqliteCommand command, IReadOnlyList<object> keys)
    {
        var names = new string[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            names[i] = "$k" + i;
            command.Parameters.AddWithValue(names[i], _schema.Key.ToDb(keys[i] ?? throw new ArgumentException("A key is null.", nameof(keys))));
        }

        return string.Join(", ", names);
    }

    private async Task<List<Dictionary<string, object?>>> ReadAllAsync(SqliteCommand command, bool includeVectors, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(_schema.Read(reader, includeVectors));
        return rows;
    }

    internal static async Task ExecAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string SqlString(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private Task<T> RunAsync<T>(string operation, Func<SqliteConnection, CancellationToken, Task<T>> body, CancellationToken ct) =>
        _store.RunAsync(operation, Name, body, ct);
}
