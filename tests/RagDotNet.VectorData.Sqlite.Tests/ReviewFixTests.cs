using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace RagDotNet.VectorData.Sqlite.Tests;

using Rec = Dictionary<string, object?>;

/// <summary>Regressions from the slice 2 review.</summary>
public sealed class ReviewFixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ReadOnlyMemory<float> Query = new float[] { 1f, 0f, 0f, 0f };

    private static VectorStoreCollectionDefinition TextDefinition() =>
        CollectionTests.Definition(typeof(string), extra: new VectorStoreDataProperty("Text", typeof(string)) { IsFullTextIndexed = true });

    private static Rec Doc(string key, string text, float[]? vector = null) => new(CollectionTests.Record(key, vector)) { ["Text"] = text };

    private static Task<List<VectorSearchResult<Rec>>> HybridAsync(VectorStoreCollection<object, Rec> c, string keyword) =>
        ((IKeywordHybridSearchable<Rec>)c).HybridSearchAsync(Query, [keyword], 10, cancellationToken: Ct).ToListAsync(Ct);

    [Fact]
    public async Task Keyword_hits_survive_vacuum()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("h", TextDefinition());
        await c.EnsureCollectionExistsAsync(Ct);
        await c.UpsertAsync([Doc("a", "first"), Doc("b", "other"), Doc("c", "zx81"), Doc("d", "more")], Ct);
        await c.DeleteAsync(["a", "b"], Ct);

        using (var connection = new SqliteConnection(db.ConnectionString))
        {
            connection.Open();
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }

        var hits = await HybridAsync(c, "zx81");

        Assert.Equal("c", hits[0].Record["Key"]);
        Assert.Equal("zx81", hits[0].Record["Text"]);
    }

    [Fact]
    public async Task A_collection_named_like_a_keyword_table_is_its_own()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var other = store.GetDynamicCollection("docs_fts", CollectionTests.Definition(typeof(string)));
        await other.EnsureCollectionExistsAsync(Ct);
        await other.UpsertAsync(CollectionTests.Record("k1"), Ct);
        var docs = store.GetDynamicCollection("docs", TextDefinition());
        await docs.EnsureCollectionExistsAsync(Ct);
        await docs.UpsertAsync(Doc("d1", "zx81"), Ct);

        Assert.Equal("d1", (await HybridAsync(docs, "zx81"))[0].Record["Key"]);
        await store.EnsureCollectionDeletedAsync("docs", Ct);

        Assert.NotNull(await other.GetAsync("k1", cancellationToken: Ct));
    }

    [Fact]
    public void An_existing_file_that_is_not_sqlite_vec_names_the_option()
    {
        using var db = new TempDatabase();
        var bogus = Path.Combine(Path.GetTempPath(), "ragdotnet-not-vec-" + Guid.NewGuid().ToString("N") + ".so");
        File.WriteAllText(bogus, "not an extension");
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() => new SqliteVectorStore(db.ConnectionString, new() { VecExtensionPath = bogus }));
            Assert.Contains("SqliteVectorStoreOptions.VecExtensionPath", e.Message);
            Assert.Contains(bogus, e.Message);

            var memory = Assert.Throws<InvalidOperationException>(() => new SqliteVectorStore("Data Source=:memory:", new() { VecExtensionPath = bogus }));
            Assert.Contains("SqliteVectorStoreOptions.VecExtensionPath", memory.Message);
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    [Theory]
    [InlineData("rowid")]
    [InlineData("OID")]
    [InlineData("ragdotnet_rowid")]
    public void Reserved_storage_names_are_rejected(string storage)
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);

        var e = Assert.Throws<ArgumentException>(() => store.GetDynamicCollection("c1",
            CollectionTests.Definition(typeof(string), extra: new VectorStoreDataProperty("Id", typeof(string)) { StorageName = storage })));
        Assert.Contains(storage, e.Message);
    }

    [Fact]
    public async Task A_zero_vector_does_not_break_search()
    {
        using var db = new TempDatabase();
        using var store = new SqliteVectorStore(db.ConnectionString);
        var c = store.GetDynamicCollection("z", TextDefinition());
        await c.EnsureCollectionExistsAsync(Ct);
        await c.UpsertAsync([Doc("zero", "zx81", [0f, 0f, 0f, 0f]), Doc("unit", "other")], Ct);

        var hits = await c.SearchAsync(Query, 10, cancellationToken: Ct).ToListAsync(Ct);
        var hybrid = await HybridAsync(c, "zx81");
        var zeroQuery = await c.SearchAsync(new ReadOnlyMemory<float>(new float[4]), 10, cancellationToken: Ct).ToListAsync(Ct);

        Assert.Equal(["unit"], hits.Select(h => h.Record["Key"]));
        Assert.Contains(hybrid, h => (string)h.Record["Key"]! == "zero");
        Assert.Empty(zeroQuery);
    }
}
