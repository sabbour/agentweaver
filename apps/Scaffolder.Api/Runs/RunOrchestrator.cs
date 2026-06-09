using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Thin launcher: creates the worktree, persists the run, and hands off to the
/// MAF workflow via RunWorkflowFactory. The workflow owns lifecycle, HITL,
/// checkpointing, and merge orchestration.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly WorktreeManager _worktreeManager;
    private readonly RunWorkflowFactory _workflowFactory;
    private readonly RunWorkflowRegistry _registry;
    private readonly RunWatchLoopService _watchLoop;
    private readonly ILogger<RunOrchestrator> _logger;

    public RunOrchestrator(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        WorktreeManager worktreeManager,
        RunWorkflowFactory workflowFactory,
        RunWorkflowRegistry registry,
        RunWatchLoopService watchLoop,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _worktreeManager = worktreeManager;
        _workflowFactory = workflowFactory;
        _registry = registry;
        _watchLoop = watchLoop;
        _logger = logger;
    }

    public async Task StartRunAsync(Run run, CancellationToken ct)
    {
        WorktreeInfo worktreeInfo;
        try
        {
            worktreeInfo = _worktreeManager.AddWorktree(run.RepositoryPath, run.OriginatingBranch, run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree for run {RunId}", run.Id);
            throw;
        }

        var started = run with
        {
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktreeInfo.WorktreePath,
            WorktreeBranch = worktreeInfo.BranchName,
        };

        await _runStore.InsertAsync(started, ct).ConfigureAwait(false);
        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            run.Task,
            worktreeInfo.WorktreePath,
            worktreeInfo.BranchName,
            run.RepositoryPath,
            run.OriginatingBranch,
            run.ModelSource.ToApiString());

        var streamingRun = await _workflowFactory.StartAsync(input, run.Id.ToString(), ct).ConfigureAwait(false);
        _registry.Register(run.Id.ToString(), streamingRun);
        _watchLoop.StartWatching(run.Id.ToString(), streamingRun, entry, run.SubmittingUser);
    }
}
