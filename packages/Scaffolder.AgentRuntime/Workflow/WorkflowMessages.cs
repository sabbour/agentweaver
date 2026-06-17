namespace Scaffolder.AgentRuntime.Workflow;

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
    int Iteration = 0);

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
    string? Feedback = null);

/// <summary>Input to the merge executor.</summary>
public sealed record MergeInput(
    string RunId,
    string TreeHash,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch);

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
    string? MergeMode = null);

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
