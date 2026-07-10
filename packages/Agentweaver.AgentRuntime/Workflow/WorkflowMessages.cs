namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>Input to the agent turn executor (workflow entry point).</summary>
public sealed record AgentTurnInput(
    string RunId,
    string Task,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch,
    string ModelSource,
    string? ModelId,
    string SubmittingUser,
    string? SystemPromptContext = null,
    string? ProjectId = null,
    string? AgentName = null,
    DateTimeOffset? RunStartedAt = null,
    /// <summary>Revision loop counter. Incremented each time Rai or a reviewer sends work back.</summary>
    int Iteration = 0,
    /// <summary>Set by revision adapters when the iteration cap is reached; routes to terminal.</summary>
    bool MaxIterationsReached = false,
    /// <summary>True when this turn continues an existing session (reviewer requested changes). Causes <see cref="CopilotAIAgent.ResumeSessionAsync"/> to be called instead of CreateSessionAsync.</summary>
    bool IsRevision = false);

/// <summary>Output from the agent turn executor, consumed by conditional edges.</summary>
public sealed record AgentTurnOutput(
    string RunId,
    string TreeHash,
    string Diff,
    int StepCount,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch,
    bool ContentSafetyFlagged,
    /// <summary>Rai issued a REVISE verdict: agent should retry with <see cref="RaiFeedback"/>.</summary>
    bool RaiRevisionRequired = false,
    /// <summary>Rai's feedback text when <see cref="RaiRevisionRequired"/> is true.</summary>
    string? RaiFeedback = null,
    /// <summary>Carried through from <see cref="AgentTurnInput.Iteration"/> for edge conditions.</summary>
    int Iteration = 0,
    /// <summary>The accountable human whose Copilot-entitled token must be used by downstream model turns.</summary>
    string? SubmittingUser = null,
    /// <summary>Project context carried forward for downstream gates that expose Agentweaver API tools.</summary>
    string? ProjectId = null,
    /// <summary>Agent context carried forward for downstream gates that expose Agentweaver API tools.</summary>
    string? AgentName = null,
    /// <summary>
    /// Set (non-null) by the agent executor when a PERSISTENT post-turn commit fault could not be
    /// cleared by the bounded clear+retry. Null = success. Drives the child graph's conditional
    /// failure->terminal edge (agent -> child-turn-failed) so the fault terminalizes as a graph-native
    /// <see cref="ChildTurnFailedOutput"/> instead of a bare rethrow. Only produced in the trimmed
    /// child/revision pipeline (the executor is constructed with the terminal-failure flag there);
    /// the full pipeline keeps rethrowing to the watcher backstop.
    /// </summary>
    string? TerminalFailureReason = null,
    /// <summary>Structured diagnostics for <see cref="TerminalFailureReason"/> (exception summary,
    /// gitdir lock path, lock age, whether the stale-lock clear ran, live-process detection).</summary>
    string? TerminalFailureEvidence = null);

/// <summary>Data surfaced to the external caller via the review request port.</summary>
public sealed record WorkflowReviewRequest(
    string RunId,
    string TreeHash,
    string Diff,
    int StepCount,
    /// <summary>True when Rai flagged a safety concern; the reviewer sees this as advisory context.</summary>
    bool RaiSafetyFlagged = false);

/// <summary>Response provided by the human reviewer through the request port.</summary>
public sealed record WorkflowReviewDecision(
    bool Approved,
    /// <summary>True when the reviewer wants the agent to revise rather than hard-declining.</summary>
    bool RequestChanges = false,
    /// <summary>Reviewer's feedback text sent back to the agent for the next iteration.</summary>
    string? Feedback = null,
    /// <summary>The human reviewer that approved the irreversible action, when applicable.</summary>
    string? ReviewedBy = null);

/// <summary>Input to the merge executor.</summary>
public sealed record MergeInput(
    string RunId,
    string TreeHash,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch,
    string? ReviewedBy = null);

/// <summary>Output from the merge executor (terminal workflow output).</summary>
public sealed record MergeOutput(string RunId, string Status, string? MergeResult, string? MergeMode = null);

/// <summary>Terminal output for runs that produce no changes.</summary>
public sealed record NoChangesOutput(string RunId);

/// <summary>Terminal output for declined reviews.</summary>
public sealed record DeclinedOutput(string RunId);

/// <summary>Terminal output for content-safety-flagged runs.</summary>
public sealed record ContentSafetyFailedOutput(string RunId);

/// <summary>
/// Terminal output for a coordinator CHILD run (ParentRunId != null). Produced by the
/// <c>child-assemble-ready</c> executor at the end of the trimmed child pipeline
/// (agentInputStorer -> agent -> RAI). The child does NOT run its own review gate, merge, or
/// scribe — those happen ONCE collectively over all children in Phase 3.
/// <para>
/// This record is the hand-off contract the coordinator's dispatch/assemble wave reads to
/// collect each child's produced tree: <see cref="WorktreeBranch"/> identifies the child's
/// isolated branch and <see cref="TreeHash"/> pins the exact tree it produced.
/// <see cref="HasChanges"/> is false for an empty-diff (no-op) child, which is still a valid
/// assemble-ready outcome.
/// </para>
/// </summary>
public sealed record AssembleReadyOutput(
    string RunId,
    string WorktreeBranch,
    string TreeHash,
    string Diff,
    bool HasChanges,
    int StepCount,
    /// <summary>True when RAI flagged a safety concern; carried forward so the collective gate sees it.</summary>
    bool RaiSafetyFlagged = false);

/// <summary>
/// Terminal output for a coordinator CHILD run whose agent turn ended cleanly but whose POST-TURN
/// commit failed PERSISTENTLY (the bounded clear+retry could not clear the blocker). Emitted by the
/// <c>child-turn-failed</c> executor via the child graph's conditional failure->terminal edge — a
/// graph-native, single terminal <c>WorkflowOutputEvent</c> (never a bare rethrow, never a fabricated
/// no-change assemble_ready). The watch loop maps this to a VISIBLE run failure so the coordinator
/// consciously re-dispatches the revision (steering feedback preserved) rather than losing work.
/// </summary>
public sealed record ChildTurnFailedOutput(
    string RunId,
    string Reason,
    /// <summary>Structured diagnostics (commit exception summary, gitdir lock path + age, whether the
    /// stale-lock clear ran, live-process detection) for live debugging of the persistent fault.</summary>
    string? Evidence = null);

/// <summary>Input to the Scribe agent turn, carrying context + terminal output for pass-through.</summary>
public sealed record ScribeTurnInput(
    string RunId,
    string ProjectId,
    string AgentName,
    DateTimeOffset RunStartedAt,
    string RepositoryPath,
    string ModelSource,
    string? ModelId,
    // Terminal output data so output adapters can reconstruct MergeOutput/NoChangesOutput
    string? TerminalStatus = null,
    string? MergeResult = null,
    string? MergeMode = null,
    string? SubmittingUser = null);

/// <summary>Input to the Rai RAI-review agent turn.</summary>
public sealed record RaiTurnInput(
    string RunId,
    string ProjectId,
    string AgentName,
    DateTimeOffset RunStartedAt,
    string RepositoryPath,
    string ModelSource,
    string? ModelId,
    string? Diff);
