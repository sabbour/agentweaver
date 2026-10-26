using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Adds owner-read evidence to the output witness within the caller's database transaction.
/// The partial store keeps this workflow separate from witness persistence while sharing its validation rules.
/// </summary>
internal sealed partial class MafExecutionOutputWitnessStore
{
    /// <summary>
    /// Reads current source-control capture and merge evidence, validates one stable owner binding, and appends the witness.
    /// </summary>
    /// <param name="connection">The connection that owns the caller's transaction.</param>
    /// <param name="transaction">The transaction used for all owner reads and witness persistence.</param>
    /// <param name="checkpointStore">The checkpoint store used to verify completion state.</param>
    /// <param name="checkpointBinding">The checkpoint identity and fence binding.</param>
    /// <param name="sourceControlStore">The source-control owner that provides capture and merge records.</param>
    /// <param name="root">The accepted root execution, decision, plan, checkpoint, and output set.</param>
    /// <param name="tenantId">The tenant expected on each owner record.</param>
    /// <param name="outputCaptureIds">The capture IDs that must prove all output obligations.</param>
    /// <param name="mergeIntentIds">The optional merge-intent IDs to include as owner evidence.</param>
    /// <param name="cancellationToken">Cancels the database reads and append.</param>
    /// <returns>The persisted output witness record.</returns>
    /// <exception cref="CoordinationException">Thrown when owner evidence is missing, stale, or bound to a different accepted run.</exception>
    /// <remarks>Every record is reread in the same transaction, and each read must return the same current owner binding before any witness is written.</remarks>
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
            cancellationToken,
            root.CurrentDecision).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that an identifier list has no blank entries or duplicate values.
    /// </summary>
    private static bool HasUniqueIdentifiers(ImmutableArray<string> identifiers) =>
        identifiers.All(identifier => !string.IsNullOrWhiteSpace(identifier)) &&
        identifiers.Distinct(StringComparer.Ordinal).Count() == identifiers.Length;

    /// <summary>
    /// Rejects source-control evidence when its accepted run or active writer binding differs from the root execution.
    /// </summary>
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

    /// <summary>
    /// Creates the conflict response for owner evidence read from a changed binding.
    /// </summary>
    private static CoordinationException StaleOwnerBinding() =>
        new("maf_execution_output_witness_owner_stale", StatusCodes.Status409Conflict);

    /// <summary>
    /// Creates the conflict response for malformed owner evidence input.
    /// </summary>
    private static CoordinationException InvalidOwnerEvidence() =>
        new("maf_execution_output_witness_evidence_invalid", StatusCodes.Status409Conflict);
}
