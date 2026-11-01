using System.Diagnostics;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
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
        VectorStore store, string model, TimeSpan? interval = null, TimeProvider? time = null, ILoggerFactory? loggers = null,
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null)
    {
        var b = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder ?? new FakeEmbeddingGenerator(64))
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
    public void Interval_over_one_minute_fails_validation()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Create(o => o.ModelCheckInterval = TimeSpan.FromMinutes(1).Add(TimeSpan.FromTicks(1))));
        Assert.Contains("ModelCheckInterval must not exceed 1 minute.", ex.Failures);
        Create(o => o.ModelCheckInterval = TimeSpan.FromMinutes(1)); // exactly one minute is allowed
    }

    [Fact]
    public void UseTimeProvider_respects_freezing()
    {
        var (_, builder, _, _, _) = Create();
        Assert.Throws<InvalidOperationException>(() => builder.UseTimeProvider(TimeProvider.System));
    }

    // ---- Reindex coordination (marker lifecycle, document locks, re-read before write) ----

    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    private static RagEngine Engine(IRagPipeline rag) => ((RagPipeline)rag).Engine;

    private static Func<IReadOnlyList<string>, bool> Has(string text) => batch => batch.Any(t => t.Contains(text, StringComparison.Ordinal));

    /// <summary>Seeds d1 ("alpha one") and d2 ("alpha two") on m1.</summary>
    private static async Task<InMemoryVectorStore> SeedTwoAsync()
    {
        var store = await SeedM1Async();
        await Builder(store, "m1").Build().IngestAsync(Md("d2", "alpha two"), cancellationToken: Ct);
        return store;
    }

    private static async Task WithinAsync(Task task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Bounded, Ct));
        Assert.True(done == task, "Timed out waiting for the task.");
        await task;
    }

    private static async Task<T> WithinAsync<T>(Task<T> task)
    {
        await WithinAsync((Task)task);
        return await task;
    }

    private static async Task<List<string>> ChunkModelsAsync(InMemoryVectorStore store)
    {
        var chunks = new ChunkStore(store, new RagOptions { CollectionName = "test", EmbeddingDimensions = 64 });
        var records = await chunks.GetAsync([ChunkKeys.Chunk("", "d1", 0), ChunkKeys.Chunk("", "d2", 0)], false, Ct);
        return records.Select(r => (string)r["EmbeddingModelId"]!).ToList();
    }

    /// <summary>Throws an IOException for any batch the predicate selects.</summary>
    private sealed class FailingEmbedder(IEmbeddingGenerator<string, Embedding<float>> inner, Func<IReadOnlyList<string>, bool> fail)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = values.ToList();
            if (fail(list)) throw new IOException("Simulated embedder failure.");
            return inner.GenerateAsync(list, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose() { }
    }

    /// <summary>
    /// Clock whose timers fire only when the test calls <see cref="CapturedTimer.Fire"/>. The system clock by default;
    /// with a start time, a manual clock moved by <see cref="Advance"/>.
    /// </summary>
    private sealed class TimerCaptureTimeProvider(DateTimeOffset? start = null) : TimeProvider
    {
        private readonly TaskCompletionSource<CapturedTimer> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ticks = start?.UtcTicks ?? 0;

        public Task<CapturedTimer> TimerCreated => _created.Task;

        public override DateTimeOffset GetUtcNow() =>
            start is null ? base.GetUtcNow() : new DateTimeOffset(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new CapturedTimer(callback, state, dueTime);
            _created.TrySetResult(timer);
            return timer;
        }
    }

    private sealed class CapturedTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; } = dueTime;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Reads_during_reindex_return_reembedded_documents_then_all()
    {
        var store = await SeedTwoAsync();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha two"));
        var builder = Builder(store, "m2", TimeSpan.Zero, embedder: gate);
        var rag = builder.Build();
        var admin = builder.BuildAdmin();

        var reindex = admin.ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);

        var during = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d1"], during.Select(h => h.DocumentId).Distinct());

        gate.Gate.SetResult();
        var result = await WithinAsync(reindex);
        Assert.Equal(2, result.DocumentsReindexed);

        var after = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d1", "d2"], after.Select(h => h.DocumentId).Distinct().Order());
        Assert.Null(await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Ingest_of_the_document_being_reindexed_waits_for_it()
    {
        var store = await SeedTwoAsync();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.Zero, embedder: gate);
        var rag = builder.Build();
        var admin = builder.BuildAdmin();

        var reindex = admin.ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);

        var ingest = rag.IngestAsync(Md("d1", "changed"), cancellationToken: Ct);
        Assert.NotSame(ingest, await Task.WhenAny(ingest, Task.Delay(100, Ct)));

        gate.Gate.SetResult();
        await WithinAsync(reindex);
        Assert.Equal(IngestStatus.Updated, (await WithinAsync(ingest)).Status);

        var hits = await rag.RetrieveAsync("changed", new() { TopK = 10 }, Ct);
        Assert.Equal("changed", hits.Single(h => h.DocumentId == "d1").Text);
        Assert.Equal(["m2"], (await admin.GetStatsAsync(Ct)).EmbeddingModelIds);
    }

    [Fact]
    public async Task Write_from_another_process_during_reindex_is_not_overwritten()
    {
        var store = await SeedTwoAsync();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var a = Builder(store, "m2", TimeSpan.Zero, embedder: gate);
        var adminA = a.BuildAdmin();
        var b = Builder(store, "m2", TimeSpan.Zero).Build();

        var reindex = adminA.ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        Assert.Equal(IngestStatus.Updated, (await b.IngestAsync(Md("d1", "from B"), cancellationToken: Ct)).Status);

        gate.Gate.SetResult();
        await WithinAsync(reindex);

        var hits = await b.RetrieveAsync("from B", new() { TopK = 10 }, Ct);
        Assert.Equal("from B", hits.Single(h => h.DocumentId == "d1").Text);
        Assert.Equal(["m2"], (await adminA.GetStatsAsync(Ct)).EmbeddingModelIds);
        Assert.Null(await Manifests(b).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Document_moved_back_to_the_old_model_during_reindex_leaves_the_marker_and_asks_for_a_rerun()
    {
        var store = await SeedTwoAsync();
        var logger = new FakeLogger();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var a = Builder(store, "m2", TimeSpan.Zero, loggers: new SingleLoggerFactory(logger), embedder: gate);
        var adminA = a.BuildAdmin();
        var ragA = a.Build();
        // An old instance that has not restarted and whose cached "all on m1" state has not expired.
        var old = Builder(store, "m1", TimeSpan.FromMinutes(1)).Build();
        await old.RetrieveAsync("alpha", cancellationToken: Ct);

        var reindex = adminA.ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        await old.IngestAsync(Md("d1", "from old"), cancellationToken: Ct);
        gate.Gate.SetResult();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(reindex));
        Assert.Equal("Some documents changed during reindex and are not yet on the configured model. Run ReindexAsync again.", ex.Message);
        var marker = await Manifests(ragA).GetMarkerAsync(Ct);
        Assert.Equal(("m2", Engine(ragA).InstanceId), (marker!.TargetModelId, marker.OwnerId));
        Assert.Contains(logger.Collector.GetSnapshot(),
            e => e.Level == LogLevel.Warning && e.Message == "Document d1 changed during reindex; skipped.");

        var rerun = await adminA.ReindexAsync(cancellationToken: Ct);
        Assert.Equal(1, rerun.DocumentsReindexed);
        Assert.Null(await Manifests(ragA).GetMarkerAsync(Ct));
        var hits = await ragA.RetrieveAsync("from old", new() { TopK = 10 }, Ct);
        Assert.Equal("from old", hits.Single(h => h.DocumentId == "d1").Text);
    }

    [Fact]
    public async Task Cancelled_reindex_resumes_immediately_in_the_same_process()
    {
        var store = await SeedTwoAsync();
        var paused = 0;
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64),
            batch => Has("alpha two")(batch) && Interlocked.Increment(ref paused) == 1);
        var builder = Builder(store, "m2", TimeSpan.Zero, embedder: gate);
        var rag = builder.Build();
        var admin = builder.BuildAdmin();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var first = admin.ReindexAsync(cancellationToken: cts.Token);
        await WithinAsync(gate.Reached);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithinAsync(first));

        Assert.Equal(Engine(rag).InstanceId, (await Manifests(rag).GetMarkerAsync(Ct))!.OwnerId);
        var during = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d1"], during.Select(h => h.DocumentId).Distinct());

        var second = await WithinAsync(admin.ReindexAsync(cancellationToken: Ct));
        Assert.Equal((1, 1), (second.DocumentsReindexed, second.DocumentsSkipped));
        var after = await rag.RetrieveAsync("alpha", new() { TopK = 10 }, Ct);
        Assert.Equal(["d1", "d2"], after.Select(h => h.DocumentId).Distinct().Order());
        Assert.Null(await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Fresh_foreign_marker_refuses_reindex_and_stale_one_is_taken_over()
    {
        var store = await SeedM1Async();
        var time = new ManualTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        var admin = builder.BuildAdmin();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "foreign", Start.ToUnixTimeMilliseconds()), Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(admin.ReindexAsync(cancellationToken: Ct)));
        Assert.Equal("Another reindex (to 'm2') holds the marker; it is considered abandoned after 5 minutes without a heartbeat.", ex.Message);
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ReindexAsync(cancellationToken: Ct)); // exactly 5 min: still fresh
        Assert.Equal("foreign", (await Manifests(rag).GetMarkerAsync(Ct))!.OwnerId);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await admin.ReindexAsync(cancellationToken: Ct);
        Assert.Equal(1, result.DocumentsReindexed);
        Assert.Null(await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Foreign_marker_with_a_heartbeat_in_the_future_counts_as_fresh()
    {
        var store = await SeedM1Async();
        var time = new ManualTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m3", "foreign", Start.ToUnixTimeMilliseconds()), Ct);

        time.Advance(TimeSpan.FromHours(-1)); // the clock stepped back: the heartbeat now lies in the future
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAdmin().ReindexAsync(cancellationToken: Ct));
        Assert.Contains("(to 'm3')", ex.Message);
        Assert.Equal(new ReindexMarker("m3", "foreign", Start.ToUnixTimeMilliseconds()), await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_reindex_in_one_process_is_refused()
    {
        var store = await SeedTwoAsync();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var admin = Builder(store, "m2", TimeSpan.Zero, embedder: gate).BuildAdmin();

        var first = admin.ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(admin.ReindexAsync(cancellationToken: Ct)));
        Assert.Equal("A reindex is already running in this process.", ex.Message);

        gate.Gate.SetResult();
        Assert.Equal(2, (await WithinAsync(first)).DocumentsReindexed);
    }

    [Fact]
    public async Task Dimension_mismatch_throws_before_the_marker_is_written()
    {
        var store = await SeedM1Async();
        var builder = Builder(store, "m2", TimeSpan.Zero, embedder: new FakeEmbeddingGenerator(32));
        var rag = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAdmin().ReindexAsync(cancellationToken: Ct));

        Assert.Null(await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Heartbeat_is_refreshed_after_60_seconds()
    {
        var store = await SeedTwoAsync();
        var time = new ManualTimeProvider(Start);
        var gate = new GatedEmbeddingGenerator(new FailingEmbedder(new FakeEmbeddingGenerator(64), Has("alpha two")), Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.Zero, time, embedder: gate);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), (await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs);
        time.Advance(TimeSpan.FromSeconds(61));
        gate.Gate.SetResult();
        await Assert.ThrowsAsync<IOException>(() => WithinAsync(reindex)); // d2 fails, so the marker stays for inspection

        Assert.Equal(new ReindexMarker("m2", Engine(rag).InstanceId, Start.AddSeconds(61).ToUnixTimeMilliseconds()),
            await Manifests(rag).GetMarkerAsync(Ct));
    }

    [Fact]
    public async Task Reindex_waits_one_check_interval_before_writing()
    {
        var store = await SeedM1Async();
        var time = new TimerCaptureTimeProvider();
        var builder = Builder(store, "m2", TimeSpan.FromMilliseconds(200), time);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        var timer = await WithinAsync(time.TimerCreated);

        Assert.Equal(TimeSpan.FromMilliseconds(200), timer.DueTime);
        Assert.Equal("m2", (await Manifests(rag).GetMarkerAsync(Ct))!.TargetModelId);
        Assert.Equal(["m1"], await ChunkModelsAsync(store)); // the marker is visible, no vector on m2 yet
        Assert.False(reindex.IsCompleted);

        timer.Fire();
        Assert.Equal(1, (await WithinAsync(reindex)).DocumentsReindexed);
        Assert.Equal(["m2"], await ChunkModelsAsync(store));
    }

    [Fact]
    public async Task Heartbeat_is_refreshed_after_the_pre_write_wait_when_60_seconds_passed()
    {
        var store = await SeedM1Async();
        var time = new TimerCaptureTimeProvider(Start);
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.FromMinutes(1), time, embedder: gate);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        var timer = await WithinAsync(time.TimerCreated);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), (await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs);

        time.Advance(TimeSpan.FromSeconds(60));
        timer.Fire();
        await WithinAsync(gate.Reached); // the first document is being re-embedded
        Assert.Equal(Start.AddSeconds(60).ToUnixTimeMilliseconds(), (await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs);

        gate.Gate.SetResult();
        Assert.Equal(1, (await WithinAsync(reindex)).DocumentsReindexed);
    }

    [Fact]
    public async Task Heartbeat_is_refreshed_between_batches_of_one_document()
    {
        var (seed, _, _, _, store) = Create(o => o.EmbeddingModelId = "m1");
        await seed.IngestAsync(Md("d1", "# A\n\nalpha one\n\n# B\n\nbeta two"), cancellationToken: Ct);
        var time = new ManualTimeProvider(Start);
        var second = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("beta two"));
        var first = new GatedEmbeddingGenerator(second, Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.Zero, time, embedder: first).Configure(o => o.EmbeddingBatchSize = 1);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        await WithinAsync(first.Reached);
        time.Advance(TimeSpan.FromSeconds(60));
        first.Gate.SetResult();
        await WithinAsync(second.Reached); // second batch of d1: the refresh must not wait for the document to finish
        Assert.Equal(Start.AddSeconds(60).ToUnixTimeMilliseconds(), (await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs);

        second.Gate.SetResult();
        var result = await WithinAsync(reindex);
        Assert.Equal((1, 2), (result.DocumentsReindexed, result.ChunksReindexed));
    }

    [Fact]
    public async Task Manifest_change_to_high_water_only_abandons_the_document()
    {
        var store = await SeedM1Async();
        var logger = new FakeLogger();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.Zero, loggers: new SingleLoggerFactory(logger), embedder: gate);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        // Another process's write leaves the same content hash and timestamp but a new high-water mark.
        var manifest = (await Manifests(rag).GetAsync("", "d1", Ct))!;
        await Manifests(rag).UpsertAsync(manifest with { ChunkHighWater = manifest.ChunkHighWater + 1 }, Ct);
        gate.Gate.SetResult();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(reindex));
        Assert.StartsWith("Some documents changed during reindex", ex.Message);
        Assert.Contains(logger.Collector.GetSnapshot(),
            e => e.Level == LogLevel.Warning && e.Message == "Document d1 changed during reindex; skipped.");
        var after = (await Manifests(rag).GetAsync("", "d1", Ct))!;
        Assert.Equal(("m1", manifest.ChunkHighWater + 1), (after.EmbeddingModelId, after.ChunkHighWater)); // the other write survives
    }

    [Fact]
    public async Task Manifest_change_to_chunking_hash_only_abandons_the_document()
    {
        var store = await SeedM1Async();
        var gate = new GatedEmbeddingGenerator(new FakeEmbeddingGenerator(64), Has("alpha one"));
        var builder = Builder(store, "m2", TimeSpan.Zero, embedder: gate);
        var rag = builder.Build();

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        await WithinAsync(gate.Reached);
        // Another process re-chunked with other options: same content hash, timestamp and counts, a new chunking hash.
        var manifest = (await Manifests(rag).GetAsync("", "d1", Ct))!;
        await Manifests(rag).UpsertAsync(manifest with { ChunkingHash = manifest.ChunkingHash + "-other" }, Ct);
        gate.Gate.SetResult();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(reindex));
        Assert.Equal("Some documents changed during reindex and are not yet on the configured model. Run ReindexAsync again.", ex.Message);
        Assert.Equal("m2", (await Manifests(rag).GetMarkerAsync(Ct))!.TargetModelId);
        var after = (await Manifests(rag).GetAsync("", "d1", Ct))!;
        Assert.Equal(("m1", manifest.ChunkingHash + "-other"), (after.EmbeddingModelId, after.ChunkingHash));
    }

    /// <summary>Polls the marker until its heartbeat equals <paramref name="expectedMs"/>, within the test bound.</summary>
    private static async Task HeartbeatReachesAsync(IRagPipeline rag, long expectedMs)
    {
        var deadline = Stopwatch.StartNew();
        while ((await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs != expectedMs)
        {
            Assert.True(deadline.Elapsed < Bounded, "Timed out waiting for the heartbeat refresh.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Heartbeat_is_refreshed_while_waiting_for_a_document_lock()
    {
        var store = await SeedM1Async();
        var time = new TimerCaptureTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        var held = await Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct); // an in-process ingest of d1

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        var timer = await WithinAsync(time.TimerCreated);
        Assert.Equal(TimeSpan.FromSeconds(60), timer.DueTime);
        Assert.Equal(Start.ToUnixTimeMilliseconds(), (await Manifests(rag).GetMarkerAsync(Ct))!.HeartbeatUnixMs);

        time.Advance(TimeSpan.FromSeconds(61));
        timer.Fire();
        await HeartbeatReachesAsync(rag, Start.AddSeconds(61).ToUnixTimeMilliseconds());
        Assert.False(reindex.IsCompleted); // still waiting for the lock

        held.Dispose();
        Assert.Equal(1, (await WithinAsync(reindex)).DocumentsReindexed);
        Assert.Equal(0, Engine(rag).DocumentLocks.Count);
    }

    [Fact]
    public async Task Takeover_while_waiting_for_a_document_lock_stops_the_wait_without_leaking_it()
    {
        var store = await SeedM1Async();
        var time = new TimerCaptureTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        var held = await Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct);

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        var timer = await WithinAsync(time.TimerCreated);
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "foreign", Start.ToUnixTimeMilliseconds()), Ct);
        time.Advance(TimeSpan.FromSeconds(61));
        timer.Fire();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(reindex));
        Assert.StartsWith("Another reindex (to 'm2') holds the marker", ex.Message);
        Assert.Equal(1, Engine(rag).DocumentLocks.Count); // only the held lock: the reindex waiter is gone
        held.Dispose();
        Assert.Equal(0, Engine(rag).DocumentLocks.Count);
        using var again = await WithinAsync(Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct).AsTask());
    }

    [Fact]
    public async Task Takeover_seen_after_the_document_lock_is_granted_releases_it()
    {
        var store = new FailingVectorStore(await SeedM1Async());
        var time = new TimerCaptureTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        var held = await Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct);

        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: Ct);
        var timer = await WithinAsync(time.TimerCreated); // the reindex is waiting for d1's lock
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeNextManifestGet = () =>
        {
            refreshing.SetResult();
            return proceed.Task;
        }; // the heartbeat refresh's marker read blocks here
        time.Advance(TimeSpan.FromSeconds(61));
        timer.Fire();
        await WithinAsync(refreshing.Task);

        held.Dispose(); // grants the reindex's pending acquire while its refresh is in flight
        await Manifests(rag).UpsertMarkerAsync(new ReindexMarker("m2", "foreign", Start.AddSeconds(61).ToUnixTimeMilliseconds()), Ct);
        proceed.SetResult();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WithinAsync(reindex));
        Assert.StartsWith("Another reindex (to 'm2') holds the marker", ex.Message);
        Assert.Equal(0, Engine(rag).DocumentLocks.Count); // the lock granted during the refresh was released
        using var again = await WithinAsync(Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct).AsTask());
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_a_document_lock_does_not_leak_it()
    {
        var store = await SeedM1Async();
        var time = new TimerCaptureTimeProvider(Start);
        var builder = Builder(store, "m2", TimeSpan.Zero, time);
        var rag = builder.Build();
        var held = await Engine(rag).DocumentLocks.AcquireAsync("", "d1", Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reindex = builder.BuildAdmin().ReindexAsync(cancellationToken: cts.Token);
        await WithinAsync(time.TimerCreated);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WithinAsync(reindex));
        Assert.Equal(1, Engine(rag).DocumentLocks.Count);
        held.Dispose();
        Assert.Equal(0, Engine(rag).DocumentLocks.Count);
    }
}
