using System.Diagnostics;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Diagnostics;
using RagDotNet.Embedding;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using RagDotNet.Storage;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class ReindexCoordinationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }

    /// <summary>Seeds d1 ("alpha one") with model m1 and returns the store.</summary>
    private static async Task<InMemoryVectorStore> SeedM1Async()
    {
        var (seed, _, _, _, store) = Create(o => o.EmbeddingModelId = "m1");
        await seed.IngestAsync(Md("d1", "alpha one"), cancellationToken: Ct);
        return store;
    }

    private static RagPipelineBuilder Builder(
        InMemoryVectorStore store, string model, TimeSpan? interval = null, TimeProvider? time = null, ILoggerFactory? loggers = null)
    {
        var b = new RagPipelineBuilder()
            .UseEmbeddingGenerator(new FakeEmbeddingGenerator(64))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(store, "test", 64)
            .Configure(o =>
            {
                o.EmbeddingModelId = model;
                if (interval is { } i) o.ModelCheckInterval = i;
            });
        if (time is not null) b.UseTimeProvider(time);
        if (loggers is not null) b.UseLoggerFactory(loggers);
        return b;
    }

    private static ManifestStore Manifests(IRagPipeline rag) => ((RagPipeline)rag).Engine.Manifests;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Marker_for_the_configured_model_limits_reads_to_reembedded_data_and_allows_writes()
    {
        var store = await SeedM1Async();
        var rag = Builder(store, "m2").Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", NowMs), Ct);

        var r = await rag.IngestAsync(Md("d2", "alpha two"), cancellationToken: Ct);
        Assert.Equal(IngestStatus.Created, r.Status);
        var hits = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d2"], hits.Select(h => h.DocumentId)); // d1 is still on m1: skipped, never compared
        Assert.True(await rag.DeleteDocumentAsync("d2", Ct));
    }

    [Theory]
    [InlineData(RagKeyType.String)]
    [InlineData(RagKeyType.Guid)]
    public async Task Marker_round_trips_and_deletes(RagKeyType keyType)
    {
        var rag = Builder(new InMemoryVectorStore(), "m2").Configure(o => o.KeyType = keyType).Build();
        await rag.IngestAsync(Md("d1", "alpha one"), cancellationToken: Ct); // creates the collections
        var manifests = Manifests(rag);
        Assert.Null(await manifests.GetMarkerAsync(Ct));

        var marker = new ReindexMarker("m2", "owner", 1234);
        await manifests.UpsertMarkerAsync(marker, Ct);
        Assert.Equal(marker, await manifests.GetMarkerAsync(Ct));
        await manifests.DeleteMarkerAsync(Ct);
        Assert.Null(await manifests.GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task FindOtherModel_never_reports_the_marker()
    {
        var rag = Builder(new InMemoryVectorStore(), "m2").Build();
        await rag.IngestAsync(Md("d1", "alpha one"), cancellationToken: Ct);
        var manifests = Manifests(rag);
        await manifests.UpsertMarkerAsync(new ReindexMarker("m3", "owner", NowMs), Ct);

        Assert.False((await manifests.FindOtherModelAsync("m2", Ct)).Found);
    }

    [Fact]
    public async Task Reads_during_reindex_log_one_warning()
    {
        var store = await SeedM1Async();
        var logger = new FakeLogger();
        var rag = Builder(store, "m2", loggers: new SingleLoggerFactory(logger)).Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", NowMs), Ct);

        await rag.RetrieveAsync("alpha", cancellationToken: Ct);
        await rag.RetrieveAsync("alpha", cancellationToken: Ct);

        var warnings = logger.Collector.GetSnapshot().Where(e => e.Level == LogLevel.Warning).ToList();
        var w = Assert.Single(warnings);
        Assert.Contains("re-embedded", w.Message);
        Assert.Contains("m2", w.Message);
    }

    [Fact]
    public async Task Reads_during_reindex_tag_the_retrieve_span()
    {
        var store = await SeedM1Async();
        var rag = Builder(store, "m2").Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", NowMs), Ct);

        using var testSource = new ActivitySource("RagDotNet.Tests.Reindex");
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RagTelemetry.ActivitySourceName || s.Name == testSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        using var root = testSource.StartActivity("test")!;

        await rag.RetrieveAsync("alpha", cancellationToken: Ct);

        Activity span;
        lock (spans) span = spans.Single(a => a.TraceId == root.TraceId && a.OperationName == "rag.retrieve");
        Assert.Equal(true, span.GetTagItem("rag.reindex.in_progress"));
    }

    [Fact]
    public async Task Marker_for_another_model_fails_every_call_with_mismatch()
    {
        var store = await SeedM1Async();
        var rag = Builder(store, "m2").Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m3", "other-owner", NowMs), Ct);

        var ex = await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        Assert.Equal("m3", ex.StoredModelId);
        Assert.Equal("m2", ex.ConfiguredModelId);
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.IngestAsync(Md("d2", "alpha two"), cancellationToken: Ct));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.DeleteDocumentAsync("d1", Ct));
    }

    [Fact]
    public async Task Marker_is_not_counted_as_a_document()
    {
        var store = await SeedM1Async();
        var builder = Builder(store, "m2");
        var rag = builder.Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", NowMs), Ct);

        var stats = await builder.BuildAdmin().GetStatsAsync(Ct);

        var partition = Assert.Single(stats.Partitions);
        Assert.Equal(1, partition.DocumentCount);
        Assert.Empty(partition.Partition);
        Assert.Equal(["m1"], stats.EmbeddingModelIds);
    }

    [Fact]
    public async Task Cached_state_expires_after_ModelCheckInterval()
    {
        var store = await SeedM1Async();
        var time = new ManualTimeProvider(Start);
        var rag = Builder(store, "m2", TimeSpan.FromSeconds(5), time).Build();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));

        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", Start.ToUnixTimeMilliseconds()), Ct);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct)); // nothing on m2 yet
    }

    [Fact]
    public async Task Backward_clock_step_refreshes_the_cached_state()
    {
        var store = await SeedM1Async();
        var time = new ManualTimeProvider(Start);
        var rag = Builder(store, "m2", TimeSpan.FromSeconds(5), time).Build();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));

        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", Start.ToUnixTimeMilliseconds()), Ct);

        time.Advance(TimeSpan.FromHours(-1)); // elapsed is negative: must count as expired, not as "fresh for an hour"
        Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public async Task Zero_interval_checks_every_call()
    {
        var store = await SeedM1Async();
        var rag = Builder(store, "m2", TimeSpan.Zero, new ManualTimeProvider(Start)).Build();
        await Assert.ThrowsAsync<EmbeddingModelMismatchException>(() => rag.RetrieveAsync("alpha", cancellationToken: Ct));

        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "other-owner", NowMs), Ct);

        Assert.Empty(await rag.RetrieveAsync("alpha", cancellationToken: Ct));
    }

    [Fact]
    public void Default_interval_is_five_seconds() =>
        Assert.Equal(TimeSpan.FromSeconds(5), new RagOptions().ModelCheckInterval);

    [Fact]
    public void Negative_interval_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Create(o => o.ModelCheckInterval = TimeSpan.FromSeconds(-1)));
        Assert.Contains("ModelCheckInterval must not be negative.", ex.Failures);
    }

    [Fact]
    public void UseTimeProvider_respects_freezing()
    {
        var (_, builder, _, _, _) = Create();
        Assert.Throws<InvalidOperationException>(() => builder.UseTimeProvider(TimeProvider.System));
    }
}
