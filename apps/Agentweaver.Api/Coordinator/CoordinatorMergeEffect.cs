using Agentweaver.Api.Git;

namespace Agentweaver.Api.Coordinator;

public static class MergeEffectState
{
    public const string Prepared = "prepared";
    public const string NotApplied = "not_applied";
    public const string Applied = "applied";
    public const string Unknown = "unknown";
}

public sealed record CoordinatorMergeEvidence(
    string Outcome,
    string? CurrentTargetCommit,
    string? Reason,
    string? CheckoutOutcome,
    DateTimeOffset ObservedAt);

public sealed record CoordinatorMergeEffect(
    string EffectId,
    int LifecycleGeneration,
    PreparedGitMergeIntent Intent,
    string State,
    CoordinatorMergeEvidence? Evidence,
    string? RecoveryAction);
