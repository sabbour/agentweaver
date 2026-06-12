using Microsoft.Extensions.Logging;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentTools;

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
    Func<string, bool>? IsCommandDenied = null);
