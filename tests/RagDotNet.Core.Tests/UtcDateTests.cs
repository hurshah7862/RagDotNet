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
/// Metadata dates reach every store in UTC: some stores (PostgreSQL through the PgVector connector) reject any other
/// offset, and the instant is what matters.
/// </summary>
public sealed class UtcDateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Local = new(2026, 1, 1, 10, 30, 15, 123, TimeSpan.FromHours(5.5));

    private static RagOptions Options()
    {
        var o = new RagOptions { CollectionName = "utc", EmbeddingDimensions = 16 };
        o.Metadata.Add<DateTimeOffset>("d");
        return o;
    }

    private static (InMemoryVectorStore Store, Pipeline.IRagPipeline Rag) Pipeline()
    {
        var store = new InMemoryVectorStore();
        var rag = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(16))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "utc", 16)
            .Configure(o => o.Metadata.Add<DateTimeOffset>("d"))
            .Build();
        return (store, rag);
    }

    private static DocumentSource Doc() =>
        new("doc", new MemoryStream("alpha dated text"u8.ToArray()), "doc.md", "text/markdown") { Metadata = { ["d"] = Local } };

    [Fact]
    public async Task Metadata_dates_are_stored_in_utc()
    {
        var (_, rag) = Pipeline();
        await rag.IngestAsync(Doc(), cancellationToken: Ct);

        var hit = Assert.Single(await rag.RetrieveAsync("alpha dated text", cancellationToken: Ct));

        var d = Assert.IsType<DateTimeOffset>(hit.Metadata["d"]);
        Assert.Equal(TimeSpan.Zero, d.Offset);
        Assert.Equal(Local.UtcTicks, d.UtcTicks);
    }

    [Fact]
    public async Task Date_filter_values_are_compared_in_utc()
    {
        var (_, rag) = Pipeline();
        await rag.IngestAsync(Doc(), cancellationToken: Ct);

        foreach (var value in new object[] { Local.ToOffset(TimeSpan.FromHours(-7)), "2026-01-01T05:00:15.123Z" })
        {
            var hits = await rag.RetrieveAsync("alpha dated text", new RetrievalOptions { Filter = RagFilter.Eq("d", value) }, Ct);
            Assert.Equal("doc", Assert.Single(hits).DocumentId);
        }

        foreach (var value in new object[] { Local, "2026-01-01T10:30:15.123+05:30", Local.LocalDateTime })
        {
            var expr = RagFilterTranslator.Translate(RagFilter.Eq("d", value), Options().Metadata, out _)!;
            var constant = Assert.IsAssignableFrom<ConstantExpression>(((BinaryExpression)expr.Body).Right);
            Assert.Equal(StoredValues.ToStore(Local, typeof(DateTimeOffset)), constant.Value); // the instant, as stored
        }
    }

    [Fact]
    public async Task Manifest_dates_are_stored_in_utc()
    {
        var (store, rag) = Pipeline();
        await rag.IngestAsync(Doc(), cancellationToken: Ct);

        var o = Options();
        var manifests = store.GetDynamicCollection("utc_documents", RecordDefinitions.Manifests(o));
        var manifest = Assert.Single(await manifests.GetAsync(r => true, 10, cancellationToken: Ct).ToListAsync(Ct));

        var stored = Assert.IsType<long>(manifest[RecordDefinitions.MetaName("d")]); // Unix microseconds, see StoredValues
        var d = Assert.IsType<DateTimeOffset>(StoredValues.FromStore(stored, typeof(DateTimeOffset)));
        Assert.Equal(TimeSpan.Zero, d.Offset);
        Assert.Equal(Local.UtcTicks, d.UtcTicks);
    }
}
