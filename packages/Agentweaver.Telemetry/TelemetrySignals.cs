using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentweaver.Telemetry;

public static class TelemetrySignals
{
    public const string Name = "Agentweaver";

    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Metrics = new(Name);
}
