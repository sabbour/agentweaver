namespace Scaffolder.AgentRuntime.Workflow;

/// <summary>Input to the agent turn executor (workflow entry point).</summary>
public sealed record AgentTurnInput(
    string RunId,
    string Task,
    string WorktreePath,
    string WorktreeBranch,
    string RepositoryPath,
    string OriginatingBranch,
    string ModelSource);

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
    bool ContentSafetyFlagged);

/// <summary>Data surfaced to the external caller via the review request port.</summary>
public sealed record WorkflowReviewRequest(
    string RunId,
    string TreeHash,
    string Diff,
    int StepCount);

/// <summary>Response provided by the human reviewer through the request port.</summary>
public sealed record WorkflowReviewDecision(bool Approved);

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
