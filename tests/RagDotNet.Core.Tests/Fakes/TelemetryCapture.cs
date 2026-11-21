using System.Diagnostics;
using System.Diagnostics.Metrics;
using RagDotNet.Diagnostics;

namespace RagDotNet.Core.Tests.Fakes;

/// <summary>Records RagDotNet spans and measurements belonging to this test's own trace only (tests run in parallel).</summary>
internal sealed class TelemetryCapture : IDisposable
{
    private static readonly ActivitySource TestSource = new("RagDotNet.Tests");
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters = new();
    private readonly Activity _root;
    private readonly object _gate = new();

    public TelemetryCapture()
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
