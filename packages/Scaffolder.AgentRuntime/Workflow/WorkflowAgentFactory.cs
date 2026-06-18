using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;

namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>
/// Production <see cref="IWorkflowAgentFactory"/>. Builds real <see cref="CopilotAIAgent"/>,
/// <see cref="RaiAIAgent"/>, and <see cref="ScribeAIAgent"/> instances from DI-resolved
/// dependencies — identical to the previous inline <c>new CopilotAIAgent(...)</c> construction.
/// </summary>
public sealed class WorkflowAgentFactory : IWorkflowAgentFactory
{
    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;

    public WorkflowAgentFactory(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _loggerFactory = loggerFactory;
    }

    public IWorkflowTurnAgent CreateWorkerAgent() => new CopilotAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>());

    public IWorkflowTurnAgent CreateRaiAgent() => new RaiAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>());

    public IWorkflowTurnAgent CreateScribeAgent() => new ScribeAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>());
}
