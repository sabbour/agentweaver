using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

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
    private const int MaxAgentHostConfigureAttempts = 2;

    private readonly WorktreeManager _worktreeManager;
    private readonly RepositoryMergeLock _mergeLock;
    private readonly RunWorkflowFactory _workflowFactory;
    private readonly GitHubCopilotClientFactory _copilotClientFactory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _sandboxExecutor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;
    private readonly TimeSpan _buildTestTotalTimeout;
    private readonly TimeSpan _buildTestStallTimeout;
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
        ILoggerFactory loggerFactory,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null,
        IConfiguration? configuration = null)
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
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
        _buildTestTotalTimeout = TimeSpan.FromMinutes(Math.Max(
            0.01,
            configuration?.GetValue("Coordinator:AssemblyBuildTestTimeoutMinutes", 20.0) ?? 20.0));
        _buildTestStallTimeout = TimeSpan.FromMinutes(Math.Max(
            0.01,
            configuration?.GetValue("Coordinator:AssemblyBuildTestStallTimeoutMinutes", 12.0) ?? 12.0));
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
            completeSubStream: _workflowFactory.CompleteSubStream,
            agentFactory: _workflowFactory.AgentFactory);

        // The aggregate is already-assembled git state, so we feed the integration diff straight in
        // (no agent turn). RunId = coordinatorRunId routes RAI events onto the coordinator stream.
        // WorktreePath (#236) lets the reviewer read the assembled integration files host-side; the
        // executor threads it into the sandbox root (RaiTurnExecutor.cs:120-122 → CopilotAIAgent).
        var input = new AgentTurnOutput(
            RunId: request.CoordinatorRunId,
            TreeHash: string.Empty,
            Diff: request.AggregateDiff,
            StepCount: 0,
            WorktreePath: request.WorktreePath,
            WorktreeBranch: string.Empty,
            RepositoryPath: request.RepositoryPath,
            OriginatingBranch: string.Empty,
            ContentSafetyFlagged: false,
            SubmittingUser: request.SubmittingUser);

        var output = await rai.HandleAsync(input, NoOpWorkflowContext.Instance, ct).ConfigureAwait(false);
        return new CollectiveRaiResult(
            SafetyFlagged: output.ContentSafetyFlagged,
            RevisionRequested: output.RaiRevisionRequired,
            Feedback: output.RaiFeedback);
    }

    public async Task<CollectiveGateDecision> RunRubberduckAsync(CollectiveRubberduckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.AggregateDiff))
            return new CollectiveGateDecision(Approved: true, RequestChanges: false, Feedback: null);

        var rubberduck = new RubberduckTurnExecutor(
            _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
            _approvalStore, _toolApprovalGate, _loggerFactory,
            _workflowFactory.GetRecordingWriter,
            name: "assembly-rubberduck",
            logicalNodeId: request.GateNodeId ?? "assembly-rubberduck",
            displayLabel: request.DisplayLabel ?? "Rubber-duck review",
            createSubStream: _workflowFactory.CreateSubStreamWriter,
            completeSubStream: _workflowFactory.CompleteSubStream,
            agentFactory: _workflowFactory.AgentFactory);

        var input = new AgentTurnOutput(
            RunId: request.CoordinatorRunId,
            TreeHash: string.Empty,
            Diff: request.AggregateDiff,
            StepCount: 0,
            WorktreePath: request.WorktreePath,
            WorktreeBranch: string.Empty,
            RepositoryPath: request.RepositoryPath,
            OriginatingBranch: string.Empty,
            ContentSafetyFlagged: false,
            SubmittingUser: request.SubmittingUser);

        var decision = await rubberduck.HandleAsync(input, NoOpWorkflowContext.Instance, ct).ConfigureAwait(false);
        return new CollectiveGateDecision(decision.Approved, decision.RequestChanges, decision.Feedback, decision.TargetFiles);
    }

    public async Task<CollectiveGateDecision> RunBuildTestAsync(CollectiveBuildTestRequest request, CancellationToken ct)
    {
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(_buildTestTotalTimeout);
        var gateCt = gateCts.Token;
        WorktreeInfo? detachedWorktree = null;
        try
        {
            detachedWorktree = _worktreeManager.AddDetachedWorktree(
                request.RepositoryPath,
                request.IntegrationBranch,
                BuildTestWorktreeName(request.CoordinatorRunId));

            if (_sandboxRuntime.IsPodPerRun)
            {
                if (_podLifecycle is null)
                {
                    throw new CollectiveBuildTestInfrastructureException(
                        "agenthost_lifecycle_unavailable",
                        "Pod-per-run Build & Test requires IAgentHostPodLifecycle, but it is not configured.",
                        retryable: false);
                }

                for (var launchAttempt = 1; ; launchAttempt++)
                {
                    try
                    {
                        var commitSha = _worktreeManager.GetBranchTipCommitSha(
                            request.RepositoryPath,
                            request.IntegrationBranch);
                        if (!PodLocalExecutionWorkspace.IsGitObjectId(commitSha))
                        {
                            throw new CollectiveBuildTestInfrastructureException(
                                "assembly_integration_commit_unresolved",
                                $"Could not resolve immutable commit SHA for integration ref '{request.IntegrationBranch}'.",
                                retryable: false);
                        }

                        await _podLifecycle.LaunchAgentHostPodAsync(
                            request.CoordinatorRunId,
                            new AgentHostLaunchContext(
                                SharedWorkingDirectory: detachedWorktree.WorktreePath,
                                SourceRepositoryPath: request.RepositoryPath,
                                SourceRef: request.IntegrationBranch,
                                BaseCommitSha: commitSha,
                                ExpectedTreeHash: request.AggregateTreeHash,
                                WorkspaceMode: ExecutionWorkspaceMode.LocalReadOnly,
                                Purpose: AgentHostPurpose.AssemblyBuildTest,
                                ScratchRoot: PodLocalExecutionWorkspace.DefaultScratchRoot),
                            gateCt).ConfigureAwait(false);
                        break;
                    }
                    catch (AgentHostConfigureException ex) when (
                        ex.Retryable && launchAttempt < MaxAgentHostConfigureAttempts)
                    {
                        _logger.LogWarning(
                            ex,
                            "Collective Build/Test: recovering AgentHost configure failure for coordinator run {RunId}; " +
                            "reason={Reason}, recoveryAction={RecoveryAction}, attempt={Attempt}/{MaxAttempts}.",
                            request.CoordinatorRunId,
                            ex.Reason,
                            ex.RecoveryAction,
                            launchAttempt,
                            MaxAgentHostConfigureAttempts);
                        try
                        {
                            await _podLifecycle.ReleaseAgentHostPodAsync(
                                request.CoordinatorRunId, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.LogWarning(
                                cleanupEx,
                                "Collective Build/Test: cleanup before bounded AgentHost configure recovery failed " +
                                "for coordinator run {RunId}; the relaunch will still reconcile the deterministic claim.",
                                request.CoordinatorRunId);
                        }
                    }
                    catch (AgentHostPodReconcilerErrorException ex)
                    {
                        throw new CollectiveBuildTestInfrastructureException(
                            "agenthost_reconciler_error",
                            ex.Message,
                            retryable: false,
                            ex);
                    }
                    catch (AgentHostConfigureException ex)
                    {
                        throw new CollectiveBuildTestInfrastructureException(
                            ex.Reason,
                            ex.Message,
                            retryable: ex.StatusCode == StatusCodes.Status507InsufficientStorage,
                            ex);
                    }
                    catch (CollectiveBuildTestInfrastructureException)
                    {
                        throw;
                    }
                    catch (InvalidOperationException ex) when (
                        ex.Message.Contains("submitting user", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CollectiveBuildTestInfrastructureException(
                            "agenthost_config_missing_submitting_user",
                            ex.Message,
                            retryable: false,
                            ex);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex,
                            "Collective Build/Test: AgentHost pod launch failed for coordinator run {RunId}: {Message}",
                            request.CoordinatorRunId, ex.Message);
                        throw new CollectiveBuildTestInfrastructureException(
                            "agenthost_launch_failed",
                            $"AgentHost pod launch failed for Build & Test: {ex.Message}",
                            retryable: true,
                            ex);
                    }
                }
            }

            var buildTest = new BuildTestTurnExecutor(
                _copilotClientFactory, _scopeProvider, _sandboxExecutor, _sandboxPolicyStore,
                _approvalStore, _toolApprovalGate, _loggerFactory,
                _workflowFactory.GetRecordingWriter,
                name: "assembly-build-test",
                logicalNodeId: request.GateNodeId ?? "assembly-build-test",
                displayLabel: request.DisplayLabel ?? "Build & Test",
                createSubStream: _workflowFactory.CreateSubStreamWriter,
                completeSubStream: _workflowFactory.CompleteSubStream,
                agentFactory: _workflowFactory.AgentFactory,
                agentId: request.AgentId,
                projectId: request.ProjectId,
                apiBaseUrl: _workflowFactory.ApiBaseUrl,
                apiKey: _workflowFactory.ApiKey,
                totalTimeout: _buildTestTotalTimeout,
                stallTimeout: _buildTestStallTimeout);

            var input = new AgentTurnOutput(
                RunId: request.CoordinatorRunId,
                TreeHash: request.AggregateTreeHash,
                Diff: request.AggregateDiff,
                StepCount: 0,
                WorktreePath: detachedWorktree.WorktreePath,
                WorktreeBranch: string.Empty,
                RepositoryPath: request.RepositoryPath,
                OriginatingBranch: string.Empty,
                ContentSafetyFlagged: false,
                SubmittingUser: request.SubmittingUser,
                ProjectId: request.ProjectId,
                AgentName: request.AgentId);

            var decision = await buildTest.HandleAsync(input, NoOpWorkflowContext.Instance, gateCt).ConfigureAwait(false);
            // spec-006 §3.3: do NOT remove the worktree here — the deterministic PreviewStep needs it as
            // its cwd. All worktree/pod teardown is deferred to CleanupBuildTestResourcesAsync.
            return new CollectiveGateDecision(decision.Approved, decision.RequestChanges, decision.Feedback, decision.TargetFiles);
        }
        catch (WorkflowAgentInfrastructureException ex)
        {
            if (ex.Reason is BuildTestTurnExecutor.WallClockTimeoutReason
                or BuildTestTurnExecutor.StallTimeoutReason)
            {
                await CleanupBuildTestResourcesAsync(
                    request.CoordinatorRunId,
                    request.RepositoryPath,
                    CancellationToken.None).ConfigureAwait(false);
            }

            _logger.LogWarning(ex,
                "Collective Build/Test: workflow agent infrastructure failure for coordinator run {RunId}: {Reason}: {Message}",
                request.CoordinatorRunId, ex.Reason, ex.Message);
            throw new CollectiveBuildTestInfrastructureException(
                ex.Reason,
                ex.Message,
                retryable: true,
                ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await CleanupBuildTestResourcesAsync(
                request.CoordinatorRunId,
                request.RepositoryPath,
                CancellationToken.None).ConfigureAwait(false);
            throw new CollectiveBuildTestInfrastructureException(
                BuildTestTurnExecutor.WallClockTimeoutReason,
                $"Collective Build/Test exceeded its total wall-clock timeout of {_buildTestTotalTimeout}.",
                retryable: true,
                ex);
        }
        catch
        {
            if (detachedWorktree is not null && !_sandboxRuntime.IsPodPerRun)
                RemoveDetachedWorktreeBestEffort(request.RepositoryPath, detachedWorktree.WorktreePath);
            throw;
        }
    }

    public async Task CleanupBuildTestResourcesAsync(
        string coordinatorRunId,
        string repositoryPath,
        CancellationToken ct = default)
    {
        if (_sandboxRuntime.IsPodPerRun && _podLifecycle is not null)
        {
            try
            {
                await _podLifecycle.ReleaseAgentHostPodAsync(coordinatorRunId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Collective Build/Test: failed to release AgentHost pod for coordinator run {RunId}",
                    coordinatorRunId);
            }
        }

        var path = _worktreeManager.DetachedWorktreePath(BuildTestWorktreeName(coordinatorRunId));
        try
        {
            _worktreeManager.RemoveDetachedWorktree(repositoryPath, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Collective Build/Test: failed to remove detached worktree {Path}",
                path);
        }
    }

    private static string BuildTestWorktreeName(string coordinatorRunId) =>
        "assembly-build-test-" + coordinatorRunId;

    public string GetBuildTestWorktreePath(string coordinatorRunId) =>
        _worktreeManager.DetachedWorktreePath(BuildTestWorktreeName(coordinatorRunId));

    public string PrepareReviewerWorktree(string coordinatorRunId, string repositoryPath, string integrationBranch)
    {
        // #236: provision a detached worktree at the assembled integration branch so the collective RAI
        // + rubber-duck reviewers can read the integration files host-side. Reuse the SAME pattern (and
        // deterministic name) as RunBuildTestAsync: AddDetachedWorktree destructively recreates the dir
        // (Directory.Delete + prune + `git worktree add --detach`), so reviewer writes can never bleed
        // into a later Build/Test run (Build/Test recreates the same-named worktree fresh), and teardown
        // is handled by the existing CleanupBuildTestResourcesAsync path — no extra cleanup wiring.
        var info = _worktreeManager.AddDetachedWorktree(
            repositoryPath,
            integrationBranch,
            BuildTestWorktreeName(coordinatorRunId));
        return info.WorktreePath;
    }

    private void RemoveDetachedWorktreeBestEffort(string repositoryPath, string worktreePath)
    {
        try
        {
            _worktreeManager.RemoveDetachedWorktree(repositoryPath, worktreePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Collective Build/Test: failed to remove detached worktree {Path}",
                worktreePath);
        }
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
                MergeOutcomeKind.Blocked => CollectiveMergeResult.Failed(outcome.Reason ?? "blocked", outcome.ConflictingFiles),
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
            apiKey: _workflowFactory.ApiKey,
            agentFactory: _workflowFactory.AgentFactory);

        var input = new ScribeTurnInput(
            RunId: request.CoordinatorRunId,
            ProjectId: request.ProjectId ?? string.Empty,
            AgentName: request.AgentName,
            RunStartedAt: request.RunStartedAt,
            RepositoryPath: request.RepositoryPath,
            ModelSource: request.ModelSource,
            ModelId: request.ModelId,
            TerminalStatus: request.TerminalStatus,
            MergeResult: request.MergeResult,
            SubmittingUser: request.SubmittingUser);

        await scribe.HandleAsync(input, NoOpWorkflowContext.Instance, ct).ConfigureAwait(false);
    }
}
