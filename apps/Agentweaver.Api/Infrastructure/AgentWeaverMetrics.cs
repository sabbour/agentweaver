using System.Diagnostics.Metrics;
using System.Threading;

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

    /// <summary>AI credit usage (nano AIU) tagged by agent and model.</summary>
    public static readonly Counter<long> TokenUsage =
        Meter.CreateCounter<long>("agentweaver.token.usage", "nano_aiu", "AI credit usage by agent and model");

    /// <summary>Run duration in milliseconds.</summary>
    public static readonly Histogram<double> RunDuration =
        Meter.CreateHistogram<double>("agentweaver.run.duration", "ms", "Run duration in milliseconds");

    /// <summary>Run errors by type.</summary>
    public static readonly Counter<long> RunErrors =
        Meter.CreateCounter<long>("agentweaver.run.errors", "runs", "Run errors by type");

    /// <summary>Currently active runs.</summary>
    public static readonly UpDownCounter<int> ActiveRuns =
        Meter.CreateUpDownCounter<int>("agentweaver.run.active", "runs", "Currently active runs");

    // Backing field for the queued-runs gauge below. Updated by the polling background service
    // (Runs.QueuedRunsMetricService) rather than incremented inline at call sites, because the
    // true queue-depth signal is a point-in-time snapshot of claimable Ready backlog tasks across
    // the shared store, not something any single transition can maintain incrementally.
    // Interlocked/Volatile access since the OTel export pipeline reads this on its own
    // collection-interval thread while the poller writes it concurrently.
    private static long _queuedRunsCount;

    /// <summary>
    /// Global count of ACTIVE-project backlog tasks waiting for coordinator pickup
    /// (<c>backlog_tasks.state='ready' AND run_id IS NULL</c>). The instrument name is retained for
    /// compatibility, but it is intentionally NOT derived from <c>runs.status='pending'</c>.
    /// Sampled periodically by <see cref="Agentweaver.Api.Runs.QueuedRunsMetricService"/> and read
    /// by the OTel export pipeline on each collection interval. Because every replica exports the
    /// same shared snapshot, downstream queries must use <c>max</c> rather than <c>sum</c>.
    /// </summary>
    public static readonly ObservableGauge<long> QueuedRuns = Meter.CreateObservableGauge(
        "agentweaver.run.queued",
        () => Volatile.Read(ref _queuedRunsCount),
        "tasks",
        "Active-project ready backlog tasks awaiting coordinator pickup");

    /// <summary>
    /// Updates the cached value backing <see cref="QueuedRuns"/>. Internal so
    /// <see cref="Agentweaver.Api.Runs.QueuedRunsMetricService"/> (and tests) can drive it
    /// directly without needing a live OTel collection cycle.
    /// </summary>
    internal static void SetQueuedRunsCount(long count) => Interlocked.Exchange(ref _queuedRunsCount, count);
}
