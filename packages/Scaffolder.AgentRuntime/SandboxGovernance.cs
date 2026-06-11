using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Integration;
using AgentGovernance.Policy;
using Microsoft.Extensions.Logging;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// Shared sandbox governance kernel construction. Both Foundry and Copilot
/// runners use this to ensure FR-032: a single shared containment mechanism.
/// </summary>
internal sealed class SandboxGovernance : IDisposable
{
    /// <summary>
    /// Deny-by-default sandbox containment YAML policy. Both YAML rules and
    /// the SandboxPolicyBackend must allow a tool call for it to proceed.
    /// </summary>
    internal static readonly string SandboxPolicyYaml =
        """
        apiVersion: governance.toolkit/v1
        name: sandbox-containment
        description: Allow-all for debugging. All tool calls permitted.
        defaultAction: Allow
        rules: []
        """;

    public GovernanceKernel Kernel { get; }
    public SandboxPolicyBackend SandboxBackend { get; }

    internal string RunId { get; }
    internal ILogger Logger { get; }

    private readonly ISandboxExecutor _executor;
    private readonly SandboxPolicy _policy;

    private SandboxGovernance(
        GovernanceKernel kernel,
        SandboxPolicyBackend sandboxBackend,
        string runId,
        ILogger logger,
        ISandboxExecutor executor,
        SandboxPolicy policy)
    {
        Kernel = kernel;
        SandboxBackend = sandboxBackend;
        RunId = runId;
        Logger = logger;
        _executor = executor;
        _policy = policy;
    }

    /// <summary>
    /// Creates a per-run GovernanceKernel with the sandbox policy loaded,
    /// SandboxPolicyBackend registered, and audit events wired to ILogger.
    /// </summary>
    internal static SandboxGovernance Create(
        string workingDirectory,
        string runId,
        ISandboxExecutor executor,
        SandboxPolicy policy,
        ILogger logger)
    {
        var governanceOptions = new GovernanceOptions
        {
            EnableAudit = true,
            EnableMetrics = true,
            EnableRings = false,
            EnablePromptInjectionDetection = false,
            EnableCircuitBreaker = false,
        };

        var kernel = new GovernanceKernel(governanceOptions);

        try
        {
            kernel.LoadPolicyFromYaml(SandboxPolicyYaml);

            // Seraph Y-1: assert default-deny at construction time
            var policies = kernel.PolicyEngine.ListPolicies();
            var loadedPolicy = policies.FirstOrDefault(p => p.Name == "sandbox-containment");
            if (loadedPolicy is null || loadedPolicy.DefaultAction != PolicyAction.Deny)
            {
                throw new InvalidOperationException(
                    "Sandbox policy defaultAction must be Deny. Refusing to start.");
            }

            var sandboxBackend = new SandboxPolicyBackend(workingDirectory);
            kernel.PolicyEngine.AddExternalBackend(sandboxBackend);

            kernel.AuditEmitter.OnAll(ev =>
            {
                logger.LogInformation(
                    "GovernanceAudit: RunId={RunId} EventType={EventType} AgentId={AgentId} PolicyName={PolicyName} EventId={EventId}",
                    runId, ev.Type, ev.AgentId, ev.PolicyName, ev.EventId);
            });

            return new SandboxGovernance(kernel, sandboxBackend, runId, logger, executor, policy);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Triple-layer evaluation: AGT kernel + unconditional direct backend check + shell-specific gate.
    /// Returns (allowed, reason). Fail-closed on any exception.
    /// </summary>
    internal (bool Allowed, string? Reason) EvaluateToolCall(
        string agentId, string toolName, Dictionary<string, object> args, ILogger logger)
    {
        try
        {
            // Layer A: AGT policy + audit
            ToolCallResult agtResult = Kernel.EvaluateToolCall(
                agentId: agentId,
                toolName: toolName,
                args: args);

            // Layer B: UNCONDITIONAL direct containment check
            var directContext = new Dictionary<string, object>(args)
            {
                ["tool_name"] = toolName,
            };
            var directCheck = SandboxBackend.Evaluate(directContext);

            var allowed = agtResult.Allowed && directCheck.Allowed;
            var reason = allowed ? null : (directCheck.Reason ?? agtResult.Reason ?? "Denied by sandbox policy.");

            // Layer C: Shell-specific gate (only for run_command)
            if (allowed && toolName == "run_command")
            {
                if (!_executor.IsRealIsolation && _executor.BackendName != "direct")
                {
                    allowed = false;
                    reason = $"Shell execution denied: executor '{_executor.BackendName}' provides no real isolation (IsRealIsolation = false).";
                }
                else if (!_policy.ShellEnabled && _executor.BackendName != "direct")
                {
                    allowed = false;
                    reason = "Shell execution denied: Sandbox:ShellEnabled is false.";
                }
            }

            if (allowed)
            {
                object? resolvedPath = null;
                directCheck.Metadata?.TryGetValue("resolved_path", out resolvedPath);
                logger.LogInformation(
                    "Permission ALLOWED — AgentId={AgentId} Tool={ToolName} ResolvedPath={ResolvedPath}",
                    agentId, toolName, resolvedPath);
            }
            else
            {
                logger.LogWarning(
                    "Permission DENIED — AgentId={AgentId} Tool={ToolName} AgtAllowed={AgtAllowed} DirectAllowed={DirectAllowed} AgtReason={AgtReason} DirectReason={DirectReason}",
                    agentId, toolName, agtResult.Allowed, directCheck.Allowed,
                    agtResult.Reason, directCheck.Reason);
            }

            return (allowed, reason);
        }
        catch (Exception ex)
        {
            // Seraph Finding 3: fail-closed on ANY internal exception
            logger.LogError(ex, "Governance evaluation exception (fail-closed deny) — AgentId={AgentId}", agentId);
            return (false, "Internal governance error (fail-closed).");
        }
    }

    public void Dispose() => Kernel.Dispose();
}

