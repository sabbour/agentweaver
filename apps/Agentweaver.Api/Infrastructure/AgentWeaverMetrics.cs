using System.Diagnostics.Metrics;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Business-level OpenTelemetry metrics for the Agentweaver platform.
/// These counters and histograms are exported to Azure Monitor (Application Insights)
/// and AKS Managed Prometheus when <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set.
/// </summary>
public static class AgentWeaverMetrics
{
    public static readonly Meter Meter = new("Agentweaver", "1.0.0");

    /// <summary>Runs created (started).</summary>
    public static readonly Counter<long> RunsCreated =
        Meter.CreateCounter<long>("agentweaver.run.created", "runs", "Runs created");

    /// <summary>Runs that reached a terminal state, tagged with <c>status</c> = "succeeded" | "failed".</summary>
    public static readonly Counter<long> RunsCompleted =
        Meter.CreateCounter<long>("agentweaver.run.completed", "runs", "Runs completed by status");

    /// <summary>Token usage by agent and model.</summary>
    public static readonly Counter<long> TokenUsage =
        Meter.CreateCounter<long>("agentweaver.token.usage", "tokens", "Token usage by agent and model");

    /// <summary>Run duration in milliseconds.</summary>
    public static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>("agentweaver.run.duration", "ms", "Run duration in milliseconds");

    /// <summary>Run errors by type.</summary>
    public static readonly Counter<long> RunErrors =
        Meter.CreateCounter<long>("agentweaver.run.errors", "runs", "Run errors by type");

    /// <summary>Currently active runs.</summary>
    public static readonly UpDownCounter<int> ActiveRuns =
        Meter.CreateUpDownCounter<int>("agentweaver.run.active", "runs", "Currently active runs");
}
