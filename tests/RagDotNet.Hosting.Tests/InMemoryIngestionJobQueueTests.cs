using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Runtime.CompilerServices;
using RagDotNet.Access;
using RagDotNet.Filtering;
using RagDotNet.Configuration;
using RagDotNet.Core.Tests.Fakes;
using RagDotNet.Documents;
using RagDotNet.Ingestion;
using RagDotNet.Pipeline;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Hosting.Tests;

/// <summary>Fails its first <paramref name="failures"/> calls with <see cref="HttpRequestException"/>, like a rate-limited provider.</summary>
internal sealed class FlakyEmbeddingGenerator(int failures) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly FakeEmbeddingGenerator _inner = new(64);
    private int _calls;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Interlocked.Increment(ref _calls) <= failures
            ? throw new HttpRequestException("429 Too Many Requests")
            : _inner.GenerateAsync(values, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}

public sealed class InMemoryIngestionJobQueueTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ragdotnet-hosting-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new();
    private readonly List<InMemoryIngestionJobQueue> _queues = [];

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        foreach (var q in _queues)
        {
            await q.StopAsync(CancellationToken.None);
            await q.DisposeAsync();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed record Setup(InMemoryIngestionJobQueue Queue, IRagAdmin Admin);

    private async Task<Setup> StartAsync(
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null, Action<IngestionQueueOptions>? configure = null, Action<RagOptions>? rag = null)
    {
        var builder = new RagPipelineBuilder()
            .UseEmbeddingGenerator(embedder ?? new FakeEmbeddingGenerator(64))
            .UseChatClient(new FakeChatClient())
            .UseVectorStore(new InMemoryVectorStore(), "test", 64);
        if (rag is not null) builder.Configure(rag);
        var admin = builder.BuildAdmin();
        var options = new IngestionQueueOptions { TempDirectory = _dir };
        configure?.Invoke(options);
        var wrapped = Options.Create(options);
        var queue = new InMemoryIngestionJobQueue(
            new IngestionJobRunner(admin, wrapped), wrapped, _time, NullLogger<InMemoryIngestionJobQueue>.Instance);
        _queues.Add(queue);
        await queue.StartAsync(Ct);
        return new Setup(queue, admin);
    }

    private static GatedEmbeddingGenerator Gated(string word = "") =>
        new(new FakeEmbeddingGenerator(64), batch => batch.Any(t => t.Contains(word, StringComparison.Ordinal)));

    private static IngestionJob Job(string id, string documentId, string text = "alpha beta gamma") => new(id, Md(documentId, text), null);

    private static async Task<IngestionJobStatus> WaitForStateAsync(
        IIngestionJobQueue queue, string jobId, IngestionJobState state, Func<IngestionJobStatus, bool>? also = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var status = await queue.GetStatusAsync(jobId, Ct);
            if (status is not null && status.State == state && (also is null || also(status))) return status;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Job {jobId} did not reach {state}; it is {status?.State.ToString() ?? "unknown"} ({status?.Error}).");
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<int> DocumentCountAsync(IRagAdmin admin) => (await admin.GetStatsAsync(Ct)).Partitions.Sum(p => p.DocumentCount);

    private string[] TempFiles(InMemoryIngestionJobQueue queue) =>
        Directory.Exists(queue.TempDirectory) ? Directory.GetFiles(queue.TempDirectory) : [];

    [Fact]
    public async Task Enqueued_job_runs_and_succeeds()
    {
        var (queue, admin) = await StartAsync();

        await queue.EnqueueAsync(Job("j1", "d"), Ct);

        var status = await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
        Assert.Equal(1, status.Attempts);
        Assert.Equal("d", status.DocumentId);
        Assert.Equal(IngestStatus.Created, status.Result!.Status);
        Assert.True(status.Result.ChunkCount > 0);
        Assert.Null(status.Error);
        Assert.Equal("d", Assert.Single(await admin.ForScope(null).RetrieveAsync("alpha", cancellationToken: Ct)).DocumentId);
        Assert.Empty(TempFiles(queue));
    }

    [Fact]
    public async Task Unknown_job_has_no_status()
    {
        var (queue, _) = await StartAsync();

        Assert.Null(await queue.GetStatusAsync("nope", Ct));
        Assert.False(await queue.CancelAsync("nope", Ct));
    }

    [Fact]
    public async Task Job_succeeds_after_the_caller_disposes_its_stream()
    {
        var (queue, _) = await StartAsync();
        var source = Md("d", "alpha beta gamma");

        await queue.EnqueueAsync(new IngestionJob("j1", source, null), Ct);
        await source.Stream!.DisposeAsync();

        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task File_source_is_read_from_its_path()
    {
        var gated = Gated();
        var (queue, _) = await StartAsync(gated);
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "doc.md");
        await File.WriteAllTextAsync(path, "# Doc\n\nalpha beta gamma", Ct);

        await queue.EnqueueAsync(new IngestionJob("j1", DocumentSource.FromFile(path, "d"), null), Ct);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(TempFiles(queue)); // no copy while it runs
        gated.Gate.SetResult();

        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task Transient_errors_retry_with_backoff()
    {
        var (queue, _) = await StartAsync(new FlakyEmbeddingGenerator(2));

        await queue.EnqueueAsync(Job("j1", "d"), Ct);

        var first = await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 1);
        Assert.StartsWith("HttpRequestException:", first.Error);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 2);
        _time.Advance(TimeSpan.FromSeconds(2));
        var done = await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
        Assert.Equal(3, done.Attempts);
    }

    [Fact]
    public async Task Backoff_waits_the_full_delay()
    {
        var (queue, _) = await StartAsync(new FlakyEmbeddingGenerator(1));

        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 1);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(200, Ct);

        Assert.Equal(1, (await queue.GetStatusAsync("j1", Ct))!.Attempts);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task Transient_errors_fail_after_max_attempts()
    {
        var (queue, _) = await StartAsync(new FlakyEmbeddingGenerator(int.MaxValue));

        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 1);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 2);
        _time.Advance(TimeSpan.FromSeconds(2));

        var failed = await WaitForStateAsync(queue, "j1", IngestionJobState.Failed);
        Assert.Equal(3, failed.Attempts);
        Assert.StartsWith("HttpRequestException:", failed.Error);
        Assert.Empty(TempFiles(queue));
    }

    [Fact]
    public async Task Permanent_error_fails_after_one_attempt()
    {
        var (queue, _) = await StartAsync();
        var source = new DocumentSource("d", new MemoryStream([1, 2, 3]), "d.xyz");

        await queue.EnqueueAsync(new IngestionJob("j1", source, null), Ct);

        var failed = await WaitForStateAsync(queue, "j1", IngestionJobState.Failed);
        Assert.Equal(1, failed.Attempts);
        Assert.StartsWith("UnsupportedDocumentException:", failed.Error);
    }

    [Fact]
    public async Task Cancel_queued_job_never_runs()
    {
        var (queue, admin) = await StartAsync();
        await queue.PauseAsync(Ct);

        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        Assert.True(await queue.CancelAsync("j1", Ct));
        Assert.Equal(IngestionJobState.Cancelled, (await queue.GetStatusAsync("j1", Ct))!.State);
        Assert.Empty(TempFiles(queue));
        await queue.ResumeAsync(Ct);

        await queue.EnqueueAsync(Job("j2", "other"), Ct); // runs after j1 was dequeued
        await WaitForStateAsync(queue, "j2", IngestionJobState.Succeeded);
        var j1 = (await queue.GetStatusAsync("j1", Ct))!;
        Assert.Equal(IngestionJobState.Cancelled, j1.State);
        Assert.Equal(0, j1.Attempts);
        Assert.Equal(1, await DocumentCountAsync(admin));
    }

    [Fact]
    public async Task Cancel_running_job_ends_cancelled()
    {
        var gated = Gated();
        var (queue, _) = await StartAsync(gated);

        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(IngestionJobState.Running, (await queue.GetStatusAsync("j1", Ct))!.State);
        Assert.True(await queue.CancelAsync("j1", Ct));

        var cancelled = await WaitForStateAsync(queue, "j1", IngestionJobState.Cancelled);
        Assert.Equal(1, cancelled.Attempts);
        await Task.Delay(200, Ct);
        Assert.Equal(IngestionJobState.Cancelled, (await queue.GetStatusAsync("j1", Ct))!.State);
        Assert.Empty(TempFiles(queue));
    }

    [Fact]
    public async Task Cancel_finished_job_returns_false()
    {
        var (queue, _) = await StartAsync();
        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);

        Assert.False(await queue.CancelAsync("j1", Ct));
        Assert.Equal(IngestionJobState.Succeeded, (await queue.GetStatusAsync("j1", Ct))!.State);
    }

    [Fact]
    public async Task Pause_holds_jobs_and_resume_runs_them()
    {
        var (queue, admin) = await StartAsync();
        await queue.PauseAsync(Ct);

        for (var i = 0; i < 3; i++) await queue.EnqueueAsync(Job("j" + i, "d" + i), Ct);
        await Task.Delay(500, Ct);
        for (var i = 0; i < 3; i++)
            Assert.Equal(IngestionJobState.Queued, (await queue.GetStatusAsync("j" + i, Ct))!.State);
        Assert.Equal(0, await DocumentCountAsync(admin));

        await queue.ResumeAsync(Ct);
        for (var i = 0; i < 3; i++) await WaitForStateAsync(queue, "j" + i, IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task Pause_returns_when_running_jobs_finish()
    {
        var gated = Gated("first");
        var (queue, _) = await StartAsync(gated);
        await queue.EnqueueAsync(Job("j1", "d1", "first alpha"), Ct);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var pause = queue.PauseAsync(Ct).AsTask();
        await Task.Delay(200, Ct);
        Assert.False(pause.IsCompleted);
        await queue.EnqueueAsync(Job("j2", "d2", "second bravo"), Ct);
        gated.Gate.SetResult();
        await pause.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(IngestionJobState.Succeeded, (await queue.GetStatusAsync("j1", Ct))!.State);
        await Task.Delay(200, Ct);
        Assert.Equal(IngestionJobState.Queued, (await queue.GetStatusAsync("j2", Ct))!.State);
    }

    [Fact]
    public async Task Concurrency_limits_running_jobs()
    {
        var gated = Gated();
        var (queue, _) = await StartAsync(gated, o => o.Concurrency = 2);

        for (var i = 0; i < 4; i++) await queue.EnqueueAsync(Job("j" + i, "d" + i), Ct);
        async Task<int> RunningAsync()
        {
            var n = 0;
            for (var i = 0; i < 4; i++)
                if ((await queue.GetStatusAsync("j" + i, Ct))!.State == IngestionJobState.Running) n++;
            return n;
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await RunningAsync() < 2 && DateTime.UtcNow < deadline) await Task.Delay(10, Ct);
        for (var k = 0; k < 20; k++)
        {
            Assert.Equal(2, await RunningAsync());
            await Task.Delay(10, Ct);
        }

        gated.Gate.SetResult();
        for (var i = 0; i < 4; i++) await WaitForStateAsync(queue, "j" + i, IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task Full_queue_waits_and_honours_cancellation()
    {
        var gated = Gated();
        var (queue, _) = await StartAsync(gated, o => { o.Capacity = 1; o.Concurrency = 1; });
        await queue.EnqueueAsync(Job("a", "da"), Ct);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct); // A is running, so the channel is empty
        await queue.EnqueueAsync(Job("b", "db"), Ct);                 // B fills it

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.EnqueueAsync(Job("c", "dc"), cts.Token).AsTask());

        Assert.Null(await queue.GetStatusAsync("c", Ct));
        Assert.Equal(2, TempFiles(queue).Length); // A's and B's copies; none for C
        gated.Gate.SetResult();
        await WaitForStateAsync(queue, "b", IngestionJobState.Succeeded);
    }

    [Fact]
    public async Task Finished_status_expires_after_retention()
    {
        var (queue, _) = await StartAsync();
        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);

        _time.Advance(new IngestionQueueOptions().StatusRetention + TimeSpan.FromSeconds(1));

        Assert.Null(await queue.GetStatusAsync("j1", Ct));
    }

    [Fact]
    public async Task Stop_cancels_running_jobs_and_leaves_no_temp_files()
    {
        var gated = Gated();
        var (queue, _) = await StartAsync(gated, o => o.Concurrency = 1);
        await queue.EnqueueAsync(Job("j1", "d1"), Ct);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await queue.EnqueueAsync(Job("j2", "d2"), Ct);
        await queue.EnqueueAsync(Job("j3", "d3"), Ct);
        Assert.NotEmpty(TempFiles(queue));

        await queue.StopAsync(Ct);

        Assert.False(Directory.Exists(queue.TempDirectory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(Job("j4", "d4"), Ct).AsTask());
    }

    [Fact]
    public async Task Two_jobs_for_one_document_leave_one_document()
    {
        var (queue, admin) = await StartAsync(configure: o => o.Concurrency = 2);

        await queue.EnqueueAsync(Job("j1", "d", "alpha"), Ct);
        await queue.EnqueueAsync(Job("j2", "d", "bravo"), Ct);

        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
        await WaitForStateAsync(queue, "j2", IngestionJobState.Succeeded);
        Assert.Equal(1, await DocumentCountAsync(admin));
    }

    [Fact]
    public async Task Duplicate_job_id_is_refused()
    {
        var (queue, _) = await StartAsync();
        await queue.PauseAsync(Ct);
        await queue.EnqueueAsync(Job("j1", "d"), Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(Job("j1", "e"), Ct).AsTask());
        Assert.Single(TempFiles(queue));
    }

    [Fact]
    public async Task Throwing_IsTransient_fails_the_job_and_keeps_the_worker()
    {
        var (queue, _) = await StartAsync(new FlakyEmbeddingGenerator(1), o =>
        {
            o.Concurrency = 1;
            o.IsTransient = _ => throw new InvalidCastException("bad classifier");
        });

        await queue.EnqueueAsync(Job("j1", "d1"), Ct);
        var failed = await WaitForStateAsync(queue, "j1", IngestionJobState.Failed);
        Assert.Equal(1, failed.Attempts);
        await queue.EnqueueAsync(Job("j2", "d2"), Ct);

        await WaitForStateAsync(queue, "j2", IngestionJobState.Succeeded); // the only worker is still alive
    }

    [Fact]
    public async Task Retry_delay_is_capped_at_one_day()
    {
        var (queue, _) = await StartAsync(new FlakyEmbeddingGenerator(1), o => o.RetryDelay = TimeSpan.FromDays(60));

        await queue.EnqueueAsync(Job("j1", "d"), Ct);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Queued, s => s.Attempts == 1);
        _time.Advance(TimeSpan.FromDays(1));

        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> EnqueueUnreferencedStreamAsync(InMemoryIngestionJobQueue queue)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("# Doc\n\nalpha beta gamma"));
        await queue.EnqueueAsync(new IngestionJob("j1", new DocumentSource("d", stream, "d.md"), null), Ct);
        return new WeakReference(stream);
    }

    [Fact]
    public async Task Queue_does_not_keep_the_callers_stream()
    {
        var (queue, _) = await StartAsync();

        var stream = await EnqueueUnreferencedStreamAsync(queue);
        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(stream.IsAlive);
    }

    [Fact]
    public async Task Metadata_and_scope_are_captured_at_enqueue()
    {
        var (queue, admin) = await StartAsync(rag: o =>
        {
            o.Metadata.Add<string>("tenant");
            o.Metadata.Add<string>("tag");
            o.PartitionKeys.Add("tenant");
        });
        await queue.PauseAsync(Ct);
        var partition = new Dictionary<string, string> { ["tenant"] = "a" };
        var source = Md("d", "alpha beta gamma", new Dictionary<string, object?> { ["tag"] = "x" });

        await queue.EnqueueAsync(new IngestionJob("j1", source, new AccessScope(partition)), Ct);
        partition["tenant"] = "b";
        source.Metadata["tag"] = "y";
        await queue.ResumeAsync(Ct);

        await WaitForStateAsync(queue, "j1", IngestionJobState.Succeeded);
        var hits = await admin.ForScope(TestScopes.Tenant("a", RagFilter.Eq("tag", "x"))).RetrieveAsync("alpha", cancellationToken: Ct);
        Assert.Equal("d", Assert.Single(hits).DocumentId);
    }

    [Fact]
    public async Task Retry_does_not_overwrite_a_newer_upload()
    {
        var (queue, admin) = await StartAsync(new FlakyEmbeddingGenerator(1));

        await queue.EnqueueAsync(Job("v1", "d", "alpha one"), Ct);
        await WaitForStateAsync(queue, "v1", IngestionJobState.Queued, s => s.Attempts == 1); // failed once, waiting to retry
        await queue.EnqueueAsync(Job("v2", "d", "bravo two"), Ct);
        await WaitForStateAsync(queue, "v2", IngestionJobState.Succeeded);
        _time.Advance(TimeSpan.FromSeconds(1));

        var v1 = await WaitForStateAsync(queue, "v1", IngestionJobState.Cancelled);
        Assert.Contains("v2", v1.Error);
        var hit = Assert.Single(await admin.ForScope(null).RetrieveAsync("bravo", cancellationToken: Ct));
        Assert.Contains("bravo", hit.Text);
    }
}
