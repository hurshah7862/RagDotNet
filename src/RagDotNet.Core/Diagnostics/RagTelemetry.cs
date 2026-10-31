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
}
