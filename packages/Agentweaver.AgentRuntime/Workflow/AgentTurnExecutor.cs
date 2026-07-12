using System.Threading.Channels;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Executor that runs the agent turn: drives a <see cref="CopilotAIAgent"/> (which the MAF
/// checkpoint manager can serialize), commits the worktree, computes the diff, and returns
/// AgentTurnOutput. Token deltas stream through the existing side-channel
/// (RecordingChannelWriter) and are invisible to MAF.
/// </summary>
public sealed class AgentTurnExecutor : Executor<AgentTurnInput, AgentTurnOutput>, IWorkflowNodeMeta
{
    /// <inheritdoc />
    public string LogicalNodeId { get; }
    /// <inheritdoc />
    public string DisplayLabel { get; }
    /// <inheritdoc />
    public string Role => "agent";
    /// <inheritdoc />
    public string NodeType => "agent";
    /// <inheritdoc />
    public bool Hidden => false;
    /// <inheritdoc />
    public string NodeKind => "live";

    private readonly IWorkflowTurnAgent _agent;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<AgentTurnExecutor> _logger;
    private readonly Func<string, ChannelWriter<RunEvent>?> _getRecordingWriter;
    private readonly string? _apiBaseUrl;
    private readonly string? _apiKey;
    private readonly string? _agentNodeCharter;
    private readonly string? _agentNodePrompt;
    private readonly bool _emitTerminalFailureOutput;

    public AgentTurnExecutor(
        IWorkflowTurnAgent agent,
        IWorktreeOperations worktreeOps,
        Func<string, ChannelWriter<RunEvent>?> getRecordingWriter,
        ILogger<AgentTurnExecutor> logger,
        string? apiBaseUrl = null,
        string? apiKey = null,
        string? agentNodeCharter = null,
        string? agentNodePrompt = null,
        bool emitTerminalFailureOutput = false,
        string name = "agent-turn",
        string logicalNodeId = "agent",
        string displayLabel = "Agent")
        : base(name)
    {
        LogicalNodeId = logicalNodeId;
        DisplayLabel = displayLabel;
        _agent = agent;
        _worktreeOps = worktreeOps;
        _getRecordingWriter = getRecordingWriter;
        _logger = logger;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _agentNodeCharter = string.IsNullOrWhiteSpace(agentNodeCharter) ? null : agentNodeCharter;
        _agentNodePrompt = string.IsNullOrWhiteSpace(agentNodePrompt) ? null : agentNodePrompt;
        _emitTerminalFailureOutput = emitTerminalFailureOutput;
    }

    public override async ValueTask<AgentTurnOutput> HandleAsync(
        AgentTurnInput input, IWorkflowContext context, CancellationToken ct)
    {
        var writer = _getRecordingWriter(input.RunId);
        bool safetyFlagged = false;

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "started", DisplayLabel,
            agentName: input.AgentName);

        try
        {
            // When the workflow node declared a bespoke inline charter (a role with no catalog
            // charter), prepend it to the run's system prompt so the agent adopts the authored
            // persona. Skipped when the run already carries the same charter (e.g. the node used a
            // catalog role whose charter was resolved upstream into SystemPromptContext).
            var systemPromptContext = input.SystemPromptContext;
            if (_agentNodeCharter is not null &&
                (string.IsNullOrEmpty(systemPromptContext) ||
                 !systemPromptContext.Contains(_agentNodeCharter, StringComparison.Ordinal)))
            {
                systemPromptContext = string.IsNullOrEmpty(systemPromptContext)
                    ? _agentNodeCharter
                    : _agentNodeCharter + "\n\n---\n\n" + systemPromptContext;
            }

            await _agent.SetupAsync(
                input.WorktreePath,
                input.RepositoryPath,
                input.RunId,
                input.ModelId,
                systemPromptContext,
                writer,
                input.ProjectId,
                input.AgentName,
                _apiBaseUrl,
                _apiKey,
                ct,
                input.SubmittingUser).ConfigureAwait(false);

            var task = input.IsRevision || _agentNodePrompt is null
                ? input.Task
                : _agentNodePrompt;
            await _agent.RunTurnAsync(task, input.IsRevision, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsContentSafetyViolation(ex))
        {
            _logger.LogWarning(ex, "Content safety violation detected for run {RunId}", input.RunId);
            safetyFlagged = true;
        }
        catch
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            throw;
        }

        if (safetyFlagged)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel);
            return new AgentTurnOutput(
                input.RunId,
                TreeHash: string.Empty,
                Diff: string.Empty,
                StepCount: 0,
                input.WorktreePath,
                input.WorktreeBranch,
                input.RepositoryPath,
                input.OriginatingBranch,
                ContentSafetyFlagged: true,
                Iteration: input.Iteration,
                SubmittingUser: input.SubmittingUser,
                ProjectId: input.ProjectId,
                AgentName: input.AgentName);
        }

        // POST-TURN BOOKKEEPING. The agent turn itself has already completed here (agent.turn.end
        // was emitted). CommitChanges is the only operation below that can throw — GetDiff and
        // GetStepCount are best-effort and swallow their own errors.
        //
        // ROOT CAUSE (in-place steering revision wedge): the coordinator CHILD pipeline is a trimmed
        // graph (agent -> child-assemble-ready) with NO failure->terminal edge, so any executor throw
        // hangs the stream (RunWatchLoopService then fails the run with
        // `watch_stream_completed_without_terminal_event`). The observed trigger was a TRANSIENT
        // LibGit2 worktree-state error on a resumed revision (a lingering child process holding
        // index.lock — the benign 'kill needs PID' tool.error seen live). Two-part handling:
        //   1. TRANSIENT: retry the commit a bounded number of times so a flaky lock/index/ref error
        //      still commits the revision's edits and the child terminalizes assemble-ready on the
        //      SAME worktree (context preserved — no fresh pod).
        //   2. PERSISTENT: after retries are exhausted, do NOT fabricate a no-change assemble-ready.
        //      That would silently DROP the revision's uncommitted edits and hide the failure. Emit a
        //      visible failed step and rethrow: the child run terminalizes as a VISIBLE Failure (the
        //      watch loop converts a child ExecutorFailedEvent into a terminal Failed run), which
        //      marks the subtask failed so the coordinator consciously re-dispatches the revision
        //      (steering feedback preserved) instead of losing work inside a fake success.
        string treeHash;
        var commitDiagnostics = new List<string>();
        try
        {
            treeHash = await CommitChangesWithRetryAsync(
                input.WorktreePath, input.RunId, commitDiagnostics, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "failed", DisplayLabel,
                agentName: input.AgentName);

            // FIX 2 (graph-native failure->terminal): in the trimmed child/revision pipeline the
            // executor is constructed with emitTerminalFailureOutput=true. A PERSISTENT post-turn
            // commit fault (the bounded clear+retry could not clear the blocker) is RETURNED as a
            // typed AgentTurnOutput carrying TerminalFailureReason — the child graph's conditional
            // edge routes it to the child-turn-failed terminal (exactly one WorkflowOutputEvent),
            // instead of a bare rethrow that only the watcher stream-abort backstop could catch. We
            // still NEVER fabricate a no-change assemble_ready — the failure is VISIBLE, with evidence.
            var evidence = BuildCommitFailureEvidence(ex, commitDiagnostics);
            if (_emitTerminalFailureOutput)
            {
                _logger.LogError(ex,
                    "Post-turn CommitChanges failed for child run {RunId} after bounded clear+retry; emitting graph-native child-turn-failed terminal (evidence: {Evidence})",
                    input.RunId, evidence);
                return new AgentTurnOutput(
                    input.RunId,
                    TreeHash: string.Empty,
                    Diff: string.Empty,
                    StepCount: 0,
                    input.WorktreePath,
                    input.WorktreeBranch,
                    input.RepositoryPath,
                    input.OriginatingBranch,
                    ContentSafetyFlagged: false,
                    Iteration: input.Iteration,
                    SubmittingUser: input.SubmittingUser,
                    ProjectId: input.ProjectId,
                    AgentName: input.AgentName,
                    TerminalFailureReason: "commit_failed_persistent",
                    TerminalFailureEvidence: evidence);
            }

            // Full pipeline: preserve existing behavior — rethrow so the fault terminalizes via the
            // watcher's ExecutorFailedEvent backstop (never a silent no-change success).
            _logger.LogError(ex,
                "Post-turn CommitChanges failed for run {RunId} after bounded clear+retry; terminalizing the run as a visible failure (evidence: {Evidence})",
                input.RunId, evidence);
            throw;
        }

        var diff = _worktreeOps.GetDiff(input.RepositoryPath, input.OriginatingBranch, input.WorktreeBranch);
        var stepCount = _worktreeOps.GetStepCount(input.RunId);

        WorkflowStepEvents.Emit(writer, _logger, input.RunId, LogicalNodeId, "completed", DisplayLabel,
            agentName: input.AgentName);

        return new AgentTurnOutput(
            input.RunId,
            treeHash,
            diff,
            stepCount,
            input.WorktreePath,
            input.WorktreeBranch,
            input.RepositoryPath,
            input.OriginatingBranch,
            ContentSafetyFlagged: false,
            Iteration: input.Iteration,
            SubmittingUser: input.SubmittingUser,
            ProjectId: input.ProjectId,
            AgentName: input.AgentName);
    }

    /// <summary>
    /// Commits the worktree with a bounded retry so a TRANSIENT git failure (a LibGit2 index.lock /
    /// ref race — e.g. a lingering child process briefly holding the lock) does not strand the run.
    /// The executor is intentionally decoupled from LibGit2Sharp types (it only sees
    /// <see cref="IWorktreeOperations"/>), so retryability is by bounded attempts rather than by
    /// exception-type classification.
    /// <para>
    /// FIX 1 (context-preserving retry — the rate driver): between attempts it asks the worktree to
    /// clear a STALE index.lock (conservatively: real gitdir resolution, age threshold, live-process
    /// guard). This is what converts the common in-place-revision wedge (a crashed/lingering process
    /// left the index locked) from a lost-context <c>dispatch_fresh</c> into a clean commit on the
    /// SAME worktree. Each clear attempt's diagnostics are appended to <paramref name="diagnostics"/>
    /// for the child-turn-failed evidence trail. NOTE: no process-group reap is performed — there is
    /// no run-owned PID/process-group tracking to make that ownership-proven (Registry.Abandon only
    /// cancels the CTS), so we deliberately rely on stale-lock handling + a VISIBLE failure instead of
    /// killing by path/name (which could reap an unrelated process).
    /// </para>
    /// A genuinely PERSISTENT failure still surfaces after the final attempt — the caller then
    /// terminalizes the run visibly (typed child-turn-failed output, or rethrow in the full pipeline)
    /// instead of fabricating a no-change success.
    /// </summary>
    private async Task<string> CommitChangesWithRetryAsync(
        string worktreePath, string runId, List<string> diagnostics, CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _worktreeOps.CommitChanges(worktreePath, runId);
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex,
                    "Post-turn CommitChanges failed for run {RunId} (attempt {Attempt}/{MaxAttempts}); clearing stale index.lock then retrying",
                    runId, attempt, maxAttempts);

                // Clear a stale index.lock the lingering/crashed process left behind, so the retry
                // can actually succeed (a plain time-backoff retry re-hits the same held lock).
                try
                {
                    var clear = _worktreeOps.TryClearStaleIndexLock(worktreePath);
                    diagnostics.Add(
                        $"attempt{attempt}: lock_present={clear.LockPresent} cleared={clear.Cleared} " +
                        $"age_s={(clear.LockAgeSeconds.HasValue ? clear.LockAgeSeconds.Value.ToString("F1") : "n/a")} " +
                        $"live_git_proc={clear.LiveGitProcessDetected} detail={clear.Detail}");
                }
                catch (Exception clearEx)
                {
                    diagnostics.Add($"attempt{attempt}: index_lock_clear_error={clearEx.GetType().Name}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static string BuildCommitFailureEvidence(Exception ex, List<string> diagnostics)
    {
        var exSummary = $"exception={ex.GetType().Name}: {ex.Message}";
        return diagnostics.Count == 0
            ? exSummary
            : exSummary + " | " + string.Join(" | ", diagnostics);
    }

    private static bool IsContentSafetyViolation(Exception ex)
    {
        // The governance kernel throws with a recognizable message pattern when
        // content safety policy is violated. Match on type name to avoid coupling
        // to the governance package's internal exception type.
        return ex.GetType().Name.Contains("ContentSafety", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("content safety", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);
    }
}
