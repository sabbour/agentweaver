using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentweaver.Telemetry;

/// <summary>
/// Shared OpenTelemetry instruments used by Agentweaver services so hosts can subscribe to
/// one stable source name without depending on service-specific instrumentation.
/// </summary>
public static class TelemetrySignals
{
    /// <summary>The stable source name registered by the telemetry service extensions.</summary>
    public const string Name = "Agentweaver";

    /// <summary>The shared source for tracing activities.</summary>
    public static readonly ActivitySource Activities = new(Name);

    /// <summary>The shared meter for application metrics.</summary>
    public static readonly Meter Metrics = new(Name);
}
