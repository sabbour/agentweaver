using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

internal sealed partial class MafExecutionOutputWitnessStore
{
    internal async Task<MafExecutionOutputWitnessRecord>
        AppendOutputWitnessWithCurrentOwnerEvidenceInTransactionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            PostgresMafCheckpointStore checkpointStore,
            MafCheckpointBinding checkpointBinding,
            SourceControlOwnerStore sourceControlStore,
            MafExecutionOwnerEvidenceBinding root,
            string tenantId,
            ImmutableArray<string> outputCaptureIds,
            ImmutableArray<string> mergeIntentIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(checkpointBinding);
        ArgumentNullException.ThrowIfNull(sourceControlStore);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (outputCaptureIds.IsDefault || mergeIntentIds.IsDefault ||
            !HasUniqueIdentifiers(outputCaptureIds) || !HasUniqueIdentifiers(mergeIntentIds))
            throw InvalidOwnerEvidence();

        var currentOwner = await sourceControlStore.ReadPrerequisiteOwnerBindingSnapshotInTransactionAsync(
            connection,
            transaction,
            root.Actor,
            root.Identity,
            tenantId,
            root.AcceptedSelectionHash,
            root.ExecutionFence,
            cancellationToken).ConfigureAwait(false);
        ValidateRootOwnerBinding(currentOwner, root, tenantId);

        var captures = ImmutableArray.CreateBuilder<SourceControlOutputCaptureRecord>(outputCaptureIds.Length);
        foreach (var captureId in outputCaptureIds)
        {
            var result = await sourceControlStore.ReadPrerequisiteOutputCaptureInTransactionAsync(
                connection,
                transaction,
                root.Actor,
                root.Identity,
                tenantId,
                root.AcceptedSelectionHash,
                root.ExecutionFence,
                captureId,
                cancellationToken).ConfigureAwait(false);
            if (result.CurrentOwner != currentOwner)
                throw StaleOwnerBinding();
            captures.Add(result.Capture ?? throw new CoordinationException(
                "source_control_output_capture_unavailable", StatusCodes.Status404NotFound));
        }

        var mergeIntents = ImmutableArray.CreateBuilder<SourceControlMergeIntentSnapshot>(mergeIntentIds.Length);
        foreach (var intentId in mergeIntentIds)
        {
            var result = await sourceControlStore.ReadPrerequisiteMergeIntentInTransactionAsync(
                connection,
                transaction,
                root.Actor,
                root.Identity,
                tenantId,
                root.AcceptedSelectionHash,
                root.ExecutionFence,
                intentId,
                cancellationToken).ConfigureAwait(false);
            if (result.CurrentOwner != currentOwner)
                throw StaleOwnerBinding();
            mergeIntents.Add(result.Intent ?? throw new CoordinationException(
                "source_control_intent_unavailable", StatusCodes.Status404NotFound));
        }

        var ownerEvidence = MafExecutionOwnerEvidenceContract.Serialize(
            root, tenantId, captures.MoveToImmutable(), mergeIntents.MoveToImmutable());
        return await AppendOutputWitnessInTransactionAsync(
            connection,
            transaction,
            checkpointStore,
            checkpointBinding,
            root.Identity,
            root.ExecutionFence,
            root.WorkPlan,
            root.Checkpoint,
            root.CurrentDecision.StateVersion,
            root.AcceptedSelectionHash,
            root.OutputSet,
            ownerEvidence,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool HasUniqueIdentifiers(ImmutableArray<string> identifiers) =>
        identifiers.All(identifier => !string.IsNullOrWhiteSpace(identifier)) &&
        identifiers.Distinct(StringComparer.Ordinal).Count() == identifiers.Length;

    private static void ValidateRootOwnerBinding(
        SourceControlOwnerStore.OwnerBindingSnapshot owner,
        MafExecutionOwnerEvidenceBinding root,
        string tenantId)
    {
        if (owner.AcceptedSelectionHash != root.AcceptedSelectionHash ||
            owner.AcceptedIssuer != root.Actor.Issuer ||
            owner.AcceptedSubject != root.Actor.Subject ||
            owner.TenantId != tenantId ||
            owner.RunFence != root.ExecutionFence ||
            owner.SessionFence != root.ExecutionFence ||
            owner.RootSessionId != root.Identity.SessionId ||
            owner.WriterIssuer != root.Actor.Issuer ||
            owner.WriterSubject != root.Actor.Subject ||
            owner.StateVersion != root.CurrentDecision.StateVersion ||
            owner.DecisionState != "accepted" ||
            owner.ExecutionState == "completed" ||
            owner.SessionLifecycle != "active")
            throw StaleOwnerBinding();
    }

    private static CoordinationException StaleOwnerBinding() =>
        new("maf_execution_output_witness_owner_stale", StatusCodes.Status409Conflict);

    private static CoordinationException InvalidOwnerEvidence() =>
        new("maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
}
