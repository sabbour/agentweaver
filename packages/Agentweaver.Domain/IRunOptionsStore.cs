namespace Agentweaver.Domain;

/// <summary>
/// Per-run operator options that change how a run handles human-in-the-loop interactions.
/// Both default OFF. They cascade from a coordinator run to its dispatched child runs.
/// </summary>
/// <param name="AutoApproveTools">
/// When true, an allow-with-approval tool request (e.g. <c>web_fetch</c>) is auto-granted at the
/// HITL gate instead of stalling for an operator. This NEVER overrides a policy deny: dangerous
/// tools are rejected upstream by sandbox governance before the HITL gate is ever reached.
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
/// it. Entries are cleared on run completion alongside the approval/question gates.
/// </summary>
public interface IRunOptionsStore
{
    /// <summary>Seeds (or replaces) the full options for a run, typically at launch/dispatch.</summary>
    void Set(string runId, RunOptions options);

    /// <summary>Returns the current options for a run, or <see cref="RunOptions"/> defaults (both OFF) if unknown.</summary>
    RunOptions Get(string runId);

    /// <summary>Toggles the auto-approve-tools flag for a run, preserving the other flag.</summary>
    void SetAutoApproveTools(string runId, bool enabled);

    /// <summary>Toggles the Autopilot flag for a run, preserving the other flag.</summary>
    void SetAutopilot(string runId, bool enabled);

    /// <summary>Removes a run's options (called on run completion).</summary>
    void Clear(string runId);
}
