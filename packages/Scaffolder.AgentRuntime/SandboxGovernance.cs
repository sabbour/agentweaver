using AgentGovernance;
using AgentGovernance.Audit;
using AgentGovernance.Integration;
using AgentGovernance.Policy;
using Microsoft.Extensions.Logging;
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
        description: Deny-by-default sandbox confinement for all agent tool calls.
        defaultAction: Deny
        rules:
          - name: allow-file-tools
            condition: "tool_name == 'read_file' or tool_name == 'list_directory' or tool_name == 'write_file' or tool_name == 'edit_file' or tool_name == 'str_replace_editor' or tool_name == 'apply_patch' or tool_name == 'create' or tool_name == 'edit'"
            action: Allow
            description: >
              File read/write/edit tools. Actual path containment enforced by SandboxPolicyBackend.
          - name: allow-search-tools
            condition: "tool_name == 'grep_search' or tool_name == 'file_search'"
            action: Allow
            description: >
              In-process search tools constrained to sandbox root.
          - name: allow-shell-sandboxed
            condition: "tool_name == 'run_command'"
            action: Allow
            description: >
              Custom sandboxed shell. Real isolation gated by ISandboxExecutor.IsRealIsolation
              and Sandbox:ShellEnabled config. SandboxPolicyBackend validates working directory.
          - name: allow-intent
            condition: "tool_name == 'report_intent'"
            action: Allow
            description: >
              UI observability tool. Emits agent.intent event. No filesystem/shell action.
          - name: deny-native-shell
            condition: "tool_name == 'shell'"
            action: Deny
            description: >
              Native shell tool ALWAYS denied (defense-in-depth). Execution routed through
              run_command custom tool which we control. See C1 resolution.
          - name: deny-all-other
            condition: "true"
            action: Deny
            description: Default deny — unknown tools blocked.
        """;

    public GovernanceKernel Kernel { get; }
    public SandboxPolicyBackend SandboxBackend { get; }

    internal string RunId { get; }
    internal ILogger Logger { get; }

    private readonly ISandboxExecutor _executor;
    private readonly SandboxOptions _options;

    private SandboxGovernance(
        GovernanceKernel kernel,
        SandboxPolicyBackend sandboxBackend,
        string runId,
        ILogger logger,
        ISandboxExecutor executor,
        SandboxOptions options)
    {
        Kernel = kernel;
        SandboxBackend = sandboxBackend;
        RunId = runId;
        Logger = logger;
        _executor = executor;
        _options = options;
    }

    /// <summary>
    /// Creates a per-run GovernanceKernel with the sandbox policy loaded,
    /// SandboxPolicyBackend registered, and audit events wired to ILogger.
    /// </summary>
    internal static SandboxGovernance Create(
        string workingDirectory,
        string runId,
        ISandboxExecutor executor,
        SandboxOptions options,
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

            return new SandboxGovernance(kernel, sandboxBackend, runId, logger, executor, options);
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
                if (!_executor.IsRealIsolation)
                {
                    allowed = false;
                    reason = $"Shell execution denied: executor '{_executor.BackendName}' provides no real isolation (IsRealIsolation = false).";
                }
                else if (!_options.ShellEnabled)
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

