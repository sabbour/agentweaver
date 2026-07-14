namespace Agentweaver.AgentTools;

/// <summary>
/// Tool-relevant subset of sandbox options. Populated from SandboxOptions in AgentRuntime.
/// </summary>
public sealed record SandboxToolOptions(
    bool ShellEnabled,
    int DefaultTimeoutMs = 30_000)
{
    /// <summary>
    /// Allowed repository roots accessible as read-only inside the sandbox.
    /// If empty, only the working directory is accessible.
    /// </summary>
    public string[] AllowedRepositoryRoots { get; init; } = [];

    /// <summary>
    /// Command patterns that require human approval before execution.
    /// Matched case-insensitively after whitespace normalization.
    /// </summary>
    public string[] DestructiveCommandPatterns { get; init; } = [];

    /// <summary>When true, ALL shell commands require human approval (not just destructive ones).</summary>
    public bool RequireApprovalForAllShell { get; init; } = false;

    /// <summary>
    /// Allow outbound network inside the sandbox. Default: false.
    /// Mirrors <c>SandboxPolicy.NetworkEnabled</c>.
    /// </summary>
    public bool NetworkEnabled { get; init; } = false;

    /// <summary>Reject destructive commands instead of waiting for operator approval.</summary>
    public bool RejectDestructiveCommands { get; init; } = false;

    /// <summary>Reject commands that detach/background work outside the command lifetime.</summary>
    public bool RejectBackgroundCommands { get; init; } = false;

    /// <summary>Upper bound for model-supplied command timeouts. Zero means no extra cap.</summary>
    public int MaximumTimeoutMs { get; init; } = 0;

    /// <summary>
    /// Lower bound (floor) for the effective command timeout. A caller-supplied
    /// <c>timeout_ms</c> below this is clamped up to it. Zero means no floor. Used by the
    /// Build/Test gate so an optimistically short model timeout can't set a sub-floor window
    /// that kills a legitimate long build (#313).
    /// </summary>
    public int MinimumTimeoutMs { get; init; } = 0;

    /// <summary>
    /// #313: grace added to the effective command timeout when arming the streaming watchdog's
    /// shell hard-deadline (<c>ShellExecutionTracker.EnterAsync</c>). Keeping the watchdog deadline
    /// strictly LATER than the executor's own <c>CancelAfter(command.TimeoutMs)</c> guarantees the
    /// executor expires first and returns a graceful, recoverable <c>timed_out:true</c>; the
    /// watchdog then only trips for a genuinely hung/unkillable process rather than duplicating the
    /// per-command timeout and fatally aborting the turn as <c>shell_execution_timeout</c>.
    /// </summary>
    public static readonly TimeSpan WatchdogTimeoutGrace = TimeSpan.FromSeconds(60);
}
