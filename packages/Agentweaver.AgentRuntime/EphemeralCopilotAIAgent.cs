using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Parameterized <see cref="CopilotAIAgent"/> for built-in roles that run ephemeral turns and
/// must never restore Copilot SDK sessions from workflow checkpoints.
/// </summary>
public sealed class EphemeralCopilotAIAgent : CopilotAIAgent
{
    private static readonly ISet<string> ScribeAllowedTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "report_intent",
        "report_outcome",
        "list_inbox",
    };

    private readonly ISet<string>? _allowedTools;

    public EphemeralCopilotAIAgent(
        string auditRoleName,
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IEnumerable<string>? allowedTools = null,
        IByokProviderConfigurationProvider? byokProviderConfiguration = null,
        IModelInvocationGuard? modelInvocationGuard = null)
        : base(factory, executor, sandboxPolicyStore, approvalStore, toolApprovalGate, logger,
            byokProviderConfiguration: byokProviderConfiguration,
            modelInvocationGuard: modelInvocationGuard)
    {
        if (string.IsNullOrWhiteSpace(auditRoleName))
            throw new ArgumentException("An explicit audit role name is required.", nameof(auditRoleName));

        AuditRoleName = auditRoleName;
        _allowedTools = allowedTools is null
            ? null
            : new HashSet<string>(allowedTools, StringComparer.Ordinal);
    }

    public string AuditRoleName { get; }

    public static EphemeralCopilotAIAgent CreateRai(
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IByokProviderConfigurationProvider? byokProviderConfiguration = null,
        IModelInvocationGuard? modelInvocationGuard = null) =>
        new("Rai", factory, executor, sandboxPolicyStore, approvalStore, toolApprovalGate, logger,
            byokProviderConfiguration: byokProviderConfiguration,
            modelInvocationGuard: modelInvocationGuard);

    public static EphemeralCopilotAIAgent CreateScribe(
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IByokProviderConfigurationProvider? byokProviderConfiguration = null,
        IModelInvocationGuard? modelInvocationGuard = null) =>
        new("Scribe", factory, executor, sandboxPolicyStore, approvalStore, toolApprovalGate, logger,
            allowedTools: ScribeAllowedTools,
            byokProviderConfiguration: byokProviderConfiguration,
            modelInvocationGuard: modelInvocationGuard);

    /// <summary>No-op: ephemeral built-in roles are never resumed across restarts.</summary>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession? session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        new(JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>No-op: ephemeral built-in roles restore a fresh session.</summary>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        CreateSessionCoreAsync(cancellationToken);

    protected override IList<AIFunction> FilterSessionTools(IList<AIFunction> tools) =>
        _allowedTools is null ? tools : FilterAllowedTools(tools, _allowedTools);

    internal static IList<AIFunction> FilterScribeAllowedTools(IEnumerable<AIFunction> tools) =>
        FilterAllowedTools(tools, ScribeAllowedTools);

    private static IList<AIFunction> FilterAllowedTools(IEnumerable<AIFunction> tools, ISet<string> allowedTools) =>
        tools.Where(tool => allowedTools.Contains(tool.Name)).ToList();
}
