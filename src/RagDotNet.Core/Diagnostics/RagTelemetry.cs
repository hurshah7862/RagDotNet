using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RagDotNet.Diagnostics;

/// <summary>Names of the OpenTelemetry sources emitted by RagDotNet.</summary>
public static class RagTelemetry
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> used for traces.</summary>
    public const string ActivitySourceName = "RagDotNet";

    /// <summary>The name of the <see cref="System.Diagnostics.Metrics.Meter"/> used for metrics.</summary>
    public const string MeterName = "RagDotNet";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> IngestDuration = Meter.CreateHistogram<double>("rag.ingest.duration", "s", "Time to ingest one document.");

    internal static readonly Histogram<int> IngestChunks = Meter.CreateHistogram<int>("rag.ingest.chunks", "{chunk}", "Chunks produced per ingested document.");

    internal static readonly Histogram<double> RetrieveDuration = Meter.CreateHistogram<double>("rag.retrieve.duration", "s", "Time to retrieve chunks for a query.");

    internal static readonly Histogram<int> RetrieveCount = Meter.CreateHistogram<int>("rag.retrieve.count", "{chunk}", "Chunks returned per retrieval.");

    internal static readonly Histogram<int> GenerateContextTokens = Meter.CreateHistogram<int>("rag.generate.context_tokens", "{token}", "Context tokens sent to the model per answer.");

    internal static readonly Counter<long> AskUnanswered = Meter.CreateCounter<long>("rag.ask.unanswered", "{answer}", "Answers where the documents did not contain the answer.");

    internal static readonly Counter<long> ContentMissing = Meter.CreateCounter<long>("rag.content.missing", "{chunk}", "Chunks dropped because the content store had no text.");

    /// <summary>Marks the activity failed with the exception type only; messages may contain document text.</summary>
    internal static void Fail(Activity? activity, Exception ex) =>
        activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
}
