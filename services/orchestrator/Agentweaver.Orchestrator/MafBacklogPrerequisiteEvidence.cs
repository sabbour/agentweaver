using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

internal sealed record MafBacklogTaskClaimTuple(
    BacklogTaskReference Task,
    long TaskRevision,
    long ClaimTaskRevision,
    BacklogTaskState TaskState,
    bool IsArchived,
    bool IsAutomationInvocationPending,
    Guid ClaimId,
    // Descriptive persisted state only; it is not a proof predicate.
    string Phase,
    string ClaimIdempotencyKey,
    string ClaimRequestHash,
    string ClaimRunId,
    string ClaimRootSessionId,
    string ClaimSelectionHash,
    long ClaimExecutionFence,
    long ClaimDecisionStateVersion);

internal sealed record MafBacklogOutputObligation(
    string WorkItemId,
    string OutputPath);

internal sealed record MafBacklogOutputSetSnapshot(
    string WorkPlanId,
    string Digest,
    ImmutableArray<MafBacklogOutputObligation> Obligations);

internal sealed record MafBacklogAcceptedRootEvidence(
    CoordinationActor Actor,
    SessionIdentity Identity,
    string AcceptedSelectionHash,
    long ExecutionFence,
    CoordinatorDecisionCurrentState CurrentDecision,
    MafExecutionCheckpointSnapshot Checkpoint,
    WorkPlanSnapshot WorkPlan,
    MafBacklogOutputSetSnapshot OutputSet);

internal abstract record MafBacklogOwnerProof;

internal sealed record MafBacklogCaptureOwnerProof(
    ProducedRunCaptureProof Capture,
    string State,
    string ObjectKey,
    long EventPosition,
    DateTimeOffset AdmittedAt,
    ImmutableArray<MafBacklogCapturedOutput> Outputs) : MafBacklogOwnerProof;

internal sealed record MafBacklogCapturedOutput(
    string Path,
    string Sha256,
    long ByteLength);

internal sealed record MafBacklogMergeOwnerProof(
    string IntentId,
    SourceControlAcceptedRunBinding AcceptedRun,
    long SourceStateVersion,
    Guid SourceDecisionId,
    string SourceRequestId,
    string WorkPlanId,
    string WorkflowStepId,
    string ApprovalRequestId,
    string State,
    Guid? ApprovalDecisionId,
    long? ApprovalStateVersion,
    CoordinatorGateDecisionReceipt? ApprovalReceipt,
    string? MergeSha);

internal sealed record MafBacklogPreparedPrerequisite(
    BacklogPrerequisiteExecutionSnapshot Snapshot,
    MafBacklogTaskClaimTuple Claim,
    MafBacklogAcceptedRootEvidence Root,
    ImmutableArray<MafBacklogOwnerProof> OwnerProofs);

internal sealed record MafBacklogPrerequisiteEvidencePreparation
    : BacklogPrerequisiteEvidencePreparation
{
    internal MafBacklogPrerequisiteEvidencePreparation(
        ImmutableArray<MafBacklogPreparedPrerequisite> preparedPrerequisites)
        : base(ToSnapshots(preparedPrerequisites)) =>
        PreparedPrerequisites = preparedPrerequisites;

    internal ImmutableArray<MafBacklogPreparedPrerequisite> PreparedPrerequisites { get; }

    private static ImmutableArray<BacklogPrerequisiteExecutionSnapshot> ToSnapshots(
        ImmutableArray<MafBacklogPreparedPrerequisite> prerequisites)
    {
        if (prerequisites.IsDefault)
            throw new ArgumentException("Prepared prerequisites must be initialized.", nameof(prerequisites));
        var snapshots = ImmutableArray.CreateBuilder<BacklogPrerequisiteExecutionSnapshot>(
            prerequisites.Length);
        foreach (var prerequisite in prerequisites)
            snapshots.Add(prerequisite.Snapshot);
        return snapshots.MoveToImmutable();
    }
}
