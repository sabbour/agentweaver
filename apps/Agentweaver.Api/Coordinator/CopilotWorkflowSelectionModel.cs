using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Production <see cref="IWorkflowSelectionModel"/>: runs a single, grounded Copilot coordinator turn
/// to select a workflow. It resolves the project's repository path (so charters/tools resolve as in
/// other coordinator turns), runs ONE non-streaming completion, and returns the raw text. Any failure
/// (model unavailable, project missing, exception) is swallowed and returned as <c>null</c> so the
/// <see cref="WorkflowSelector"/> falls back to the project default rather than failing the run —
/// workflow selection is an optimization over the default, never a hard gate.
/// </summary>
public sealed class CopilotWorkflowSelectionModel : IWorkflowSelectionModel
{
    private const string CoordinatorAgentName = "Coordinator";

    private const string SelectionCharter =
        "You are the Coordinator selecting the single best-fit functional workflow for a task. " +
        "Choose strictly from the provided candidate workflows by matching each workflow's description " +
        "to the task and team. Respond with ONLY the requested JSON object — no prose, no code fences.";

    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly IProjectStore _projectStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CopilotWorkflowSelectionModel> _logger;
    private readonly string? _modelId;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;

    public CopilotWorkflowSelectionModel(
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        IProjectStore projectStore,
        ILoggerFactory loggerFactory,
        IConfiguration configuration)
    {
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _projectStore = projectStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CopilotWorkflowSelectionModel>();
        _modelId = configuration["Providers:GitHubCopilot:Model"];
        _apiBaseUrl = configuration["Agentweaver:ApiBaseUrl"] ?? "http://localhost:5000";
        _apiKey = configuration["Auth:ApiKey"]
            ?? configuration.GetSection("Auth:Keys").GetChildren().FirstOrDefault()?["Token"];
    }

    public async Task<string?> CompleteAsync(
        string prompt, WorkflowSelectionContext context, CancellationToken ct)
    {
        var repositoryPath = await ResolveRepositoryPathAsync(context.ProjectId, ct).ConfigureAwait(false)
                             ?? Directory.GetCurrentDirectory();

        CopilotAIAgent? agent = null;
        try
        {
            agent = new CopilotAIAgent(
                _copilotClientFactory,
                _scopeProvider,
                _sandboxExecutor,
                _sandboxPolicyStore,
                _approvalStore,
                _toolApprovalGate,
                _loggerFactory.CreateLogger<CopilotAIAgent>());

            await agent.SetupAsync(
                workingDirectory: repositoryPath,
                repositoryPath: repositoryPath,
                runId: $"workflow-selection-{context.ProjectId}-{Guid.NewGuid():N}",
                modelId: _modelId,
                systemPromptContext: SelectionCharter,
                streamWriter: null,
                projectId: context.ProjectId,
                agentName: CoordinatorAgentName,
                apiBaseUrl: _apiBaseUrl,
                apiKey: _apiKey,
                ct).ConfigureAwait(false);

            var session = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            return await agent.ExecuteStreamingLoopAsync(prompt, session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Workflow selection model turn failed for project {ProjectId}; selector will use the default.",
                context.ProjectId);
            return null;
        }
        finally
        {
            if (agent is not null)
                await agent.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<string?> ResolveRepositoryPathAsync(string projectId, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParse(projectId, out var guid)) return null;
            var project = await _projectStore.GetAsync(new ProjectId(guid), ct).ConfigureAwait(false);
            return project?.WorkingDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow selection: could not resolve repository for project {ProjectId}.", projectId);
            return null;
        }
    }
}
