using System.Linq.Expressions;
using CommunityToolkit.VectorData.InMemory;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Filtering;
using RagDotNet.Retrieval;
using RagDotNet.Storage;

namespace RagDotNet.Core.Tests;

/// <summary>
/// Bool and date metadata are stored as numbers: some connectors (Redis) index only strings, string arrays and numbers.
/// Core converts at the store boundary only, so callers see the declared types.
/// </summary>
public sealed class StoredValueTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Type Dto = typeof(DateTimeOffset);

    [Fact]
    public void Stored_types_are_strings_arrays_or_numbers()
    {
        Assert.Equal(typeof(int?), StoredValues.StoreType(typeof(bool)));
        Assert.Equal(typeof(long?), StoredValues.StoreType(Dto));
        Assert.Equal(typeof(int?), StoredValues.StoreType(typeof(int)));
        Assert.Equal(typeof(long?), StoredValues.StoreType(typeof(long)));
        Assert.Equal(typeof(string), StoredValues.StoreType(typeof(string)));
        Assert.Equal(typeof(string[]), StoredValues.StoreType(typeof(string[])));
    }

    [Fact]
    public void Dates_are_stored_as_unix_microseconds()
    {
        Assert.Equal(1L, StoredValues.ToStore(DateTimeOffset.UnixEpoch.AddTicks(15), Dto));
        Assert.Equal(-1L, StoredValues.ToStore(DateTimeOffset.UnixEpoch.AddTicks(-5), Dto)); // floor, not toward zero
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(-10), StoredValues.FromStore(-1L, Dto));

        var local = new DateTimeOffset(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5)).AddTicks(4560);
        var back = Assert.IsType<DateTimeOffset>(StoredValues.FromStore(StoredValues.ToStore(local, Dto), Dto));
        Assert.Equal(local, back);
        Assert.Equal(TimeSpan.Zero, back.Offset);
        Assert.Null(StoredValues.ToStore(null, Dto));
        Assert.Null(StoredValues.FromStore(null, Dto));
    }

    [Fact]
    public void Bools_are_stored_as_ints()
    {
        Assert.Equal(1, StoredValues.ToStore(true, typeof(bool)));
        Assert.Equal(0, StoredValues.ToStore(false, typeof(bool)));
        Assert.Equal(false, StoredValues.FromStore(0L, typeof(bool)));
        Assert.Equal(true, StoredValues.FromStore(1, typeof(bool)));
        Assert.Equal(true, StoredValues.FromStore(1.0, typeof(bool)));
        Assert.Equal(5L, StoredValues.FromStore(5.0, typeof(long)));
        Assert.Equal(5, StoredValues.FromStore(5L, typeof(int)));
    }

    private static readonly DateTimeOffset Date = new DateTimeOffset(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5)).AddTicks(7);

    private static (InMemoryVectorStore Store, RagPipelineBuilder Builder) Pipeline()
    {
        var store = new InMemoryVectorStore();
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(16))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "sv", 16)
            .Configure(o => o.Metadata.Add<bool>("b").Add<DateTimeOffset>("d").Add<int>("i"));
        return (store, builder);
    }

    private static DocumentSource Doc() =>
        new("doc", new MemoryStream("alpha stored text"u8.ToArray()), "doc.md", "text/markdown") { Metadata = { ["b"] = true, ["d"] = Date, ["i"] = 7 } };

    [Fact]
    public async Task Metadata_comes_back_with_its_declared_type()
    {
        var (store, builder) = Pipeline();
        var rag = builder.Build();
        await rag.IngestAsync(Doc(), cancellationToken: Ct);
        var o = new RagOptions { CollectionName = "sv", EmbeddingDimensions = 16 };
        o.Metadata.Add<bool>("b").Add<DateTimeOffset>("d").Add<int>("i");

        var chunk = Assert.Single(await store.GetDynamicCollection("sv", RecordDefinitions.Chunks(o)).GetAsync(r => true, 10, cancellationToken: Ct).ToListAsync(Ct));
        Assert.Equal(1, Assert.IsType<int>(chunk[RecordDefinitions.MetaName("b")]));
        Assert.IsType<long>(chunk[RecordDefinitions.MetaName("d")]);
        Assert.Equal(7, Assert.IsType<int>(chunk[RecordDefinitions.MetaName("i")]));

        var hit = Assert.Single(await rag.RetrieveAsync("alpha stored text", cancellationToken: Ct));
        Assert.Equal(true, hit.Metadata["b"]);
        Assert.Equal(StoredValues.FromStore(StoredValues.ToStore(Date, Dto), Dto), hit.Metadata["d"]);
        Assert.Equal(7, hit.Metadata["i"]);

        var raw = Assert.Single(await store.GetDynamicCollection("sv_documents", RecordDefinitions.Manifests(o)).GetAsync(r => (int)r["ChunkCount"]! >= 0, 10, cancellationToken: Ct).ToListAsync(Ct));
        var manifest = ManifestRecord.FromDynamic(raw, o);
        Assert.Equal(true, manifest.Metadata["b"]);
        Assert.IsType<DateTimeOffset>(manifest.Metadata["d"]);
        Assert.Equal(7, manifest.Metadata["i"]);
    }

    [Fact]
    public async Task Date_filters_match_values_with_finer_ticks()
    {
        var rag = Pipeline().Builder.Build();
        await rag.IngestAsync(Doc(), cancellationToken: Ct);

        foreach (var filter in new[] { RagFilter.Eq("d", Date), RagFilter.In("d", [Date]) })
        {
            var hits = await rag.RetrieveAsync("alpha stored text", new RetrievalOptions { Filter = filter }, Ct);
            Assert.Equal("doc", Assert.Single(hits).DocumentId);
        }
    }

    [Fact]
    public void Bool_filters_compare_stored_ints()
    {
        var schema = new MetadataSchema().Add<bool>("b");
        var expr = RagFilterTranslator.Translate(RagFilter.Eq("b", true), schema, out _)!;
        var eq = Assert.IsAssignableFrom<BinaryExpression>(expr.Body);
        var read = Assert.IsAssignableFrom<UnaryExpression>(eq.Left);
        Assert.Equal(ExpressionType.Convert, read.NodeType);
        Assert.Equal(typeof(int?), read.Type);
        var constant = Assert.IsAssignableFrom<ConstantExpression>(eq.Right);
        Assert.Equal(typeof(int?), constant.Type);
        Assert.Equal(1, constant.Value);
    }
}
