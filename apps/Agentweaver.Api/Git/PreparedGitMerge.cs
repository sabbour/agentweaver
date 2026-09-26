namespace Agentweaver.Api.Git;

public enum PreparedGitMergeKind
{
    FastForward,
    MergeCommit,
    AlreadyApplied,
}

public sealed record PreparedGitCheckoutState(
    string WorktreePath,
    string FullRef,
    string HeadCommit,
    string IndexTree);

public sealed record PreparedGitMergeIntent(
    string EffectId,
    int LifecycleGeneration,
    string RepositoryIdentity,
    string SourceRef,
    string SourceCommit,
    string SourceTree,
    string TargetRef,
    string ExpectedTargetCommit,
    string IntendedCommit,
    string IntendedTree,
    PreparedGitMergeKind Kind,
    PreparedGitCheckoutState? CheckedOutState);

public enum PrepareGitMergeOutcome
{
    Prepared,
    Conflict,
    Failed,
}

public sealed record PrepareGitMergeResult(
    PrepareGitMergeOutcome Outcome,
    PreparedGitMergeIntent? Intent = null,
    string? Reason = null,
    IReadOnlyList<string>? ConflictingFiles = null);

public enum ApplyPreparedGitMergeOutcome
{
    AppliedNow,
    RecoveredApplied,
    NotApplied,
    Unknown,
}

public sealed record ApplyPreparedGitMergeResult(
    ApplyPreparedGitMergeOutcome Outcome,
    string? CurrentTargetCommit,
    string? Reason = null,
    string? CheckoutOutcome = null);
