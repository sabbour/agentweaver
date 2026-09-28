namespace Agentweaver.Domain;

public sealed record BacklogDependencyEdit(
    BacklogTaskId TaskId,
    IReadOnlyList<BacklogTaskId> Add,
    IReadOnlyList<BacklogTaskId> Remove,
    IReadOnlyList<BacklogTaskId>? Replace = null);

public sealed record BacklogDependencyEditResult(
    long Revision,
    IReadOnlyList<BacklogTaskId> Prerequisites,
    IReadOnlyList<BacklogTaskId> AffectedTaskIds,
    bool Changed);

public sealed class BacklogDependencyEditException(string code) : Exception(code);

public sealed record BacklogClaimedPrerequisite(
    string TaskId, string RunId, string Outcome,
    int LifecycleGeneration,
    string? MergedCommitHash, string? TreeHash,
    string? ExecutableWorkflowContentDigest,
    string? OutputRevisionId = null);
