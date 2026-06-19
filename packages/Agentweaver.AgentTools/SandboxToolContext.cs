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
    Agentweaver.Domain.IQuestionGate? QuestionGate = null);
