using System.Diagnostics;
using RagDotNet.Diagnostics;

namespace RagDotNet.StoreTests.Infrastructure;

/// <summary>Records the rag.hybrid.path tag of retrieve spans in this test's own trace (tests run in parallel).</summary>
internal sealed class HybridPathCapture : IDisposable
{
    // A constant, not TestSource.Name: creating TestSource calls every registered listener's ShouldListenTo, and a
    // listener from a parallel test must not read TestSource while it is still null.
    private const string TestSourceName = "RagDotNet.StoreTests";
    private static readonly ActivitySource TestSource = new(TestSourceName);
    private readonly ActivityListener _listener;
    private readonly Activity _root;
    private readonly List<string> _paths = [];

    public HybridPathCapture()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RagTelemetry.ActivitySourceName || s.Name == TestSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Record,
        };
        ActivitySource.AddActivityListener(_listener);
        _root = TestSource.StartActivity("store-test")!;
    }

    public IReadOnlyList<string> Paths
    {
        get { lock (_paths) return _paths.ToList(); }
    }

    public void Dispose()
    {
        _root.Stop();
        _listener.Dispose();
    }

    private void Record(Activity a)
    {
        if (_root is null || a.TraceId != _root.TraceId || a.OperationName != "rag.retrieve") return;
        if (a.GetTagItem("rag.hybrid.path") is string path)
            lock (_paths) _paths.Add(path);
    }
}
