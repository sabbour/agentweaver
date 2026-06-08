using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Integration;
using AgentGovernance.Policy;
using Microsoft.Extensions.Logging;
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
        description: Deny-by-default sandbox confinement for all agent tool calls.
        defaultAction: Deny
        rules:
          - name: allow-file-read-or-list
            condition: "tool_name == 'read_file' or tool_name == 'list_directory'"
            action: Allow
            description: >
              Unified rule — both read_file and list_directory pass tool-name gating.
              Actual path containment is enforced by SandboxPolicyBackend regardless
              of which tool name was resolved.
          - name: allow-file-write
            condition: "tool_name == 'write_file' or tool_name == 'edit_file'"
            action: Allow
            description: Allowed if SandboxPolicyBackend passes path check.
          - name: deny-shell
            condition: "tool_name == 'shell'"
            action: Deny
            description: Shell execution categorically denied.
          - name: deny-all-other
            condition: "true"
            action: Deny
            description: Default deny — unknown tools blocked.
        """;

    public GovernanceKernel Kernel { get; }
    public SandboxPolicyBackend SandboxBackend { get; }

    private SandboxGovernance(GovernanceKernel kernel, SandboxPolicyBackend sandboxBackend)
    {
        Kernel = kernel;
        SandboxBackend = sandboxBackend;
    }

    /// <summary>
    /// Creates a per-run GovernanceKernel with the sandbox policy loaded,
    /// SandboxPolicyBackend registered, and audit events wired to ILogger.
    /// </summary>
    internal static SandboxGovernance Create(string workingDirectory, string runId, ILogger logger)
    {
        var options = new GovernanceOptions
        {
            EnableAudit = true,
            EnableMetrics = true,
            EnableRings = false,
            EnablePromptInjectionDetection = false,
            EnableCircuitBreaker = false,
        };

        var kernel = new GovernanceKernel(options);

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

            return new SandboxGovernance(kernel, sandboxBackend);
        }
        catch
        {
            kernel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Dual-layer evaluation: AGT kernel + unconditional direct backend check.
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

            return (allowed, allowed ? null : (directCheck.Reason ?? agtResult.Reason ?? "Denied by sandbox policy."));
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
