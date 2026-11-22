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
                await DeleteFullTextAsync(command, keyList, ct).ConfigureAwait(false);
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
                + $"ON CONFLICT({_schema.Key.Quoted}) DO UPDATE SET {string.Join(", ", columns.Skip(1).Select(c => $"{c.Quoted} = excluded.{c.Quoted}"))} RETURNING {Schema.RowId}";
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

    /// <summary>The ORDER BY list: each sort's column, then the row id so ties come back in a stable order.</summary>
    private string OrderBy(FilteredRecordRetrievalOptions<Dictionary<string, object?>> options)
    {
        if (options.OrderBy is null) return Schema.RowId;
        var sorts = options.OrderBy(new FilteredRecordRetrievalOptions<Dictionary<string, object?>>.OrderByDefinition()).Values;
        var parts = sorts.Select(s =>
        {
            var column = FilterTranslator.Field(s.PropertySelector.Body, s.PropertySelector.Parameters[0], _schema)
                ?? throw new NotSupportedException("RagDotNet.VectorData.Sqlite orders only by a record field, like r => r[\"Key\"].");
            return $"{column.Quoted} {(s.Ascending ? "ASC" : "DESC")}";
        });
        return string.Join(", ", parts.Append(Schema.RowId));
    }

    /// <summary>An exact scan: every record's distance to the query, so a filter never cuts the results short.</summary>
    public override async IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<Dictionary<string, object?>>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Skip);
        var query = QueryBytes(searchValue);
        CheckVectorProperty(options.VectorProperty);
        var (where, parameters) = Where(options.Filter);
        var includeVectors = options.IncludeVectors;
        var maxDistance = MaxDistance(options.ScoreThreshold);
        var distance = $"vec_distance_cosine({_schema.Vector.Quoted}, @q)";

        var rows = await RunAsync("Search", async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {_schema.SelectList(includeVectors)}, {distance} AS d FROM {_schema.Table} "
                + $"WHERE {_schema.Vector.Quoted} IS NOT NULL AND {distance} IS NOT NULL AND {where}{(maxDistance is null ? "" : $" AND {distance} <= @maxd")} "
                + $"ORDER BY d, {Schema.RowId} LIMIT @top OFFSET @skip";
            command.Parameters.AddRange(parameters);
            command.Parameters.AddWithValue("@q", query);
            command.Parameters.AddWithValue("@top", top);
            command.Parameters.AddWithValue("@skip", options.Skip);
            if (maxDistance is not null) command.Parameters.AddWithValue("@maxd", maxDistance.Value);

            var results = new List<VectorSearchResult<Dictionary<string, object?>>>();
            var d = _schema.Data.Count + 1 + (includeVectors ? 1 : 0);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(new(_schema.Read(reader, includeVectors), Score(reader.GetDouble(d))));
            return results;
        }, cancellationToken).ConfigureAwait(false);
        foreach (var result in rows) yield return result;
    }

    private bool IsSimilarity => _schema.Distance == DistanceFunction.CosineSimilarity;

    /// <summary>The score RagDotNet expects for a cosine distance: the similarity, or the distance itself.</summary>
    private double Score(double distance) => IsSimilarity ? 1 - distance : distance;

    /// <summary>A score threshold as the largest distance it lets through.</summary>
    private double? MaxDistance(double? threshold) => threshold is null ? null : IsSimilarity ? 1 - threshold.Value : threshold.Value;

    private byte[] QueryBytes<TInput>(TInput value)
    {
        ReadOnlyMemory<float> v = value switch
        {
            ReadOnlyMemory<float> m => m,
            float[] a => a,
            null => throw new ArgumentNullException(nameof(value)),
            _ => throw new NotSupportedException($"RagDotNet.VectorData.Sqlite searches with a ReadOnlyMemory<float> or float[] vector, not a {typeof(TInput).Name}; generate the embedding first."),
        };
        if (v.Length != _schema.Dimensions)
            throw new ArgumentException($"The query vector has {v.Length} dimensions but '{_schema.Vector.Name}' expects {_schema.Dimensions}.", nameof(value));
        return Schema.QueryBytes(v);
    }

    private void CheckVectorProperty(Expression<Func<Dictionary<string, object?>, object?>>? selector)
    {
        if (selector is null) return;
        var column = FilterTranslator.Field(selector.Body, selector.Parameters[0], _schema, allowVector: true);
        if (column != _schema.Vector)
            throw new NotSupportedException($"RagDotNet.VectorData.Sqlite searches the vector property '{_schema.Vector.Name}' only.");
    }

    private (string Sql, IReadOnlyList<SqliteParameter> Parameters) Where(Expression<Func<Dictionary<string, object?>, bool>>? filter) =>
        filter is null ? ("1", []) : FilterTranslator.Translate(filter, _schema, "f");

    /// <summary>Reciprocal rank fusion constant.</summary>
    private const int RrfK = 60;

    /// <summary>
    /// Candidates taken from each ranking before fusing. A pool larger than one page keeps a later page consistent with
    /// a larger first page; the vector ranking is an exact scan either way, so the pool size costs little.
    /// </summary>
    private const int MinPool = 100;

    /// <summary>FTS5 keyword ranking (bm25) fused with the exact vector ranking by reciprocal rank fusion (k = 60).</summary>
    public async IAsyncEnumerable<VectorSearchResult<Dictionary<string, object?>>> HybridSearchAsync<TInput>(TInput searchValue, ICollection<string> keywords, int top, HybridSearchOptions<Dictionary<string, object?>>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Skip);
        var text = _schema.FullText
            ?? throw new NotSupportedException($"Collection '{Name}' has no full-text indexed property, so RagDotNet.VectorData.Sqlite cannot run hybrid search on it.");
        if (options.AdditionalProperty is not null
            && FilterTranslator.Field(options.AdditionalProperty.Body, options.AdditionalProperty.Parameters[0], _schema) != text)
            throw new NotSupportedException($"RagDotNet.VectorData.Sqlite runs keyword search on the full-text property '{text.Name}' only.");
        if (options.ScoreThreshold is not null)
            throw new NotSupportedException("RagDotNet.VectorData.Sqlite does not support ScoreThreshold for hybrid search: fused scores are ranks, not similarities.");

        var match = MatchQuery(keywords);
        var vectorOptions = new VectorSearchOptions<Dictionary<string, object?>> { Filter = options.Filter, IncludeVectors = options.IncludeVectors, Skip = match is null ? options.Skip : 0 };
        if (match is null)
        {
            await foreach (var hit in SearchAsync(searchValue, top, vectorOptions, cancellationToken).ConfigureAwait(false)) yield return hit;
            yield break;
        }

        var pool = Math.Max(top + options.Skip, MinPool);
        var byVector = new List<Dictionary<string, object?>>();
        await foreach (var hit in SearchAsync(searchValue, pool, vectorOptions, cancellationToken).ConfigureAwait(false)) byVector.Add(hit.Record);
        var byKeyword = await KeywordRankingAsync(match, options.Filter, options.IncludeVectors, pool, cancellationToken).ConfigureAwait(false);

        var fused = new Dictionary<string, (Dictionary<string, object?> Record, double Score)>(StringComparer.Ordinal);
        foreach (var ranking in new[] { byVector, byKeyword })
            for (var rank = 1; rank <= ranking.Count; rank++)
            {
                var record = ranking[rank - 1];
                var key = (string)_schema.Key.ToDb(record[_schema.Key.Name]);
                var score = 1.0 / (RrfK + rank);
                fused[key] = fused.TryGetValue(key, out var seen) ? (seen.Record, seen.Score + score) : (record, score);
            }

        foreach (var (_, (record, score)) in fused.OrderByDescending(f => f.Value.Score).ThenBy(f => f.Key, StringComparer.Ordinal).Skip(options.Skip).Take(top))
            yield return new VectorSearchResult<Dictionary<string, object?>>(record, score);
    }

    /// <summary>
    /// Each keyword as a quoted FTS5 phrase, joined with OR, so quotes, operators and column syntax in the input are
    /// plain text. Keywords with no letters or digits produce no tokens and are dropped; null when none are left.
    /// </summary>
    internal static string? MatchQuery(IEnumerable<string> keywords)
    {
        var phrases = keywords
            .Where(k => k is not null && k.Any(char.IsLetterOrDigit))
            .Select(k => "\"" + k.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return phrases.Count == 0 ? null : string.Join(" OR ", phrases);
    }

    private Task<List<Dictionary<string, object?>>> KeywordRankingAsync(string match, Expression<Func<Dictionary<string, object?>, bool>>? filter, bool includeVectors, int pool, CancellationToken cancellationToken)
    {
        var (where, parameters) = Where(filter);
        return RunAsync("HybridSearch", async (connection, ct) =>
        {
            using var command = connection.CreateCommand();
            // The FTS rows are ranked in a CTE with names of its own, so the filter's unqualified columns stay unambiguous.
            command.CommandText =
                $"WITH ragdotnet_k AS (SELECT rowid AS ragdotnet_fts_rowid, bm25({_schema.FtsTable}) AS ragdotnet_bm25 FROM {_schema.FtsTable} WHERE {_schema.FtsTable} MATCH @match) "
                + $"SELECT {_schema.SelectList(includeVectors)} FROM {_schema.Table} JOIN ragdotnet_k ON {_schema.Table}.{Schema.RowId} = ragdotnet_k.ragdotnet_fts_rowid "
                + $"WHERE {where} ORDER BY ragdotnet_k.ragdotnet_bm25, {_schema.Table}.{Schema.RowId} LIMIT @pool";
            command.Parameters.AddRange(parameters);
            command.Parameters.AddWithValue("@match", match);
            command.Parameters.AddWithValue("@pool", pool);
            return await ReadAllAsync(command, includeVectors, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType == typeof(VectorStoreCollectionMetadata) ? _metadata : null;
    }

    /// <summary>
    /// Creates the FTS5 table (its rowid is the main table's ragdotnet_rowid). When it is new on an existing collection, fills it
    /// from the rows already there.
    /// </summary>
    private async Task EnsureFullTextAsync(SqliteConnection connection, CancellationToken ct)
    {
        if (_schema.FullText is not { } text) return;
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
            exists.Parameters.AddWithValue("$n", Schema.FtsName(_schema.Name));
            if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return;
        }

        await ExecAsync(connection, $"CREATE VIRTUAL TABLE {_schema.FtsTable} USING fts5(ragdotnet_text, tokenize = 'unicode61 remove_diacritics 2')", ct).ConfigureAwait(false);
        await ExecAsync(connection, $"INSERT INTO {_schema.FtsTable} (rowid, ragdotnet_text) SELECT {Schema.RowId}, {text.Quoted} FROM {_schema.Table} WHERE {text.Quoted} IS NOT NULL", ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the record's FTS row, in the upsert's transaction.</summary>
    private async Task UpsertFullTextAsync(SqliteConnection connection, long rowid, object[] row, CancellationToken ct)
    {
        if (_schema.FullText is not { } text) return;
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_schema.FtsTable} WHERE rowid = @r";
        command.Parameters.AddWithValue("@r", rowid);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var value = row[1 + IndexOf(text)];
        if (value is DBNull) return;
        command.CommandText = $"INSERT INTO {_schema.FtsTable} (rowid, ragdotnet_text) VALUES (@r, @t)";
        command.Parameters.AddWithValue("@t", value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the FTS rows of the keys bound in <paramref name="keyed"/>, before their main rows go.</summary>
    private async Task DeleteFullTextAsync(SqliteCommand keyed, string keyList, CancellationToken ct)
    {
        if (_schema.FullText is null) return;
        keyed.CommandText = $"DELETE FROM {_schema.FtsTable} WHERE rowid IN (SELECT {Schema.RowId} FROM {_schema.Table} WHERE {_schema.Key.Quoted} IN ({keyList}))";
        await keyed.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private int IndexOf(Column column)
    {
        for (var i = 0; i < _schema.Data.Count; i++)
            if (_schema.Data[i] == column) return i;
        throw new InvalidOperationException($"'{column.Name}' is not a data column.");
    }

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
