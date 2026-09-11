using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Domain;

/// <summary>
/// Immutable approval policy selected when a run is created. Both options default OFF.
/// Auto-approval is limited to tools explicitly classified as safe by the repository.
/// </summary>
public sealed record RunApprovalPolicy(bool AutoApproveTools = false, bool Autopilot = false)
{
    public static RunApprovalPolicy ForDirectRun(bool? autoApproveTools, bool? autopilot) =>
        new(autoApproveTools ?? false, autopilot ?? false);

    public static RunApprovalPolicy ForBacklogPickup(bool autoApproveTools, bool autopilot) =>
        new(autoApproveTools, autopilot);

    public bool AllowsAutoApproval(string toolName) =>
        AutoApproveTools && ToolApprovalPolicySemantics.IsRunAutoApprovalEligible(toolName);

    public RunOptions ToRunOptions() => new(AutoApproveTools, Autopilot);

    public static RunApprovalPolicy FromOptions(RunOptions options) =>
        new(options.AutoApproveTools, options.Autopilot);
}

/// <summary>
/// Immutable provenance for the approval policy selected when a run was created.
/// Heartbeat claims capture the project settings and their update timestamp inside the same
/// transaction that reserves the run.
/// </summary>
public sealed record RunApprovalPolicySnapshot
{
    public RunApprovalPolicySnapshot(
        RunApprovalPolicy Policy,
        string Source,
        DateTimeOffset CapturedAt,
        DateTimeOffset? SettingsUpdatedAt = null,
        string? InheritedFromRunId = null,
        string? SnapshotId = null)
    {
        this.Policy = Policy;
        this.Source = Source;
        this.CapturedAt = CapturedAt;
        this.SettingsUpdatedAt = SettingsUpdatedAt;
        this.InheritedFromRunId = InheritedFromRunId;
        this.SnapshotId = SnapshotId ?? ComputeSnapshotId(
            Policy, Source, CapturedAt, SettingsUpdatedAt, InheritedFromRunId);
    }

    public RunApprovalPolicy Policy { get; }
    public string Source { get; }
    public DateTimeOffset CapturedAt { get; }
    public DateTimeOffset? SettingsUpdatedAt { get; }
    public string? InheritedFromRunId { get; }

    /// <summary>
    /// Stable, persisted, non-secret identity for this immutable snapshot.
    /// </summary>
    public string SnapshotId { get; }

    private static string ComputeSnapshotId(
        RunApprovalPolicy policy,
        string source,
        DateTimeOffset capturedAt,
        DateTimeOffset? settingsUpdatedAt,
        string? inheritedFromRunId)
    {
        var material = string.Join(
            "\n",
            policy.AutoApproveTools ? "1" : "0",
            policy.Autopilot ? "1" : "0",
            source,
            capturedAt.ToUniversalTime().ToString("O"),
            settingsUpdatedAt?.ToUniversalTime().ToString("O") ?? "",
            inheritedFromRunId ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..24];
    }
}

/// <summary>
/// Per-run operator options that change how a run handles human-in-the-loop interactions.
/// Both default OFF. They cascade from a coordinator run to its dispatched child runs.
/// </summary>
/// <param name="AutoApproveTools">
/// When true, a repository-approved safe tool request (<c>web_fetch</c> or
/// <c>start_preview</c>) is auto-granted at the HITL gate instead of stalling for an operator.
/// This NEVER overrides validation or a policy deny: dangerous tools are rejected upstream by
/// sandbox governance before the HITL gate is ever reached.
/// </param>
/// <param name="Autopilot">
/// Coordinator-only. When true, CLARIFYING QUESTIONS bubbled by child workers (or asked on the
/// coordinator run) are auto-answered by the coordinator model from the outcome spec + context.
/// Tool-approval/permission requests are NEVER auto-granted by Autopilot (that is the separate
/// <see cref="AutoApproveTools"/> opt-in).
/// </param>
public sealed record RunOptions(bool AutoApproveTools = false, bool Autopilot = false);

/// <summary>
/// In-memory, per-run source of truth for <see cref="RunOptions"/>. The agent runtime reads it on
/// the hot path (per tool call) with no database round-trip; launch and live-toggle endpoints write
/// it. Runtime overrides are cleared on completion, then reads fall back to the persisted launch
/// policy so run details and retries retain the auditable choice.
/// </summary>
public interface IRunOptionsStore
{
    /// <summary>Seeds (or replaces) the full options for a run, typically at launch/dispatch.</summary>
    void Set(string runId, RunOptions options);

    /// <summary>Returns the current options for a run, or <see cref="RunOptions"/> defaults (both OFF) if unknown.</summary>
    RunOptions Get(string runId);

    /// <summary>
    /// Returns the immutable policy selected when the run was launched. This survives runtime
    /// cleanup so retries can preserve the original, auditable choice.
    /// </summary>
    RunApprovalPolicy GetLaunchPolicy(string runId);

    /// <summary>Toggles the auto-approve-tools flag for a run, preserving the other flag.</summary>
    void SetAutoApproveTools(string runId, bool enabled);

    /// <summary>Toggles the Autopilot flag for a run, preserving the other flag.</summary>
    void SetAutopilot(string runId, bool enabled);

    /// <summary>Clears runtime overrides while retaining the immutable launch policy.</summary>
    void Clear(string runId);
}
