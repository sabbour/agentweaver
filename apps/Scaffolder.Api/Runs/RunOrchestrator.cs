using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RunOrchestrator> _logger;

    public RunOrchestrator(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        WorktreeManager worktreeManager,
        RunWorkflowFactory workflowFactory,
        RunWorkflowRegistry registry,
        RunWatchLoopService watchLoop,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<RunOrchestrator> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _worktreeManager = worktreeManager;
        _workflowFactory = workflowFactory;
        _registry = registry;
        _watchLoop = watchLoop;
        _scopeFactory = scopeFactory;
        _configuration = configuration;
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

        var agentCharter = ResolveAgentCharter(run);

        var started = run with
        {
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktreeInfo.WorktreePath,
            WorktreeBranch = worktreeInfo.BranchName,
            AgentCharter = agentCharter,
        };

        await _runStore.InsertAsync(started, ct).ConfigureAwait(false);
        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);

        var (taskWithHarvest, systemPromptContext) = await BuildContextAsync(started, ct);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            taskWithHarvest,
            worktreeInfo.WorktreePath,
            worktreeInfo.BranchName,
            run.RepositoryPath,
            run.OriginatingBranch,
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            systemPromptContext,
            run.ProjectId?.ToString(),
            run.AgentName,
            run.StartedAt);

        // Create the per-run CTS before starting the workflow so the same token reaches both
        // the agent execution and the registry's Abandon path. Using CancellationToken.None as
        // the base avoids cancellation when the HTTP request ends.
        var runCts = new CancellationTokenSource();
        var streamingRun = await _workflowFactory.StartAsync(input, run.Id.ToString(), runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(run.Id.ToString(), streamingRun, runCts);
        _watchLoop.StartWatching(run.Id.ToString(), streamingRun, entry, run.SubmittingUser, entry.Generation, runCt);
    }

    /// <summary>
    /// TryCreateProjectRunAsync (Pending row already in DB). Transitions the row to InProgress
    /// with worktree info, then fires off the workflow. The caller is responsible for
    /// compensating (terminalizing) the Pending row if this method throws.
    /// </summary>
    public async Task StartReservedProjectRunAsync(Run run, CancellationToken ct)
    {
        WorktreeInfo worktreeInfo;
        try
        {
            worktreeInfo = _worktreeManager.AddWorktree(run.RepositoryPath, run.OriginatingBranch, run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree for reserved run {RunId}", run.Id);
            throw;
        }

        await _runStore.UpdateToInProgressAsync(
            run.Id, worktreeInfo.WorktreePath, worktreeInfo.BranchName, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);

        var entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);

        var (taskWithHarvest2, systemPromptContext2) = await BuildContextAsync(run, ct);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            taskWithHarvest2,
            worktreeInfo.WorktreePath,
            worktreeInfo.BranchName,
            run.RepositoryPath,
            run.OriginatingBranch,
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            systemPromptContext2,
            run.ProjectId?.ToString(),
            run.AgentName,
            run.StartedAt);

        // Create the per-run CTS before starting the workflow so the same token reaches both
        // the agent execution and the registry's Abandon path.
        var runCts = new CancellationTokenSource();
        var streamingRun = await _workflowFactory.StartAsync(input, run.Id.ToString(), runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(run.Id.ToString(), streamingRun, runCts);
        _watchLoop.StartWatching(run.Id.ToString(), streamingRun, entry, run.SubmittingUser, entry.Generation, runCt);
    }

    /// <summary>
    /// Starts a fresh workflow execution against the SAME worktree for a revision cycle
    /// (B3 / request-changes). Skips worktree creation — the existing worktree and branch
    /// are reused so the agent builds on top of prior commits. The stream entry is reused
    /// (or recreated if evicted) to preserve full event history for replay. The caller is
    /// responsible for the CAS transition, checkpoint deletion, and audit row insertion
    /// BEFORE invoking this method.
    /// </summary>
    public async Task StartRevisionAsync(Run run, string revisedTask, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(run.WorktreePath))
            throw new InvalidOperationException($"Run {run.Id} has no worktree path; cannot start revision.");
        if (string.IsNullOrEmpty(run.WorktreeBranch))
            throw new InvalidOperationException($"Run {run.Id} has no worktree branch; cannot start revision.");

        // Reuse the existing stream entry so prior events are preserved for replay.
        // If the entry was evicted (rare but possible: TryMarkEvicted ran between Get and
        // BumpGeneration), discard the dead entry and create a fresh live one so that
        // RecordNext calls from the new revision are not silently dropped.
        var entry = _streamStore.Get(run.Id.ToString())
            ?? _streamStore.Create(run.Id.ToString(), run.SubmittingUser);

        var (bumped, generation) = entry.TryBumpGeneration();
        if (!bumped)
        {
            _logger.LogWarning(
                "Stream entry for run {RunId} was evicted before BumpGeneration; recreating a fresh entry.",
                run.Id);
            _streamStore.Remove(run.Id.ToString());
            entry = _streamStore.Create(run.Id.ToString(), run.SubmittingUser);
            generation = entry.BumpGeneration();
        }

        var (taskWithHarvest, systemPromptContext) = await BuildContextAsync(
            run with { Task = revisedTask }, ct);

        var input = new AgentTurnInput(
            run.Id.ToString(),
            taskWithHarvest,
            run.WorktreePath,
            run.WorktreeBranch,
            run.RepositoryPath,
            run.OriginatingBranch,
            run.ModelSource.ToApiString(),
            run.ModelId,
            run.SubmittingUser,
            systemPromptContext,
            run.ProjectId?.ToString(),
            run.AgentName,
            run.StartedAt,
            IsRevision: true);

        // Create the per-run CTS before starting the workflow so the same token reaches both
        // the agent execution and the registry's Abandon path.
        var runCts = new CancellationTokenSource();
        var streamingRun = await _workflowFactory.StartAsync(input, run.Id.ToString(), runCts.Token).ConfigureAwait(false);
        var runCt = _registry.Register(run.Id.ToString(), streamingRun, runCts);
        _watchLoop.StartWatching(run.Id.ToString(), streamingRun, entry, run.SubmittingUser, generation, runCt);
    }

    /// <summary>
    /// Resolves the agent charter for the given run by reading the .squad/agents/{name}/charter.md file.
    /// Tries the lowercase name first (Squad convention), then the original case as a fallback.
    /// Returns null if no AgentName is set or the charter file does not exist.
    /// </summary>
    public string? ResolveAgentCharter(Run run)
    {
        if (string.IsNullOrWhiteSpace(run.AgentName) || string.IsNullOrWhiteSpace(run.RepositoryPath))
            return null;

        // Squad stores agent dirs in lowercase (e.g. .squad/agents/trinity/). Try that first.
        var lowerPath = Path.Combine(run.RepositoryPath, ".squad", "agents",
            run.AgentName.ToLowerInvariant(), "charter.md");
        if (File.Exists(lowerPath))
            return File.ReadAllText(lowerPath);

        // Fallback: original case (e.g. hand-created dirs on case-insensitive filesystems).
        var originalPath = Path.Combine(run.RepositoryPath, ".squad", "agents", run.AgentName, "charter.md");
        if (File.Exists(originalPath))
            return File.ReadAllText(originalPath);

        return null;
    }

    private async Task<(string TaskWithHarvest, string? SystemPromptContext)> BuildContextAsync(
        Run run, CancellationToken ct)
    {
        // Compile memory context (progressive disclosure — layer 1-4)
        string? systemPromptContext = null;
        if (!string.IsNullOrEmpty(run.AgentName) && run.ProjectId.HasValue)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var memoryCompiler = scope.ServiceProvider.GetRequiredService<MemoryContextCompiler>();
                systemPromptContext = await memoryCompiler.CompileAsync(
                    run.ProjectId.Value.ToString(), run.AgentName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Memory context compilation failed for run {RunId} — proceeding without", run.Id);
            }

            // Charter always takes priority regardless of memory compilation success/failure.
            // Resolve it now if the run doesn't already carry it (defensive: covers code paths
            // that skip the endpoint-level load, e.g. StartReservedProjectRunAsync called from tests).
            var charter = !string.IsNullOrEmpty(run.AgentCharter)
                ? run.AgentCharter
                : ResolveAgentCharter(run);

            if (!string.IsNullOrEmpty(charter))
            {
                systemPromptContext = string.IsNullOrEmpty(systemPromptContext)
                    ? charter
                    : charter + "\n\n---\n\n" + systemPromptContext;
            }
            else
            {
                _logger.LogWarning(
                    "No charter found for agent '{AgentName}' in run {RunId} — agent will use vanilla prompt. " +
                    "Expected: {ExpectedPath}",
                    run.AgentName, run.Id,
                    Path.Combine(run.RepositoryPath ?? "(null)", ".squad", "agents",
                        (run.AgentName ?? "").ToLowerInvariant(), "charter.md"));
            }
        }

        return (run.Task, systemPromptContext);
    }
}
