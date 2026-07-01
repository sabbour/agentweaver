using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Production <see cref="ICollectiveAssemblyPipeline"/> (D3). REUSES the existing executors and git
/// plumbing rather than re-implementing them:
/// <list type="bullet">
/// <item>Integration branch — <see cref="WorktreeManager.BuildIntegrationBranch"/> (headless tree merges).</item>
/// <item>Collective RAI — the SAME <see cref="RaiTurnExecutor"/> used by the per-run pipeline, fed the
/// AGGREGATE diff and invoked directly via a no-op <see cref="IWorkflowContext"/> (the executor never
/// touches the context — it only resolves the Rai charter, runs the Rai agent in the sandbox, and
/// emits to the coordinator's sub-stream).</item>
/// <item>Collective merge — <see cref="WorktreeManager.MergeWorktree"/> serialized by
/// <see cref="RepositoryMergeLock"/> (same primitive the single-run merge uses).</item>
/// <item>Collective scribe — the SAME <see cref="ScribeTurnExecutor"/>, invoked the same way.</item>
/// </list>
/// Writer/sub-stream seams are borrowed from <see cref="RunWorkflowFactory"/> so RAI/Scribe events land
/// on the coordinator run's stream exactly as per-run events do.
/// </summary>
public sealed class CollectiveAssemblyPipeline : ICollectiveAssemblyPipeline
{
    private readonly WorktreeManager _worktreeManager;
    private readonly RepositoryMergeLock _mergeLock;
    private readonly RunWorkflowFactory _workflowFactory;
    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CollectiveAssemblyPipeline> _logger;

    public CollectiveAssemblyPipeline(
        WorktreeManager worktreeManager,
        RepositoryMergeLock mergeLock,
        RunWorkflowFactory workflowFactory,
        GitHubCopilotClientFactory copilotClientFactory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor sandboxExecutor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILoggerFactory loggerFactory)
    {
        _worktreeManager = worktreeManager;
        _mergeLock = mergeLock;
        _workflowFactory = workflowFactory;
        _copilotClientFactory = copilotClientFactory;
        _scopeProvider = scopeProvider;
        _sandboxExecutor = sandboxExecutor;
        _sandboxPolicyStore = sandboxPolicyStore;
        _approvalStore = approvalStore;
        _toolApprovalGate = toolApprovalGate;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CollectiveAssemblyPipeline>();
    }

    public IntegrationBranchResult BuildIntegrationBranch(CollectiveIntegrationRequest request) =>
        _worktreeManager.BuildIntegrationBranch(
            request.RepositoryPath,
            request.OriginatingBranch,
            request.IntegrationBranch,
            request.ChildBranchesInOrder);

    public void PrepareIntegrationBranchRetry(CollectiveIntegrationRequest request) =>
        _worktreeManager.TryCleanIntegrationRetryArtifacts(
            request.RepositoryPath,
            request.IntegrationBranch);

    public async Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.AggregateDiff))
            return new CollectiveRaiResult(SafetyFlagged: false);

        var rai = new RaiTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory,
            _workflowFactory.GetRecordingWriter,
            name: "assembly-rai",
            createSubStream: _workflowFactory.CreateSubStreamWriter,
            completeSubStream: _workflowFactory.CompleteSubStream);

        // The aggregate is already-assembled git state, so we feed the integration diff straight in
        // (no agent turn). RunId = coordinatorRunId routes RAI events onto the coordinator stream.
        var input = new AgentTurnOutput(
            RunId: request.CoordinatorRunId,
            TreeHash: string.Empty,
            Diff: request.AggregateDiff,
            StepCount: 0,
            WorktreePath: string.Empty,
            WorktreeBranch: string.Empty,
            RepositoryPath: request.RepositoryPath,
            OriginatingBranch: string.Empty,
            ContentSafetyFlagged: false);

        var output = await rai.HandleAsync(input, NoOpWorkflowContext.Instance, ct).ConfigureAwait(false);
        return new CollectiveRaiResult(SafetyFlagged: output.ContentSafetyFlagged);
    }

    public async Task<CollectiveMergeResult> MergeAsync(CollectiveMergeRequest request, CancellationToken ct)
    {
        string canonicalPath;
        try { canonicalPath = Path.GetFullPath(request.RepositoryPath); }
        catch { return CollectiveMergeResult.Failed("invalid_repository_path"); }

        var lockHandle = await _mergeLock.TryAcquireAsync(canonicalPath, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (lockHandle is null)
            return CollectiveMergeResult.Failed("repository_busy");

        try
        {
            var outcome = _worktreeManager.MergeWorktree(
                request.RepositoryPath, request.OriginatingBranch, request.IntegrationBranch, request.TreeHash);

            return outcome.Kind switch
            {
                MergeOutcomeKind.Merged => CollectiveMergeResult.Merged(outcome.CommitHash),
                MergeOutcomeKind.Conflict => CollectiveMergeResult.Conflict(outcome.ConflictingFiles ?? [], outcome.Reason),
                MergeOutcomeKind.Blocked => CollectiveMergeResult.Failed(outcome.Reason ?? "blocked"),
                _ => CollectiveMergeResult.Failed("unexpected_merge_outcome"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Collective merge threw for coordinator run {RunId}", request.CoordinatorRunId);
            return CollectiveMergeResult.Failed("unexpected_error");
        }
        finally
        {
            lockHandle.Dispose();
        }
    }

    public async Task RunScribeAsync(CollectiveScribeRequest request, CancellationToken ct)
    {
        var scribe = new ScribeTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory,
            _workflowFactory.GetRecordingWriter,
            name: "assembly-scribe",
            createSubStream: _workflowFactory.CreateSubStreamWriter,
            completeSubStream: _workflowFactory.CompleteSubStream,
            apiBaseUrl: _workflowFactory.ApiBaseUrl,
            apiKey: _workflowFactory.ApiKey);

        var input = new ScribeTurnInput(
            RunId: request.CoordinatorRunId,
            ProjectId: request.ProjectId ?? string.Empty,
            AgentName: request.AgentName,
            RunStartedAt: request.RunStartedAt,
            RepositoryPath: request.RepositoryPath,
            ModelSource: request.ModelSource,
            ModelId: request.ModelId,
            TerminalStatus: request.TerminalStatus,
            MergeResult: request.MergeResult);

        await scribe.HandleAsync(input, NoOpWorkflowContext.Instance, ct).ConfigureAwait(false);
    }
}
