using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using RagDotNet.Configuration;
using RagDotNet.Content;
using RagDotNet.Diagnostics;
using static RagDotNet.Core.Tests.Fakes.TestRag;

namespace RagDotNet.Core.Tests;

public class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records RagDotNet spans and measurements belonging to this test's own trace only (tests run in parallel).</summary>
    private sealed class Capture : IDisposable
    {
        private static readonly ActivitySource TestSource = new("RagDotNet.Tests");
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly Activity _root;
        private readonly object _gate = new();

        public Capture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = s => s.Name == RagTelemetry.ActivitySourceName || s.Name == "RagDotNet.Tests",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => { lock (_gate) Spans.Add(a); },
            };
            ActivitySource.AddActivityListener(_activities);
            _root = TestSource.StartActivity("test")!;

            _meters.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == RagTelemetry.MeterName) l.EnableMeasurementEvents(i);
            };
            _meters.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i.Name, v));
            _meters.SetMeasurementEventCallback<int>((i, v, _, _) => Add(i.Name, v));
            _meters.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i.Name, v));
            _meters.Start();
        }

        public List<Activity> Spans { get; } = [];

        public List<(string Name, double Value)> Measurements { get; } = [];

        private ActivityTraceId Trace => _root.TraceId;

        public IReadOnlyList<Activity> Mine
        {
            get { lock (_gate) return Spans.Where(a => a.TraceId == Trace && a.Source.Name == RagTelemetry.ActivitySourceName).ToList(); }
        }

        public Activity Span(string name) => Mine.Single(a => a.OperationName == name);

        public double Sum(string name)
        {
            lock (_gate) return Measurements.Where(m => m.Name == name).Sum(m => m.Value);
        }

        public int Count(string name)
        {
            lock (_gate) return Measurements.Count(m => m.Name == name);
        }

        private void Add(string name, double value)
        {
            if (Activity.Current?.TraceId != Trace) return;
            lock (_gate) Measurements.Add((name, value));
        }

        public void Dispose()
        {
            _root.Stop();
            _meters.Dispose();
            _activities.Dispose();
        }
    }

    private static IEnumerable<string> AllTagValues(Capture c) =>
        c.Mine.SelectMany(a => a.TagObjects.Select(t => t.Value?.ToString() ?? ""));

    [Fact]
    public async Task Ingest_emits_nested_spans()
    {
        var (pipeline, _, _, _, _) = Create();
        using var capture = new Capture();

        var result = await pipeline.IngestAsync(Md("tele-doc", "# Title\n\nSome body text for telemetry."), cancellationToken: Ct);

        var ingest = capture.Span("rag.ingest");
        Assert.Equal("tele-doc", ingest.GetTagItem("rag.document.id"));
        Assert.Equal("Created", ingest.GetTagItem("rag.ingest.status"));
        Assert.Equal(result.ChunkCount, ingest.GetTagItem("rag.chunk.count"));
        foreach (var name in new[] { "rag.read", "rag.chunk", "rag.embed", "rag.store" })
            Assert.Equal(ingest.Id, capture.Span(name).ParentId);
        Assert.DoesNotContain(capture.Mine, a => a.OperationName == "rag.content");
        Assert.Equal(1, capture.Count("rag.ingest.duration"));
        Assert.Equal(result.ChunkCount, capture.Sum("rag.ingest.chunks"));

        await pipeline.IngestAsync(Md("tele-doc", "# Title\n\nSome body text for telemetry."), cancellationToken: Ct);
        var unchanged = capture.Mine.Where(a => a.OperationName == "rag.ingest").Last();
        Assert.Equal("Unchanged", unchanged.GetTagItem("rag.ingest.status"));
        Assert.Single(capture.Mine, a => a.OperationName == "rag.read");
    }

    [Fact]
    public async Task External_content_mode_emits_rag_content_span_under_ingest()
    {
        var (pipeline, _, _, _, _) = Create(o => o.ContentStorage = ContentStorageMode.External, b => b.UseContentStore(new InMemoryChunkContentStore()));
        using var capture = new Capture();

        await pipeline.IngestAsync(Md("ext-doc", "# T\n\nExternal body."), cancellationToken: Ct);

        Assert.Equal(capture.Span("rag.ingest").Id, capture.Span("rag.content").ParentId);
    }

    [Fact]
    public async Task Ask_emits_retrieve_and_generate_spans_with_usage()
    {
        var (pipeline, _, _, chat, _) = Create();
        await pipeline.IngestAsync(Md("ask-doc", "# Install\n\nUse apt to install the tool."), cancellationToken: Ct);
        chat.Replies.Enqueue("Use apt [1].");
        chat.Usage = new UsageDetails { InputTokenCount = 42, OutputTokenCount = 7 };
        using var capture = new Capture();

        await pipeline.AskAsync("how do I install", cancellationToken: Ct);

        var retrieve = capture.Span("rag.retrieve");
        Assert.Equal(new RagDotNet.Retrieval.RetrievalOptions().TopK, retrieve.GetTagItem("rag.top_k"));
        Assert.Equal(1, retrieve.GetTagItem("rag.result.count"));
        var generate = capture.Span("rag.generate");
        Assert.Equal("chat", generate.GetTagItem("gen_ai.operation.name"));
        Assert.Equal(42L, generate.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(7L, generate.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(1, capture.Count("rag.retrieve.duration"));
        Assert.Equal(1, capture.Sum("rag.retrieve.count"));
        Assert.True(capture.Sum("rag.generate.context_tokens") > 0);
    }

    [Fact]
    public async Task Streaming_ask_span_covers_the_stream_and_records_usage()
    {
        var (pipeline, _, _, chat, _) = Create();
        await pipeline.IngestAsync(Md("stream-doc", "# Install\n\nUse apt to install the tool."), cancellationToken: Ct);
        chat.Replies.Enqueue("Use apt [1].");
        chat.Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 3 };
        using var capture = new Capture();

        await foreach (var _ in pipeline.AskStreamingAsync("how do I install", cancellationToken: Ct)) { }

        var generate = capture.Span("rag.generate");
        Assert.Equal(11L, generate.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(3L, generate.GetTagItem("gen_ai.usage.output_tokens"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_text_in_tags_unless_sensitive_data_enabled(bool sensitive)
    {
        var (pipeline, _, _, chat, _) = Create(o => o.EnableSensitiveData = sensitive);
        using var capture = new Capture();
        const string question = "how do I install zebrafish";
        const string chunkText = "Zebrafish installation uses apt.";
        await pipeline.IngestAsync(Md("secret-doc", "# Guide\n\n" + chunkText), cancellationToken: Ct);
        chat.Replies.Enqueue("Use apt [1].");

        await pipeline.AskAsync(question, cancellationToken: Ct);

        var values = AllTagValues(capture).ToList();
        Assert.DoesNotContain(values, v => v.Contains(chunkText, StringComparison.Ordinal));
        Assert.DoesNotContain(values, v => v.Contains("Zebrafish", StringComparison.OrdinalIgnoreCase) && v != question);
        Assert.Equal(sensitive, values.Contains(question));
        Assert.Equal(sensitive, values.Contains("Use apt [1]."));
        Assert.Equal(sensitive, capture.Span("rag.retrieve").GetTagItem("rag.query") is not null);
        Assert.Equal(sensitive, capture.Span("rag.generate").GetTagItem("rag.answer") is not null);
    }

    [Fact]
    public async Task Unanswered_counter_increments()
    {
        var (pipeline, _, _, chat, _) = Create();
        await pipeline.IngestAsync(Md("un-doc", "# Install\n\nUse apt to install the tool."), cancellationToken: Ct);
        using var capture = new Capture();

        chat.Replies.Enqueue("I don't know based on the provided documents.");
        var a1 = await pipeline.AskAsync("how do I install", cancellationToken: Ct);
        Assert.False(a1.IsAnswered);
        Assert.Equal(1, capture.Sum("rag.ask.unanswered"));

        chat.Replies.Enqueue("Use apt [1].");
        await pipeline.AskAsync("how do I install", cancellationToken: Ct);
        Assert.Equal(1, capture.Sum("rag.ask.unanswered"));

        // Nothing indexed in a fresh pipeline: the zero-chunks path counts too, and the model is not called.
        var (empty, _, _, _, _) = Create();
        var a3 = await empty.AskAsync("anything at all", cancellationToken: Ct);
        Assert.False(a3.IsAnswered);
        Assert.Equal(2, capture.Sum("rag.ask.unanswered"));
    }

    [Fact]
    public async Task Failed_ingest_sets_error_status_with_exception_type_only()
    {
        var (pipeline, _, _, _, _) = Create();
        using var capture = new Capture();
        var source = new RagDotNet.Documents.DocumentSource("bad-doc", new MemoryStream([1, 2, 3]), "x.unknownext", "application/x-unknown");

        await Assert.ThrowsAnyAsync<Exception>(() => pipeline.IngestAsync(source, cancellationToken: Ct));

        var ingest = capture.Span("rag.ingest");
        Assert.Equal(ActivityStatusCode.Error, ingest.Status);
        Assert.EndsWith("Exception", ingest.StatusDescription);
    }
}
