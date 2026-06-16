using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Emits workflow.step events and renders a visual pipeline bar to the server console.
///
/// Each run gets one line that is reprinted on every stage transition:
///   [a1b2c3d4] Agent ✓ ── Rai ✓ ── Review … ── Merge · ── Scribe ·
///
/// ANSI colors are applied when the console supports them (non-redirected output).
/// </summary>
public static class WorkflowStepEvents
{
    // Canonical stage order — matches the MAF workflow graph.
    private static readonly string[] StageOrder = ["agent", "rai", "review", "merge", "scribe"];

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _runStates = new();

    // ── ANSI helpers ────────────────────────────────────────────────────────
    private static bool UseAnsi => !Console.IsOutputRedirected;

    private static string Green(string s)  => UseAnsi ? $"\x1b[32m{s}\x1b[0m" : s;
    private static string Red(string s)    => UseAnsi ? $"\x1b[31m{s}\x1b[0m" : s;
    private static string Yellow(string s) => UseAnsi ? $"\x1b[33m{s}\x1b[0m" : s;
    private static string Dim(string s)    => UseAnsi ? $"\x1b[2m{s}\x1b[0m"  : s;
    private static string Bold(string s)   => UseAnsi ? $"\x1b[1m{s}\x1b[0m"  : s;

    // ── Public API ───────────────────────────────────────────────────────────

    public static void Emit(
        ChannelWriter<RunEvent>? stream,
        ILogger logger,
        string runId,
        string step,
        string status,
        string label,
        int sequence = 0,
        string? agentName = null)
    {
        // Update per-run state.
        var runState = _runStates.GetOrAdd(runId, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        runState[step] = status;

        // Write pipeline bar directly to stdout (bypasses logger prefixes for clean viz).
        var bar = BuildPipelineBar(runId, runState, agentName);
        Console.WriteLine(bar);

        // Structured log for log aggregators / file sinks.
        if (agentName is not null)
            logger.LogInformation("[workflow:{RunId}] {Step}({Agent}) → {Status}", runId[..8], step, agentName, status);
        else
            logger.LogInformation("[workflow:{RunId}] {Step} → {Status}", runId[..8], step, status);

        // Emit to the run stream for the web UI.
        var timestampUtc = DateTimeOffset.UtcNow.ToString("O");
        object payload = agentName is not null
            ? new { step, status, label, agent_name = agentName, timestamp_utc = timestampUtc }
            : new { step, status, label, timestamp_utc = timestampUtc };
        stream?.TryWrite(new RunEvent(sequence, EventTypes.WorkflowStep, payload));

        // Clean up completed runs so the dictionary doesn't grow unbounded.
        if (status is "completed" or "failed" or "skipped" && step == "scribe")
            _runStates.TryRemove(runId, out _);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private static string BuildPipelineBar(string runId, ConcurrentDictionary<string, string> state, string? agentName)
    {
        var sb = new StringBuilder();
        sb.Append(Bold($"[{runId[..8]}]"));
        if (agentName is not null)
        {
            sb.Append(' ');
            sb.Append(Dim($"({agentName})"));
        }
        sb.Append(' ');

        for (var i = 0; i < StageOrder.Length; i++)
        {
            var stageName = StageOrder[i];
            state.TryGetValue(stageName, out var stageStatus);
            sb.Append(FormatStage(stageName, stageStatus));
            if (i < StageOrder.Length - 1)
                sb.Append(Dim(" ── "));
        }

        return sb.ToString();
    }

    private static string FormatStage(string name, string? status)
    {
        var label = name switch
        {
            "agent"  => "Agent",
            "rai"    => "Rai",
            "review" => "Review",
            "merge"  => "Merge",
            "scribe" => "Scribe",
            _        => name,
        };

        return status switch
        {
            "started"   => Yellow($"{label} …"),
            "completed" => Green($"{label} ✓"),
            "skipped"   => Dim($"{label} —"),
            "failed"    => Red($"{label} ✗"),
            _           => Dim(label),          // pending — not yet reached
        };
    }
}
