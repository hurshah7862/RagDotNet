using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

public sealed class CollectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal const int Dims = 4;

    /// <summary>One property of each type RagDotNet stores, plus a 4-dimension vector.</summary>
    internal static VectorStoreCollectionDefinition Definition(Type keyType, string distance = DistanceFunction.CosineSimilarity, params VectorStoreProperty[] extra)
    {
        var props = new List<VectorStoreProperty>
        {
            new VectorStoreKeyProperty("Key", keyType),
            new VectorStoreDataProperty("S", typeof(string)) { IsIndexed = true },
            new VectorStoreDataProperty("I", typeof(int)),
            new VectorStoreDataProperty("L", typeof(long)),
            new VectorStoreDataProperty("B", typeof(bool)),
            new VectorStoreDataProperty("D", typeof(DateTimeOffset)),
            new VectorStoreDataProperty("A", typeof(string[])),
            new VectorStoreDataProperty("NI", typeof(int?)),
            new VectorStoreDataProperty("NL", typeof(long?)),
            new VectorStoreDataProperty("NB", typeof(bool?)),
            new VectorStoreDataProperty("ND", typeof(DateTimeOffset?)),
        };
        props.AddRange(extra);
        props.Add(new VectorStoreVectorProperty("Embedding", typeof(ReadOnlyMemory<float>), Dims) { DistanceFunction = distance });
        return new VectorStoreCollectionDefinition { Properties = props };
    }

    private static readonly string[] DataNames = ["S", "I", "L", "B", "D", "A", "NI", "NL", "NB", "ND"];

    internal static Dictionary<string, object?> Record(object key, float[]? vector = null) => new()
    {
        ["Key"] = key,
        ["Embedding"] = new ReadOnlyMemory<float>(vector ?? [1f, 0f, 0f, 0f]),
    };

    private static Dictionary<string, object?> Full(object key) => new(Record(key, [0.1f, 0.2f, 0.3f, 0.4f]))
    {
        ["S"] = "text é",
        ["I"] = 42,
        ["L"] = 5_000_000_000L,
        ["B"] = true,
        ["D"] = new DateTimeOffset(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5)),
        ["A"] = new[] { "x", "y z" },
        ["NI"] = 7,
        ["NL"] = -3L,
        ["NB"] = false,
        ["ND"] = new DateTimeOffset(2026, 6, 1, 8, 0, 0, 456, TimeSpan.FromHours(-7)),
    };

    private static async Task<VectorStoreCollection<object, Dictionary<string, object?>>> EnsuredAsync(SqliteVectorStore store, string name = "c1", Type? keyType = null)
    {
        var c = store.GetDynamicCollection(name, Definition(keyType ?? typeof(string)));
        await c.EnsureCollectionExistsAsync(Ct);
        return c;
    }

    private static async Task<List<string>> NamesAsync(SqliteVectorStore store)
    {
        var names = new List<string>();
        await foreach (var n in store.ListCollectionNamesAsync(Ct)) names.Add(n);
        return names;
    }

    [Fact]
    public async Task Ensure_exists_creates_and_is_idempotent()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("c1", Definition(typeof(string)));

        Assert.False(await c.CollectionExistsAsync(Ct));
        Assert.False(await store.CollectionExistsAsync("c1", Ct));
        await c.EnsureCollectionExistsAsync(Ct);
        await c.EnsureCollectionExistsAsync(Ct);

        Assert.True(await c.CollectionExistsAsync(Ct));
        Assert.True(await store.CollectionExistsAsync("c1", Ct));
        Assert.Equal(["c1"], await NamesAsync(store));
    }

    [Fact]
    public async Task Delete_collection_removes_it()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);
        await c.UpsertAsync(Full("k1"), Ct);

        await store.EnsureCollectionDeletedAsync("c1", Ct);

        Assert.False(await c.CollectionExistsAsync(Ct));
        Assert.Empty(await NamesAsync(store));
        await store.EnsureCollectionDeletedAsync("c1", Ct);
        await c.EnsureCollectionDeletedAsync(Ct);
    }

    public static TheoryData<string> KeyTypes => new() { "string", "guid" };

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public async Task Every_type_round_trips(string keyType)
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        object key = keyType == "guid" ? Guid.NewGuid() : "k1";
        var c = await EnsuredAsync(store, keyType: key.GetType());
        var expected = Full(key);

        await c.UpsertAsync(expected, Ct);
        var got = await c.GetAsync(key, new RecordRetrievalOptions { IncludeVectors = true }, Ct);

        Assert.NotNull(got);
        Assert.Equal(key, got["Key"]);
        Assert.IsType(key.GetType(), got["Key"]);
        foreach (var name in new[] { "S", "I", "L", "B", "NI", "NL", "NB" })
        {
            Assert.Equal(expected[name], got[name]);
            Assert.IsType(expected[name]!.GetType(), got[name]);
        }

        Assert.Equal(["x", "y z"], Assert.IsType<string[]>(got["A"]));
        Assert.Equal(((DateTimeOffset)expected["D"]!).UtcTicks, Assert.IsType<DateTimeOffset>(got["D"]).UtcTicks);
        Assert.Equal(((DateTimeOffset)expected["ND"]!).UtcTicks, Assert.IsType<DateTimeOffset>(got["ND"]).UtcTicks);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], Assert.IsType<ReadOnlyMemory<float>>(got["Embedding"]).ToArray());
    }

    [Fact]
    public async Task Missing_and_null_values_read_back_as_null()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);
        var nulls = Record("k2");
        foreach (var name in DataNames) nulls[name] = null;

        await c.UpsertAsync([Record("k1"), nulls], Ct);

        foreach (var key in new[] { "k1", "k2" })
        {
            var got = await c.GetAsync(key, cancellationToken: Ct);
            Assert.NotNull(got);
            foreach (var name in DataNames)
            {
                Assert.True(got.ContainsKey(name), $"{key} lacks {name}");
                Assert.Null(got[name]);
            }
        }
    }

    [Fact]
    public async Task Vectors_only_when_asked()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);
        await c.UpsertAsync(Full("k1"), Ct);

        var without = await c.GetAsync("k1", cancellationToken: Ct);
        var with = await c.GetAsync("k1", new RecordRetrievalOptions { IncludeVectors = true }, Ct);

        Assert.True(!without!.TryGetValue("Embedding", out var v) || v is null);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], ((ReadOnlyMemory<float>)with!["Embedding"]!).ToArray());
    }

    [Fact]
    public async Task Upsert_replaces_and_delete_removes()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);
        await c.UpsertAsync(Full("k1"), Ct);
        var changed = Full("k1");
        changed["S"] = "new";
        changed["I"] = null;
        await c.UpsertAsync([changed, Full("k2")], Ct);

        var got = await c.GetAsync(["k1", "k2", "k3"], cancellationToken: Ct).ToListAsync(Ct);
        Assert.Equal(2, got.Count);
        var k1 = Assert.Single(got, r => (string)r["Key"]! == "k1");
        Assert.Equal("new", k1["S"]);
        Assert.Null(k1["I"]);

        await c.DeleteAsync(["k1", "k2"], Ct);
        await c.DeleteAsync("missing", Ct);
        Assert.Empty(await c.GetAsync(["k1", "k2"], cancellationToken: Ct).ToListAsync(Ct));
        Assert.Null(await c.GetAsync("k1", cancellationToken: Ct));
    }

    [Fact]
    public async Task Get_many_keys_spans_batches()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);
        var keys = Enumerable.Range(0, 1201).Select(i => (object)$"k{i}").ToList();
        await c.UpsertAsync(keys.Select(k => Record(k)), Ct);

        var got = await c.GetAsync(keys, cancellationToken: Ct).ToListAsync(Ct);

        Assert.Equal(1201, got.Select(r => r["Key"]).Distinct().Count());
    }

    [Fact]
    public async Task Wrong_dimension_is_rejected()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = await EnsuredAsync(store);

        var e = await Assert.ThrowsAsync<ArgumentException>(() => c.UpsertAsync(Record("k1", [1f, 2f, 3f]), Ct));

        Assert.Contains("Embedding", e.Message);
        Assert.Contains("4", e.Message);
        Assert.Contains("3", e.Message);
    }

    [Fact]
    public void Invalid_names_and_types_are_rejected()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);

        Assert.Throws<ArgumentException>(() => store.GetDynamicCollection("a-b", Definition(typeof(string))));
        Assert.Throws<ArgumentException>(() => store.GetDynamicCollection("ragdotnet_x", Definition(typeof(string))));
        var dec = Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("c1", Definition(typeof(string), extra: new VectorStoreDataProperty("M", typeof(decimal)))));
        Assert.Contains("Decimal", dec.Message);
        var dist = Assert.Throws<NotSupportedException>(() => store.GetDynamicCollection("c1", Definition(typeof(string), DistanceFunction.EuclideanDistance)));
        Assert.Contains("EuclideanDistance", dist.Message);
        var typed = Assert.Throws<NotSupportedException>(() => store.GetCollection<string, object>("c1"));
        Assert.Contains("GetDynamicCollection", typed.Message);
    }

    [Fact]
    public async Task Parallel_upserts_do_not_fail()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        await EnsuredAsync(store);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            var c = store.GetDynamicCollection("c1", Definition(typeof(string)));
            for (var i = 0; i < 50; i++)
                await c.UpsertAsync(Full($"t{t}-{i}"), Ct);
        }, Ct)));

        var c = store.GetDynamicCollection("c1", Definition(typeof(string)));
        var keys = Enumerable.Range(0, 8).SelectMany(t => Enumerable.Range(0, 50).Select(i => (object)$"t{t}-{i}")).ToList();
        Assert.Equal(400, (await c.GetAsync(keys, cancellationToken: Ct).ToListAsync(Ct)).Count);
    }

    [Fact]
    public async Task Reopened_store_sees_existing_data()
    {
        using var db = new TempDatabase();
        using (var first = new SqliteVectorStore(db.ConnectionString))
            await (await EnsuredAsync(first)).UpsertAsync(Full("k1"), Ct);

        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("c1", Definition(typeof(string)));
        Assert.True(await c.CollectionExistsAsync(Ct));
        await c.EnsureCollectionExistsAsync(Ct);
        Assert.Equal("text é", (await c.GetAsync("k1", cancellationToken: Ct))!["S"]);

        var wider = store.GetDynamicCollection("c1", Definition(typeof(string), extra: new VectorStoreDataProperty("Extra", typeof(string))));
        await wider.EnsureCollectionExistsAsync(Ct);
        var old = await wider.GetAsync("k1", cancellationToken: Ct);
        Assert.True(old!.ContainsKey("Extra"));
        Assert.Null(old["Extra"]);
        var added = Full("k2");
        added["Extra"] = "e";
        await wider.UpsertAsync(added, Ct);
        Assert.Equal("e", (await wider.GetAsync("k2", cancellationToken: Ct))!["Extra"]);
    }
}
