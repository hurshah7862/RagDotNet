using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite;

/// <summary>How a value is stored in its SQLite column.</summary>
internal enum ColumnKind
{
    Text,
    Guid,
    Int32,
    Int64,
    Bool,
    Date,
    StringArray,
    Vector,
}

/// <summary>One property of a collection definition and its column.</summary>
internal sealed record Column(string Name, string Storage, Type Type, ColumnKind Kind, bool IsIndexed, bool IsFullText)
{
    public string Quoted => Schema.Quote(Storage);

    /// <summary>The CLR value as a SQLite parameter value.</summary>
    public object ToDb(object? value)
    {
        if (value is null) return DBNull.Value;
        return Kind switch
        {
            ColumnKind.Text => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)!,
            ColumnKind.Guid => value switch
            {
                Guid g => g.ToString("D"),
                string s => Guid.Parse(s).ToString("D"),
                _ => throw Wrong(value),
            },
            ColumnKind.Int32 or ColumnKind.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ColumnKind.Bool => value is bool b ? (b ? 1L : 0L) : throw Wrong(value),
            ColumnKind.Date => value switch
            {
                DateTimeOffset d => d.UtcTicks,
                DateTime d => new DateTimeOffset(d).UtcTicks,
                _ => throw Wrong(value),
            },
            ColumnKind.StringArray => value is IEnumerable<string> a ? JsonSerializer.Serialize(a.ToArray()) : throw Wrong(value),
            _ => throw new InvalidOperationException($"'{Name}' is not a data column."),
        };
    }

    /// <summary>The column value at <paramref name="ordinal"/>, as the declared CLR type.</summary>
    public object? FromDb(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return Kind switch
        {
            ColumnKind.Text => reader.GetString(ordinal),
            ColumnKind.Guid => Guid.Parse(reader.GetString(ordinal)),
            ColumnKind.Int32 => checked((int)reader.GetInt64(ordinal)),
            ColumnKind.Int64 => reader.GetInt64(ordinal),
            ColumnKind.Bool => reader.GetInt64(ordinal) != 0,
            ColumnKind.Date => new DateTimeOffset(reader.GetInt64(ordinal), TimeSpan.Zero),
            ColumnKind.StringArray => JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? [],
            ColumnKind.Vector => VectorFromDb((byte[])reader.GetValue(ordinal)),
            _ => throw new InvalidOperationException($"Unknown column kind {Kind}."),
        };
    }

    private object VectorFromDb(byte[] bytes)
    {
        var floats = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        return Type == typeof(float[]) ? floats : new ReadOnlyMemory<float>(floats);
    }

    private ArgumentException Wrong(object value) =>
        new($"Property '{Name}' is declared as {Type.Name} but the record holds a {value.GetType().Name}.");
}

/// <summary>A collection definition mapped to SQLite: one table, a column per property, the vector as a float32 BLOB.</summary>
internal sealed partial class Schema
{
    internal const string Registry = "ragdotnet_collections";

    private Schema(string name, Column key, IReadOnlyList<Column> data, Column vector, int dimensions, string distance)
    {
        Name = name;
        Key = key;
        Data = data;
        Vector = vector;
        Dimensions = dimensions;
        Distance = distance;
        FullText = data.FirstOrDefault(c => c.IsFullText);
    }

    public string Name { get; }

    public string Table => Quote(Name);

    public string FtsTable => Quote(FtsName(Name));

    /// <summary>The FTS5 table of a collection. User collections cannot start with "ragdotnet_", so it never collides with one.</summary>
    public static string FtsName(string collection) => "ragdotnet_fts_" + collection;

    /// <summary>
    /// The explicit INTEGER PRIMARY KEY that links a row to its FTS row. An implicit rowid may change on VACUUM; this one does not.
    /// </summary>
    public const string RowId = "\"ragdotnet_rowid\"";

    public Column Key { get; }

    public IReadOnlyList<Column> Data { get; }

    public Column Vector { get; }

    public Column? FullText { get; }

    public int Dimensions { get; }

    public string Distance { get; }

    public IEnumerable<Column> All => Data.Prepend(Key).Append(Vector);

    public Column? Find(string propertyName) => All.FirstOrDefault(c => c.Name == propertyName);

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex NamePattern();

    public static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!NamePattern().IsMatch(name) || name.StartsWith("ragdotnet_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Collection name '{name}' is not valid for SQLite: use letters, digits and underscores, starting with a letter or underscore, at most 128 characters, and not starting with 'ragdotnet_'.",
                nameof(name));
    }

    public static Schema From(string name, VectorStoreCollectionDefinition definition)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(definition);

        Column? key = null;
        Column? vector = null;
        var dimensions = 0;
        var distance = DistanceFunction.CosineSimilarity;
        var data = new List<Column>();
        foreach (var p in definition.Properties)
        {
            var storage = p.StorageName ?? p.Name;
            if (storage.StartsWith("ragdotnet_", StringComparison.OrdinalIgnoreCase) || Reserved.Contains(storage))
                throw new ArgumentException($"Property '{p.Name}' stores to '{storage}', a name SQLite or RagDotNet.VectorData.Sqlite reserves; set a different StorageName.", nameof(definition));
            var type = p.Type ?? throw new ArgumentException($"Property '{p.Name}' has no type; dynamic collections need one.", nameof(definition));
            switch (p)
            {
                case VectorStoreKeyProperty k:
                    if (key is not null) throw Unsupported("more than one key property");
                    var keyKind = type == typeof(string) ? ColumnKind.Text
                        : type == typeof(Guid) ? ColumnKind.Guid
                        : throw Unsupported($"key type {type.Name}");
                    key = new Column(k.Name, storage, type, keyKind, false, false);
                    break;
                case VectorStoreDataProperty d:
                    var kind = DataKind(type) ?? throw Unsupported($"data property '{d.Name}' of type {type.Name}");
                    if (d.IsFullTextIndexed && kind != ColumnKind.Text) throw Unsupported($"full-text search on '{d.Name}', which is not a string");
                    if (d.IsFullTextIndexed && data.Any(c => c.IsFullText)) throw Unsupported("more than one full-text property");
                    data.Add(new Column(d.Name, storage, type, kind, d.IsIndexed, d.IsFullTextIndexed));
                    break;
                case VectorStoreVectorProperty v:
                    if (vector is not null) throw Unsupported("more than one vector property");
                    if (type != typeof(ReadOnlyMemory<float>) && type != typeof(ReadOnlyMemory<float>?) && type != typeof(float[]))
                        throw Unsupported($"vector property '{v.Name}' of type {type.Name}; use ReadOnlyMemory<float>");
                    distance = v.DistanceFunction ?? DistanceFunction.CosineSimilarity;
                    if (distance != DistanceFunction.CosineSimilarity && distance != DistanceFunction.CosineDistance)
                        throw Unsupported($"distance function {distance}; use CosineSimilarity or CosineDistance");
                    if (v.Dimensions <= 0) throw new ArgumentException($"Vector property '{v.Name}' needs a positive number of dimensions.", nameof(definition));
                    dimensions = v.Dimensions;
                    vector = new Column(v.Name, storage, type, ColumnKind.Vector, false, false);
                    break;
                default:
                    throw Unsupported($"property kind {p.GetType().Name}");
            }
        }

        if (key is null) throw new ArgumentException("The definition has no key property.", nameof(definition));
        if (vector is null) throw new ArgumentException("The definition has no vector property.", nameof(definition));
        var duplicate = data.Select(c => c.Storage).Append(key.Storage).Append(vector.Storage)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Two properties store to the column '{duplicate.Key}'.", nameof(definition));
        return new Schema(name, key, data, vector, dimensions, distance);
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "rowid", "oid", "_rowid_" };

    private static ColumnKind? DataKind(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return ColumnKind.Text;
        if (u == typeof(int)) return ColumnKind.Int32;
        if (u == typeof(long)) return ColumnKind.Int64;
        if (u == typeof(bool)) return ColumnKind.Bool;
        if (u == typeof(DateTimeOffset)) return ColumnKind.Date;
        if (u == typeof(string[])) return ColumnKind.StringArray;
        return null;
    }

    internal static NotSupportedException Unsupported(string what) =>
        new($"RagDotNet.VectorData.Sqlite does not support {what}.");

    public static string SqlType(ColumnKind kind) => kind switch
    {
        ColumnKind.Int32 or ColumnKind.Int64 or ColumnKind.Bool or ColumnKind.Date => "INTEGER",
        ColumnKind.Vector => "BLOB",
        _ => "TEXT",
    };

    public string CreateTableSql() =>
        $"CREATE TABLE IF NOT EXISTS {Table} ({RowId} INTEGER PRIMARY KEY, {Key.Quoted} TEXT NOT NULL UNIQUE, "
        + string.Concat(Data.Select(c => $"{c.Quoted} {SqlType(c.Kind)}, "))
        + $"{Vector.Quoted} BLOB)";

    public IEnumerable<string> CreateIndexSql() =>
        Data.Where(c => c.IsIndexed).Select(c => $"CREATE INDEX IF NOT EXISTS {Quote("ix_" + Name + "_" + c.Storage)} ON {Table} ({c.Quoted})");

    /// <summary>The definition as JSON, for the registry row.</summary>
    public string DefinitionJson() => JsonSerializer.Serialize(new
    {
        dimensions = Dimensions,
        distance = Distance,
        properties = All.Select(c => new { name = c.Name, storage = c.Storage, kind = c.Kind.ToString(), indexed = c.IsIndexed, fullText = c.IsFullText }),
    });

    /// <summary>The select list for every data column, the key first, and the vector last when asked.</summary>
    public string SelectList(bool includeVectors) =>
        string.Join(", ", Data.Prepend(Key).Select(c => c.Quoted).Concat(includeVectors ? [Vector.Quoted] : []));

    /// <summary>Reads a row selected with <see cref="SelectList"/>.</summary>
    public Dictionary<string, object?> Read(SqliteDataReader reader, bool includeVectors)
    {
        var record = new Dictionary<string, object?>(Data.Count + 2) { [Key.Name] = Key.FromDb(reader, 0) };
        for (var i = 0; i < Data.Count; i++) record[Data[i].Name] = Data[i].FromDb(reader, i + 1);
        if (includeVectors) record[Vector.Name] = Vector.FromDb(reader, Data.Count + 1);
        return record;
    }

    /// <summary>The record's vector as sqlite-vec's float32 BLOB, checked against the declared dimensions.</summary>
    public byte[] VectorBytes(object? value)
    {
        ReadOnlyMemory<float> v = value switch
        {
            ReadOnlyMemory<float> m => m,
            float[] a => a,
            null => throw new ArgumentException($"The record has no value for the vector property '{Vector.Name}'."),
            _ => throw new ArgumentException($"Vector property '{Vector.Name}' holds a {value.GetType().Name}; use ReadOnlyMemory<float>."),
        };
        if (v.Length != Dimensions)
            throw new ArgumentException($"Vector property '{Vector.Name}' expects {Dimensions} dimensions but the record has {v.Length}.");
        return MemoryMarshal.AsBytes(v.Span).ToArray();
    }

    public static byte[] QueryBytes(ReadOnlyMemory<float> v) => MemoryMarshal.AsBytes(v.Span).ToArray();
}
