using Microsoft.Extensions.Logging;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentTools;

/// <summary>
/// Per-run context threaded into every sandboxed tool function.
/// Governance is exposed as a delegate to avoid a direct reference to
/// the internal SandboxGovernance type (which lives in AgentRuntime).
/// This keeps the dependency graph acyclic: AgentTools does not reference AgentRuntime.
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
    /// <summary>
    /// Governance evaluation delegate. Returns (allowed, reason).
    /// Backed by SandboxGovernance.EvaluateToolCall in production.
    /// </summary>
    Func<string, IReadOnlyDictionary<string, object>, (bool Allowed, string? Reason)> EvaluateToolCall,
    ILogger Logger);
