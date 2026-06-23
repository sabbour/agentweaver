using Microsoft.Extensions.Configuration;

namespace Agentweaver.Api.Diagnostics;

/// <summary>
/// A single completed heartbeat tick outcome, stored in the ring buffer.
/// </summary>
public readonly struct TickRecord
{
    public DateTimeOffset TimestampUtc { get; init; }

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

    public HeartbeatStatusStore(IConfiguration configuration)
    {
        Enabled = configuration.GetValue("Coordinator:HeartbeatEnabled", true);
        var seconds = configuration.GetValue("Coordinator:HeartbeatIntervalSeconds", 10);
        Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
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
        int actedCount,
        int errorCount,
        double durationMs,
        string? error)
    {
        lock (_sync)
        {
            _ring[_ringHead] = new TickRecord
            {
                TimestampUtc = tickUtc,
                ActedCount   = actedCount,
                ErrorCount   = errorCount,
                DurationMs   = durationMs,
                Error        = error,
            };
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_ringCount < RingCapacity) _ringCount++;
            if (error is not null) _lastError = error;
        }
    }
}
