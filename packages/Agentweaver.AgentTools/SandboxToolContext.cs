using Microsoft.Extensions.Logging;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.AgentTools;

/// <summary>
/// Per-run context threaded into every sandboxed tool function.
/// </summary>
public sealed record SandboxToolContext(
    string AgentId,
    string WorkingDirectory,
    string SandboxRoot,
    ISandboxExecutor Executor,
    SandboxedFileTools FileTools,
    SandboxedSearchTools SearchTools,
    SandboxOutputRedactor Redactor,
    SandboxToolOptions Options,
    ILogger Logger,
    /// <summary>Optional: emits a run event. Null in test/CLI contexts.</summary>
    Action<string, object>? EmitEvent = null,
    /// <summary>The run ID — used to scope shell approvals.</summary>
    string RunId = "",
    /// <summary>
    /// Returns true if the given command hash has been approved for this run.
    /// Null in test contexts (treated as not approved).
    /// </summary>
    Func<string, bool>? IsCommandApproved = null,
    /// <summary>
    /// Returns true if the given command hash has been denied for this run.
    /// Null in test contexts (treated as not denied).
    /// </summary>
    Func<string, bool>? IsCommandDenied = null,
    /// <summary>
    /// Optional blocking question gate used by the <c>ask_question</c> tool to suspend
    /// until the operator answers. Null in test/CLI contexts (ask_question returns a
    /// proceed-with-best-judgement fallback when absent).
    /// </summary>
    Agentweaver.Domain.IQuestionGate? QuestionGate = null,
    /// <summary>
    /// Optional single-flight shell tracker. Assembly Build/Test supplies one so concurrent model
    /// tool calls cannot overlap and future heartbeat/deadline policies can observe active timing.
    /// </summary>
    ShellExecutionTracker? ShellExecutionTracker = null,
    /// <summary>
    /// Optional run-scoped scratch directory that is outside the git worktree and therefore never a
    /// commit candidate. Shell tools may use it for ephemeral artifacts; file tools remain rooted at
    /// <see cref="SandboxRoot"/>.
    /// </summary>
    string? ScratchDirectory = null,
    /// <summary>
    /// Per-turn Agentweaver API base URL (worker-tier), delivered via <c>AgentSetupParams</c> (#335 P1)
    /// since warm AgentHost pods boot with no static <c>AgentHost__ApiBaseUrl</c>. Threaded into
    /// <see cref="IAgentRuntimeToolProvider"/> implementations (e.g. the preview-publish tool) so they
    /// target the real API instead of falling back to an unreachable <c>http://localhost:5000</c>.
    /// Null/empty when not yet delivered (pre-first-turn) or on the direct/env-var launch path.
    /// </summary>
    string? ApiBaseUrl = null,
    /// <summary>Per-turn Agentweaver API key paired with <see cref="ApiBaseUrl"/>. See its remarks.</summary>
    string? ApiKey = null);
