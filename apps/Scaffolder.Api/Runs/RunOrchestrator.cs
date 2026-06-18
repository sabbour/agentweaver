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
    /// Starts a coordinator CHILD run (Feature 008 Phase 2 dispatch). Identical to
    /// <see cref="StartRunAsync"/> except the workflow is built with the TRIMMED child pipeline
    /// (<c>isChild: true</c>): agent + RAI terminating assemble-ready, with no per-child review
    /// gate, merge, or scribe. The supplied <paramref name="run"/> MUST carry
    /// <see cref="Run.ParentRunId"/> (the coordinator run id) and <see cref="Run.SubtaskId"/>.
    /// The existing <see cref="RunWatchLoopService"/> observes the child stream and persists the
    /// assemble-ready terminal exactly as for any other child run; the coordinator's dispatch
    /// service projects <c>subtask.*</c> / <c>coordinator.topology</c> events from that stream.
    /// </summary>
    public async Task StartChildRunAsync(Run run, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(run.ParentRunId))
            throw new InvalidOperationException($"Child run {run.Id} must carry a ParentRunId.");

        WorktreeInfo worktreeInfo;
        try
        {
            worktreeInfo = _worktreeManager.AddWorktree(run.RepositoryPath, run.OriginatingBranch, run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create worktree for child run {RunId}", run.Id);
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
            started.StartedAt);

        var runCts = new CancellationTokenSource();
        var streamingRun = await _workflowFactory
            .StartAsync(input, run.Id.ToString(), runCts.Token, isChild: true)
            .ConfigureAwait(false);
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
    /// (B3 / request-changes, and Feature 008 Phase 2 child steering injection). Skips worktree
    /// creation — the existing worktree and branch are reused so the agent builds on top of prior
    /// commits. The stream entry is reused (or recreated if evicted) to preserve full event history
    /// for replay. The caller is responsible for the CAS transition, checkpoint deletion, and audit
    /// row insertion BEFORE invoking this method. When <paramref name="isChild"/> is true the
    /// revised turn runs the TRIMMED coordinator child pipeline (agent + RAI, no review/merge/scribe
    /// gate), matching how the child was originally launched via <see cref="StartChildRunAsync"/>;
    /// this is the mechanism a queued <c>redirect</c>/<c>amend</c> steering directive uses to inject
    /// the steered instruction at the child's next turn boundary.
    /// </summary>
    public async Task StartRevisionAsync(Run run, string revisedTask, CancellationToken ct, bool isChild = false)
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
        var streamingRun = await _workflowFactory.StartAsync(input, run.Id.ToString(), runCts.Token, isChild).ConfigureAwait(false);
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
        // Coordinator CHILD runs (ParentRunId != null) get a deliberately LEAN, focused system
        // prompt: the child agent's charter (exactly once) plus an explicit working-directory
        // sandbox boundary. They do NOT receive the full coordinator memory/decision stack — that
        // stack duplicated the charter and carried coordinator-style instructions to write artifacts
        // into session-state/.copilot paths that don't exist inside a child's worktree, which the
        // sandbox correctly rejected and stalled the child (Defect C).
        if (!string.IsNullOrEmpty(run.ParentRunId))
        {
            var childCharter = !string.IsNullOrEmpty(run.AgentCharter)
                ? run.AgentCharter
                : ResolveAgentCharter(run);
            return (run.Task, ComposeChildSystemPrompt(childCharter));
        }

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

    /// <summary>
    /// Builds the LEAN system prompt for a coordinator CHILD run: the agent <paramref name="charter"/>
    /// EXACTLY ONCE (when present) followed by an explicit working-directory sandbox boundary. A child
    /// ALWAYS receives the boundary even when it has no charter — this is never null. The boundary is
    /// what prevents the Defect C/#2/#5 stall where a child tried to write a findings brief to a
    /// session-state path, was sandbox-rejected as outside its worktree, and hung. The coordinator
    /// memory/decision stack is deliberately NOT included here.
    /// </summary>
    internal static string ComposeChildSystemPrompt(string? charter)
    {
        const string boundary =
            "## Working-directory sandbox boundary\n" +
            "You are running inside an isolated git worktree. ALL file reads and writes MUST stay " +
            "within your current working directory (this worktree). You must NEVER write to any path " +
            "outside the working directory — including session-state, .copilot, the home directory, " +
            "or temp directories. If a write is rejected because it targets a path outside the " +
            "sandbox, do not retry the same path: adapt and write the file within your current " +
            "working directory instead.";

        return string.IsNullOrEmpty(charter)
            ? boundary
            : charter + "\n\n---\n\n" + boundary;
    }

    /// <summary>
    /// Terminalizes a coordinator CHILD run that failed BEFORE <see cref="StartChildRunAsync"/> could
    /// finish launching (e.g. worktree creation threw before the run row was persisted). Without this,
    /// the dispatched subtask carries a childRunId that <c>GET /api/runs/{childRunId}</c> cannot find,
    /// leaving an empty execution log (Defect B). This persists a terminal FAILED <see cref="Run"/>
    /// row, creates a stream entry, records a <see cref="EventTypes.RunFailed"/> event carrying the
    /// error message, and persists the event so the failed child is retrievable with a non-empty log.
    /// Mirrors how <see cref="RunWatchLoopService"/> terminalizes a failed run. Fully defensive: any
    /// persistence error is swallowed (logged) so it can never throw back into the dispatch loop.
    /// </summary>
    public async Task MarkChildRunFailedAsync(Run run, Exception error, CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var reason = RedactFailureReason(error);
        try
        {
            var now = DateTimeOffset.UtcNow;

            // The run row may not exist yet (worktree creation threw before InsertAsync) or may already
            // be InProgress (a later failure). Update-if-present, otherwise insert a fresh FAILED row.
            var existing = await _runStore.GetAsync(run.Id, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                await _runStore.TrySetTerminalStatusAsync(run.Id, RunStatus.Failed, now, reason, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var failedRow = run with
                {
                    Status = RunStatus.Failed,
                    StartedAt = run.StartedAt == default ? now : run.StartedAt,
                    EndedAt = now,
                    Result = reason,
                };
                await _runStore.InsertAsync(failedRow, ct).ConfigureAwait(false);
            }

            // Ensure a stream entry exists so the RunFailed event has somewhere to land, then record it
            // and close the stream — exactly the store/stream/event pattern RunWatchLoopService uses.
            var entry = _streamStore.Get(runId) ?? _streamStore.Create(runId, run.SubmittingUser);
            entry.RecordNext(EventTypes.RunFailed, new { reason });
            _streamStore.Complete(runId);

            await PersistFailedRunEventsAsync(runId, entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never propagate: the dispatch loop must keep finalizing the subtask regardless.
            _logger.LogError(ex,
                "MarkChildRunFailedAsync failed to terminalize child run {RunId}; subtask will still be marked failed",
                runId);
        }
    }

    /// <summary>
    /// Normalizes an exception into a durable, user-visible failure reason that is safe to persist
    /// into the run event log and serve over the API. Prepends the exception type, masks user-home
    /// path segments (which embed the OS login name / internal layout — info-disclosure, RAI YELLOW),
    /// collapses whitespace, and caps length. The full unredacted exception remains in the server
    /// logger for operators. Defensive: never throws.
    /// </summary>
    internal static string RedactFailureReason(Exception error)
    {
        const int maxLength = 500;
        try
        {
            var text = $"{error.GetType().Name}: {error.Message}";

            // Mask Windows user-home paths: C:\Users\<login>\... -> C:\Users\<redacted>\...
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"(?i)([A-Za-z]:\\Users\\)[^\\""'\s]+", "$1<redacted>");
            // Mask Unix user-home paths: /home/<login>/... and /Users/<login>/...
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"(?i)(/(?:home|Users)/)[^/\s""']+", "$1<redacted>");

            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

            if (text.Length > maxLength)
                text = text[..maxLength] + "…";

            return text.Length == 0 ? error.GetType().Name : text;
        }
        catch
        {
            return error.GetType().Name;
        }
    }

    /// <summary>
    /// Persists the stream's events to the RunEvents table so the failed child's execution log is
    /// retrievable after the in-memory stream is evicted. Mirrors
    /// <see cref="RunWorkflowFactory.PersistRunEventsAsync"/> but is inlined here so the pre-start
    /// failure path has no dependency on a fully constructed workflow factory.
    /// </summary>
    private async Task PersistFailedRunEventsAsync(string runId, RunStreamEntry entry, CancellationToken ct)
    {
        var events = entry.GetSnapshotSince(0).Events;
        if (events.Count == 0)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var existingSeqs = db.RunEvents
            .Where(e => e.RunId == runId)
            .Select(e => e.Sequence)
            .ToHashSet();

        var toInsert = events
            .Where(e => !existingSeqs.Contains(e.Sequence))
            .Select(e => new RunEventRecord
            {
                RunId = runId,
                Sequence = e.Sequence,
                EventType = e.Type,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(e.Payload),
                CreatedAt = DateTime.UtcNow,
            })
            .ToList();

        if (toInsert.Count == 0)
            return;

        db.RunEvents.AddRange(toInsert);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
