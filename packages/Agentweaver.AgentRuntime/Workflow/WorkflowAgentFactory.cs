using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime.Workflow;

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
    private readonly IQuestionGate _questionGate;
    private readonly IRunOptionsStore _runOptions;
    private readonly ILoggerFactory _loggerFactory;

    public WorkflowAgentFactory(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        IQuestionGate questionGate,
        IRunOptionsStore runOptions,
        ILoggerFactory loggerFactory)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _questionGate = questionGate;
        _runOptions = runOptions;
        _loggerFactory = loggerFactory;
    }

    public IWorkflowTurnAgent CreateWorkerAgent() => new CopilotAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>(), _questionGate, _runOptions);

    public IWorkflowTurnAgent CreateRaiAgent() => new RaiAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>());

    public IWorkflowTurnAgent CreateScribeAgent() => new ScribeAIAgent(
        _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
        _approvalStore, _toolApprovalGate, _loggerFactory.CreateLogger<CopilotAIAgent>());
}
