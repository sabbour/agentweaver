using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// A single completed heartbeat tick outcome, stored in the ring buffer.
/// </summary>
public readonly struct TickRecord
{
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Human-readable name of the automation that fired this tick (e.g. "Coordinator Heartbeat").</summary>
    public string AutomationName { get; init; }

    /// <summary>Number of backlog tasks submitted to pickup this tick (across all eligible projects).</summary>
    public int ActedCount { get; init; }

    /// <summary>Number of per-task or per-project errors caught during this tick.</summary>
    public int ErrorCount { get; init; }

    public double DurationMs { get; init; }

    /// <summary>Last error message caught during this tick, or null when error-free.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Thread-safe, process-lifetime observable state for the coordinator heartbeat background service.
/// Written by <see cref="Agentweaver.Api.Coordinator.CoordinatorHeartbeatService"/> after each
/// completed tick; read by the diagnostics endpoints (FR-017). Singleton lifetime; the background
/// service and the diagnostics query path share no mutable state beyond this surface.
/// </summary>
public sealed class HeartbeatStatusStore
{
    private const int RingCapacity = 50;

    private readonly TickRecord[] _ring = new TickRecord[RingCapacity];
    private int _ringHead = 0;     // index of the next write slot
    private int _ringCount = 0;    // number of valid records (0..RingCapacity)
    private string? _lastError;
    private readonly object _sync = new();

    // Cross-pod persistence (optional). When a scope factory is supplied, each tick UPSERTs this
    // pod's row into MemoryDbContext so the diagnostics endpoint can aggregate across replicas.
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ILogger<HeartbeatStatusStore>? _logger;

    /// <summary>This pod's identity (Kubernetes pod name, falling back to the machine/host name).</summary>
    public string PodName { get; }

    /// <summary>
    /// Whether the coordinator heartbeat is enabled
    /// (<c>Coordinator:HeartbeatEnabled</c> configuration key; default <c>true</c>).
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// The fixed tick interval read from <c>Coordinator:HeartbeatIntervalSeconds</c>
    /// (default 10 s; minimum 1 s).
    /// </summary>
    public TimeSpan Interval { get; }

    public HeartbeatStatusStore(
        IConfiguration configuration,
        IServiceScopeFactory? scopeFactory = null,
        IKubernetesEnvironment? kubernetesEnvironment = null,
        ILogger<HeartbeatStatusStore>? logger = null)
    {
        Enabled = configuration.GetValue("Coordinator:HeartbeatEnabled", true);
        var seconds = configuration.GetValue("Coordinator:HeartbeatIntervalSeconds", 10);
        Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        _scopeFactory = scopeFactory;
        _logger = logger;
        PodName = kubernetesEnvironment?.PodName
            ?? (string.IsNullOrWhiteSpace(Environment.MachineName) ? "unknown-pod" : Environment.MachineName);
    }

    /// <summary>UTC timestamp of the last completed tick. Null before the first tick.</summary>
    public DateTimeOffset? LastTickUtc
    {
        get
        {
            lock (_sync)
            {
                if (_ringCount == 0) return null;
                var lastIdx = (_ringHead - 1 + RingCapacity) % RingCapacity;
                return _ring[lastIdx].TimestampUtc;
            }
        }
    }

    /// <summary>Last error message from any tick, or null when the most-recent ticks are error-free.</summary>
    public string? LastError
    {
        get { lock (_sync) return _lastError; }
    }

    /// <summary>
    /// Returns a snapshot of the ring buffer contents, newest first (up to 50 entries).
    /// </summary>
    public TickRecord[] GetRecentActivity()
    {
        lock (_sync)
        {
            if (_ringCount == 0) return [];
            var result = new TickRecord[_ringCount];
            for (int i = 0; i < _ringCount; i++)
            {
                // Walk backwards from the last written slot.
                var idx = (_ringHead - 1 - i + RingCapacity * 2) % RingCapacity;
                result[i] = _ring[idx];
            }
            return result;
        }
    }

    /// <summary>
    /// Records the outcome of a completed tick. Called exclusively by the heartbeat background service.
    /// </summary>
    public void RecordTickOutcome(
        DateTimeOffset tickUtc,
        string automationName,
        int actedCount,
        int errorCount,
        double durationMs,
        string? error)
    {
        lock (_sync)
        {
            _ring[_ringHead] = new TickRecord
            {
                TimestampUtc   = tickUtc,
                AutomationName = automationName,
                ActedCount     = actedCount,
                ErrorCount     = errorCount,
                DurationMs     = durationMs,
                Error          = error,
            };
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;
            if (error is not null) _lastError = error;
        }

        // Cross-pod source of truth: UPSERT this pod's latest tick. Best-effort and fire-and-forget so
        // a transient DB hiccup never disrupts the heartbeat loop; the in-memory ring above is always
        // authoritative for the local pod. No-op when no scope factory was supplied (unit tests).
        if (_scopeFactory is not null)
            _ = UpsertPodRowAsync(tickUtc, actedCount, errorCount, durationMs, error);
    }

    private async Task UpsertPodRowAsync(
        DateTimeOffset tickUtc, int actedCount, int errorCount, double durationMs, string? error)
    {
        try
        {
            using var scope = _scopeFactory!.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var existing = await db.HeartbeatStatuses
                .FirstOrDefaultAsync(h => h.PodName == PodName)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.HeartbeatStatuses.Add(new HeartbeatStatusRecord
                {
                    PodName = PodName,
                    LastTickUtc = tickUtc,
                    ActedCount = actedCount,
                    ErrorCount = errorCount,
                    DurationMs = (long)durationMs,
                    Error = error,
                    Enabled = Enabled,
                    IntervalSeconds = (int)Interval.TotalSeconds,
                });
            }
            else
            {
                existing.LastTickUtc = tickUtc;
                existing.ActedCount = actedCount;
                existing.ErrorCount = errorCount;
                existing.DurationMs = (long)durationMs;
                existing.Error = error;
                existing.Enabled = Enabled;
                existing.IntervalSeconds = (int)Interval.TotalSeconds;
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Heartbeat: failed to persist pod status row for {PodName}", PodName);
        }
    }
}
