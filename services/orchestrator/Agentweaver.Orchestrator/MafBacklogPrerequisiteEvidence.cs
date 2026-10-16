using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Agents.AI.Workflows;
using Npgsql;

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
    string TenantId,
    SessionIdentity Identity,
    AcceptedRoot Root,
    EffectiveRunSelection Selection,
    string AcceptedSelectionHash,
    long ExecutionFence,
    WorkPlanRunSelectionContext SelectionContext,
    CoordinatorDecisionCurrentState CurrentDecision,
    MafExecutionCheckpointSnapshot Checkpoint,
    ImmutableArray<(string SessionId, string WorkPlanItemId)> ChildBindings,
    WorkPlanSnapshot WorkPlan,
    MafExecutionOutputWitnessRecord? OutputWitness,
    MafBacklogOutputSetSnapshot OutputSet,
    ImmutableArray<string> MissingFixedAssociationIds);

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
    string? MergeSha) : MafBacklogOwnerProof;

internal sealed record MafBacklogNoOutputOwnerProof(
    string WorkPlanId,
    string OutputSetSha256,
    long CheckpointRevision,
    long DecisionStateVersion) : MafBacklogOwnerProof;

internal sealed record MafBacklogPreparedPrerequisite(
    BacklogPrerequisiteExecutionSnapshot Snapshot,
    MafBacklogTaskClaimTuple Claim,
    MafBacklogAcceptedRootEvidence Root,
    ImmutableArray<MafBacklogOwnerProof> OwnerProofs);

internal sealed record MafBacklogPrerequisiteEvidencePreparation
    : BacklogPrerequisiteEvidencePreparation
{
    internal MafBacklogPrerequisiteEvidencePreparation(
        ImmutableArray<BacklogPrerequisiteExecutionSnapshot> snapshots,
        ImmutableArray<MafBacklogPreparedPrerequisite> preparedPrerequisites)
        : base(snapshots)
    {
        if (snapshots.IsDefault || preparedPrerequisites.IsDefault)
            throw new ArgumentException("Prepared prerequisites must be initialized.");
        PreparedPrerequisites = preparedPrerequisites;
    }

    internal ImmutableArray<MafBacklogPreparedPrerequisite> PreparedPrerequisites { get; }
}

internal sealed class MafBacklogPrerequisiteEvidenceReader : IBacklogPrerequisiteEvidenceReader
{
    private static readonly JsonSerializerOptions OwnerEvidenceJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

    private readonly NpgsqlDataSource _dataSource;
    private readonly BacklogOwnerStore _backlog;
    private readonly CoordinationOwnerStore _coordination;
    private readonly CoordinatorDecisionOwnerStore _decisions;
    private readonly CoordinatorRunSelectionContextStore _selectionContexts;
    private readonly SourceControlOwnerStore _sourceControl;
    private readonly PostgresMafCheckpointStore _checkpoints;
    private readonly MafExecutionOutputWitnessStore _outputWitnesses;

    internal MafBacklogPrerequisiteEvidenceReader(
        NpgsqlDataSource dataSource,
        BacklogOwnerStore backlog,
        CoordinationOwnerStore coordination,
        CoordinatorDecisionOwnerStore decisions,
        CoordinatorRunSelectionContextStore selectionContexts,
        SourceControlOwnerStore sourceControl,
        PostgresMafCheckpointStore checkpoints,
        MafExecutionOutputWitnessStore outputWitnesses)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _backlog = backlog ?? throw new ArgumentNullException(nameof(backlog));
        _coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _selectionContexts = selectionContexts ?? throw new ArgumentNullException(nameof(selectionContexts));
        _sourceControl = sourceControl ?? throw new ArgumentNullException(nameof(sourceControl));
        _checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        _outputWitnesses = outputWitnesses ?? throw new ArgumentNullException(nameof(outputWitnesses));
    }

    public async Task<BacklogPrerequisiteEvidencePreparation> PrepareForClaimAsync(
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogDependencyGraph graph,
        BacklogTaskReference task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(task);
        var prerequisites = GetPrerequisites(graph, task);
        if (prerequisites.IsEmpty)
            return new MafBacklogPrerequisiteEvidencePreparation([], []);

        var snapshots = ImmutableArray.CreateBuilder<BacklogPrerequisiteExecutionSnapshot>(
            prerequisites.Length);
        var claims = ImmutableDictionary.CreateBuilder<string, MafBacklogTaskClaimTuple>(
            StringComparer.Ordinal);
        var bindings = ImmutableDictionary.CreateBuilder<string, AcceptedRootBinding>(
            StringComparer.Ordinal);
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            var claimRows = await _backlog.ReadPrerequisiteClaimTuplesInTransactionAsync(
                connection, transaction, task.ProjectId, prerequisites, cancellationToken).ConfigureAwait(false);
            foreach (var prerequisite in prerequisites)
            {
                var graphTask = FindTask(graph, prerequisite);
                var claim = claimRows.SingleOrDefault(
                    row => row.Task.TaskId == prerequisite.TaskId);
                if (claim is null)
                {
                    snapshots.Add(CreateSnapshot(
                        prerequisite,
                        graphTask.Revision,
                        0,
                        BacklogPrerequisiteExecutionState.Missing,
                        BacklogOutputProofState.Missing));
                    continue;
                }

                claims.Add(prerequisite.TaskId, claim);
                snapshots.Add(CreateUnverifiedSnapshot(claim, 0));
                try
                {
                    var binding = await ReadRootBindingAsync(
                        connection, transaction, claim, cancellationToken).ConfigureAwait(false);
                    if (claim.ClaimDecisionStateVersion > binding.DecisionStateVersion)
                    {
                        snapshots[^1] = CreateSnapshot(
                            prerequisite, claim.TaskRevision, 0,
                            BacklogPrerequisiteExecutionState.Stale,
                            BacklogOutputProofState.Stale);
                        continue;
                    }
                    bindings.Add(prerequisite.TaskId, binding);
                }
                catch (CoordinationException exception) when (IsUnavailableBinding(exception))
                {
                    snapshots[^1] = CreateUnverifiedSnapshot(claim, 0);
                }
                catch (CoordinationException exception) when (IsStaleBinding(exception))
                {
                    snapshots[^1] = CreateSnapshot(
                        prerequisite, claim.TaskRevision, 0,
                        BacklogPrerequisiteExecutionState.Stale,
                        BacklogOutputProofState.Stale);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var contexts = ImmutableDictionary.CreateBuilder<string, WorkPlanRunSelectionContext>(
            StringComparer.Ordinal);
        var failedContextTasks = ImmutableDictionary.CreateBuilder<string, BacklogPrerequisiteExecutionState>(
            StringComparer.Ordinal);
        foreach (var (taskId, binding) in bindings)
        {
            try
            {
                contexts[taskId] = await _selectionContexts.ReadAsync(
                        binding.Selection, binding.Root.ExecutionFence, cancellationToken)
                    .ConfigureAwait(false)
                    ?? CoordinatorWorkflowCatalog.CreateRunSelectionContext(binding.Selection.Snapshot);
            }
            catch (CoordinationException exception) when (IsStaleBinding(exception))
            {
                failedContextTasks[taskId] = BacklogPrerequisiteExecutionState.Stale;
            }
            catch (CoordinationException exception) when (IsUnavailableBinding(exception))
            {
                failedContextTasks[taskId] = BacklogPrerequisiteExecutionState.Indeterminate;
            }
        }

        var prepared = ImmutableArray.CreateBuilder<MafBacklogPreparedPrerequisite>();
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken)
                         .ConfigureAwait(false))
        {
            for (var index = 0; index < prerequisites.Length; index++)
            {
                var prerequisite = prerequisites[index];
                if (!claims.TryGetValue(prerequisite.TaskId, out var claim) ||
                    !bindings.TryGetValue(prerequisite.TaskId, out var initialBinding))
                    continue;
                if (failedContextTasks.TryGetValue(prerequisite.TaskId, out var failedContextState))
                {
                    snapshots[index] = CreateSnapshot(
                        prerequisite, claim.TaskRevision, 0, failedContextState,
                        failedContextState == BacklogPrerequisiteExecutionState.Stale
                            ? BacklogOutputProofState.Stale
                            : BacklogOutputProofState.Indeterminate);
                    continue;
                }

                var selectionContext = contexts[prerequisite.TaskId];
                try
                {
                    var currentBinding = await ReadRootBindingAsync(
                        connection, transaction, claim, cancellationToken).ConfigureAwait(false);
                    if (!SameRootBinding(initialBinding, currentBinding))
                    {
                        snapshots[index] = CreateSnapshot(
                            prerequisite, claim.TaskRevision, 0,
                            BacklogPrerequisiteExecutionState.Stale,
                            BacklogOutputProofState.Stale);
                        continue;
                    }

                    var evidence = await ReadRootEvidenceInTransactionAsync(
                        connection,
                        transaction,
                        currentBinding,
                        selectionContext,
                        cancellationToken).ConfigureAwait(false);
                    if (evidence is null)
                    {
                        snapshots[index] = CreateUnverifiedSnapshot(claim, 0);
                        continue;
                    }

                    var ownerProofs = await ReadCurrentOwnerProofsInTransactionAsync(
                        connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
                    prepared.Add(new MafBacklogPreparedPrerequisite(
                        CreateUnverifiedSnapshot(claim, evidence.Checkpoint.State.Revision),
                        claim,
                        evidence,
                        ownerProofs));
                    snapshots[index] = CreateUnverifiedSnapshot(
                        claim, evidence.Checkpoint.State.Revision);
                }
                catch (CoordinationException exception) when (IsUnavailableBinding(exception))
                {
                    snapshots[index] = CreateUnverifiedSnapshot(claim, 0);
                }
                catch (CoordinationException exception) when (IsStaleBinding(exception))
                {
                    snapshots[index] = CreateSnapshot(
                        prerequisite, claim.TaskRevision, 0,
                        BacklogPrerequisiteExecutionState.Stale,
                        BacklogOutputProofState.Stale);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new MafBacklogPrerequisiteEvidencePreparation(
            snapshots.MoveToImmutable(), prepared.ToImmutable());
    }

    public async Task<ImmutableArray<BacklogPrerequisiteExecutionSnapshot>> ReadCurrentForClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        BacklogDependencyGraph graph,
        BacklogTaskReference task,
        BacklogPrerequisiteEvidencePreparation prepared,
        long decisionStateVersion,
        long executionFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied connection.", nameof(transaction));
        if (decisionStateVersion < 1 || executionFence < 1 ||
            task.ProjectId != selection.Selection.ProjectId ||
            graph.ProjectId != task.ProjectId)
            throw new CoordinationException(
                "backlog_prerequisite_binding_invalid", StatusCodes.Status409Conflict);
        if (prepared is not MafBacklogPrerequisiteEvidencePreparation mafPrepared)
            throw new InvalidOperationException(
                "Backlog prerequisite evidence must be prepared by the MAF evidence reader.");

        var prerequisites = GetPrerequisites(graph, task);
        var claimRows = await _backlog.ReadPrerequisiteClaimTuplesInTransactionAsync(
            connection, transaction, task.ProjectId, prerequisites, cancellationToken).ConfigureAwait(false);
        var preparedByTask = mafPrepared.PreparedPrerequisites.ToDictionary(
            item => item.Claim.Task.TaskId, StringComparer.Ordinal);
        var preparedSnapshotsByTask = mafPrepared.Prerequisites.ToDictionary(
            snapshot => snapshot.Prerequisite.TaskId, StringComparer.Ordinal);
        var snapshots = ImmutableArray.CreateBuilder<BacklogPrerequisiteExecutionSnapshot>(
            prerequisites.Length);
        foreach (var prerequisite in prerequisites)
        {
            var claim = claimRows.SingleOrDefault(row => row.Task.TaskId == prerequisite.TaskId);
            if (claim is null)
            {
                var graphTask = FindTask(graph, prerequisite);
                snapshots.Add(CreateSnapshot(
                    prerequisite, graphTask.Revision, 0,
                    BacklogPrerequisiteExecutionState.Missing,
                    BacklogOutputProofState.Missing));
                continue;
            }
            if (!preparedByTask.TryGetValue(prerequisite.TaskId, out var preparedItem))
            {
                if (preparedSnapshotsByTask.TryGetValue(prerequisite.TaskId, out var preparedSnapshot) &&
                    preparedSnapshot.TaskRevision == claim.TaskRevision)
                    snapshots.Add(preparedSnapshot);
                else
                    snapshots.Add(CreateSnapshot(
                        prerequisite, claim.TaskRevision, 0,
                        BacklogPrerequisiteExecutionState.Stale,
                        BacklogOutputProofState.Stale));
                continue;
            }
            if (!SameClaim(preparedItem.Claim, claim))
            {
                snapshots.Add(CreateSnapshot(
                    prerequisite, claim.TaskRevision, 0,
                    BacklogPrerequisiteExecutionState.Stale,
                    BacklogOutputProofState.Stale));
                continue;
            }

            try
            {
                var currentBinding = await ReadRootBindingAsync(
                    connection, transaction, claim, cancellationToken).ConfigureAwait(false);
                if (!SameRootBinding(preparedItem.Root, currentBinding))
                {
                    snapshots.Add(CreateSnapshot(
                        prerequisite, claim.TaskRevision, 0,
                        BacklogPrerequisiteExecutionState.Stale,
                        BacklogOutputProofState.Stale));
                    continue;
                }

                var evidence = await ReadRootEvidenceInTransactionAsync(
                    connection,
                    transaction,
                    currentBinding,
                    preparedItem.Root.SelectionContext,
                    cancellationToken).ConfigureAwait(false);
                if (evidence is null)
                {
                    snapshots.Add(CreateUnverifiedSnapshot(claim, 0));
                    continue;
                }
                var ownerProofs = await ReadCurrentOwnerProofsInTransactionAsync(
                    connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
                if (!SameCheckpoint(preparedItem.Root.Checkpoint, evidence.Checkpoint) ||
                !preparedItem.Root.ChildBindings.SequenceEqual(evidence.ChildBindings) ||
                !SameOutputEvidence(preparedItem.Root, evidence) ||
                !SameOwnerProofs(preparedItem.OwnerProofs, ownerProofs))
                {
                    snapshots.Add(CreateSnapshot(
                        prerequisite, claim.TaskRevision, evidence.Checkpoint.State.Revision,
                        BacklogPrerequisiteExecutionState.Stale,
                        BacklogOutputProofState.Stale));
                    continue;
                }

                snapshots.Add(CreateVerifiedSnapshot(
                    claim, evidence.Checkpoint.State.Revision, evidence, ownerProofs));
            }
            catch (CoordinationException exception) when (IsUnavailableBinding(exception))
            {
                snapshots.Add(CreateUnverifiedSnapshot(claim, 0));
            }
            catch (CoordinationException exception) when (IsStaleBinding(exception))
            {
                snapshots.Add(CreateSnapshot(
                    prerequisite, claim.TaskRevision, 0,
                    BacklogPrerequisiteExecutionState.Stale,
                    BacklogOutputProofState.Stale));
            }
        }
        return snapshots.ToImmutable();
    }

    private async Task<AcceptedRootBinding> ReadRootBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafBacklogTaskClaimTuple claim,
        CancellationToken cancellationToken)
    {
        var binding = await _coordination.ReadPrerequisiteRootBindingInTransactionAsync(
            connection,
            transaction,
            claim.Task.ProjectId,
            claim.ClaimRunId,
            claim.ClaimRootSessionId,
            claim.ClaimSelectionHash,
            claim.ClaimExecutionFence,
            cancellationToken).ConfigureAwait(false);
        return new AcceptedRootBinding(
            claim,
            binding.Root,
            binding.Actor,
            binding.TenantId,
            binding.Selection,
            binding.AcceptedSelectionHash,
            binding.DecisionStateVersion);
    }

    private async Task<MafBacklogAcceptedRootEvidence?> ReadRootEvidenceInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AcceptedRootBinding binding,
        WorkPlanRunSelectionContext selectionContext,
        CancellationToken cancellationToken)
    {
        var identity = new SessionIdentity(
            binding.Root.ProjectId, binding.Root.RunId, binding.Root.RootSessionId);
        var decision = await _decisions.ReadBacklogCurrentDecisionInTransactionAsync(
            connection,
            transaction,
            binding.Actor,
            binding.TenantId,
            identity,
            binding.Selection,
            binding.SelectionHash,
            binding.Root.ExecutionFence,
            binding.DecisionStateVersion,
            selectionContext,
            cancellationToken).ConfigureAwait(false);
        var plan = decision.State.ConfirmedWorkPlan;
        if (plan is null ||
            !decision.State.WorkflowConfirmed ||
            decision.State.CandidateWorkPlan is not null ||
            decision.State.Fence != binding.Root.ExecutionFence ||
            decision.StateVersion != binding.DecisionStateVersion ||
            decision.SelectionHash != binding.SelectionHash)
            return null;

        var latest = await ReadLatestExecutionCheckpointInTransactionAsync(
            connection, transaction, binding, cancellationToken).ConfigureAwait(false);
        if (latest is null)
            return null;
        if (latest.Info.SessionId != binding.Root.RootSessionId ||
            latest.State.WorkPlanId != plan.Plan.Id ||
            latest.State.DecisionStateVersion != decision.StateVersion)
            throw new CoordinationException(
                "backlog_prerequisite_binding_stale", StatusCodes.Status409Conflict);

        var childBindings = await _coordination.ReadBacklogWorkPlanChildBindingsInTransactionAsync(
            connection,
            transaction,
            binding.Root.ProjectId,
            binding.Root.RunId,
            binding.Root.RootSessionId,
            cancellationToken).ConfigureAwait(false);
        var output = MafExecutionOutputWitness.CreateCompletePlanOutputSet(plan, latest, identity);
        MafExecutionOutputWitnessRecord? outputWitness = null;
        if (output.MissingFixedAssociationIds.IsEmpty)
        {
            try
            {
                outputWitness = await _outputWitnesses.ReadOutputWitnessInTransactionAsync(
                    connection,
                    transaction,
                    _checkpoints,
                    CreateCheckpointBinding(binding),
                    identity,
                    binding.Root.ExecutionFence,
                    plan,
                    latest,
                    decision.StateVersion,
                    binding.SelectionHash,
                    output.OutputSet,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CoordinationException exception) when (
                exception.Code == "maf_execution_output_witness_plan_incomplete")
            {
            }
        }
        return new MafBacklogAcceptedRootEvidence(
            binding.Actor,
            binding.TenantId,
            identity,
            binding.Root,
            binding.Selection,
            binding.SelectionHash,
            binding.Root.ExecutionFence,
            selectionContext,
            decision,
            latest,
            childBindings,
            plan,
            outputWitness,
            output.OutputSet,
            output.MissingFixedAssociationIds);
    }

    private async Task<ImmutableArray<MafBacklogOwnerProof>> ReadCurrentOwnerProofsInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafBacklogAcceptedRootEvidence root,
        CancellationToken cancellationToken)
    {
        if (root.OutputWitness is not { } witness || root.OutputSet is null ||
            !TryParseOwnerEvidence(
                witness.OwnerEvidenceJson,
                out var captureRow,
                out var mergeRow,
                out var noOutputRow))
            return [];

        if (noOutputRow is not null)
        {
            if (!root.OutputSet.Obligations.IsEmpty)
                return [];
            var inventory = await ReadCurrentOutputCaptureInventoryInTransactionAsync(
                connection, transaction, root, cancellationToken).ConfigureAwait(false);
            if (inventory is null ||
                !TryRebuildCurrentOwnerEvidence(
                    CreateOwnerEvidenceBinding(root),
                    root.TenantId,
                    null,
                    inventory.Captures,
                    [],
                    out var expected) ||
                !JsonElement.DeepEquals(expected, witness.OwnerEvidenceJson))
                return [];
            return [new MafBacklogNoOutputOwnerProof(
                noOutputRow.WorkPlanId,
                noOutputRow.OutputSetSha256,
                noOutputRow.CheckpointRevision,
                noOutputRow.DecisionStateVersion)];
        }

        if (captureRow is null ||
            captureRow.Capture is not { } captureProof ||
            captureRow.Outputs.IsDefault ||
            !ValidCaptureProof(captureProof))
            return [];

        var currentInventory = await ReadCurrentOutputCaptureInventoryInTransactionAsync(
            connection, transaction, root, cancellationToken).ConfigureAwait(false);
        if (currentInventory is null ||
            !IsCurrentOwnerBinding(currentInventory.CurrentOwner, root) ||
            currentInventory.Captures.Length != 1 ||
            currentInventory.Captures[0].Proof.CaptureId != captureProof.CaptureId)
            return [];

        var capture = currentInventory.Captures[0];
        var captures = currentInventory.Captures;
        var mergeIntents = ImmutableArray<SourceControlMergeIntentSnapshot>.Empty;
        SourceControlMergeIntentSnapshot? mergeIntent = null;
        if (mergeRow is not null)
        {
            if (string.IsNullOrWhiteSpace(mergeRow.IntentId))
                return [];
            var mergeResult = await _sourceControl.ReadPrerequisiteMergeIntentInTransactionAsync(
                connection,
                transaction,
                root.Actor,
                root.Identity,
                root.TenantId,
                root.AcceptedSelectionHash,
                root.ExecutionFence,
                mergeRow.IntentId,
                cancellationToken).ConfigureAwait(false);
            if (mergeResult.Intent is not { } currentMergeIntent ||
                currentInventory.CurrentOwner != mergeResult.CurrentOwner)
                return [];
            mergeIntent = currentMergeIntent;
            mergeIntents = [currentMergeIntent];
        }

        if (!TryRebuildCurrentOwnerEvidence(
                CreateOwnerEvidenceBinding(root),
                root.TenantId,
                captureProof.CaptureId,
                captures,
                mergeIntents,
                out var expectedEvidence) ||
            !JsonElement.DeepEquals(expectedEvidence, witness.OwnerEvidenceJson))
            return [];

        var proofs = ImmutableArray.CreateBuilder<MafBacklogOwnerProof>(mergeIntent is null ? 1 : 2);
        proofs.Add(new MafBacklogCaptureOwnerProof(
            capture.Proof,
            capture.State,
            capture.ObjectKey!,
            capture.EventPosition!.Value,
            capture.AdmittedAt!.Value,
            [.. captureRow.Outputs.Select(output =>
                new MafBacklogCapturedOutput(output.Path, output.Sha256, output.ByteLength))]));
        if (mergeIntent is not null)
            proofs.Add(new MafBacklogMergeOwnerProof(
                mergeIntent.IntentId,
                mergeIntent.AcceptedRun,
                mergeIntent.SourceStateVersion,
                mergeIntent.SourceDecisionId,
                mergeIntent.SourceRequestId,
                mergeIntent.WorkPlanId,
                mergeIntent.WorkflowStepId,
                mergeIntent.ApprovalRequestId,
                mergeIntent.State,
                mergeIntent.ApprovalDecisionId,
                mergeIntent.ApprovalStateVersion,
                mergeIntent.ApprovalReceipt,
                mergeIntent.MergeSha));
        return proofs.ToImmutable();
    }

    private async Task<CurrentOutputCaptureInventory?> ReadCurrentOutputCaptureInventoryInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MafBacklogAcceptedRootEvidence root,
        CancellationToken cancellationToken)
    {
        var proofs = await _sourceControl.ReadPrerequisiteOutputCaptureProofsInTransactionAsync(
            connection,
            transaction,
            root.Actor,
            root.Identity,
            root.TenantId,
            root.AcceptedSelectionHash,
            root.ExecutionFence,
            cancellationToken).ConfigureAwait(false);
        if (proofs.IsDefault)
            return null;

        var captures = ImmutableArray.CreateBuilder<SourceControlOutputCaptureRecord>(proofs.Length);
        SourceControlOwnerStore.OwnerBindingSnapshot? currentOwner = null;
        foreach (var proof in proofs)
        {
            if (!ValidCaptureProof(proof))
                return null;
            var result = await _sourceControl.ReadPrerequisiteOutputCaptureInTransactionAsync(
                connection,
                transaction,
                root.Actor,
                root.Identity,
                root.TenantId,
                root.AcceptedSelectionHash,
                root.ExecutionFence,
                proof.CaptureId,
                cancellationToken).ConfigureAwait(false);
            if (result.Capture is not { } capture ||
                capture.Proof != proof ||
                !IsCurrentOwnerBinding(result.CurrentOwner, root) ||
                currentOwner is not null && currentOwner != result.CurrentOwner)
                return null;
            currentOwner = result.CurrentOwner;
            captures.Add(capture);
        }

        if (currentOwner is null)
        {
            currentOwner = await _sourceControl.ReadPrerequisiteOwnerBindingSnapshotInTransactionAsync(
                connection,
                transaction,
                root.Actor,
                root.Identity,
                root.TenantId,
                root.AcceptedSelectionHash,
                root.ExecutionFence,
                cancellationToken).ConfigureAwait(false);
            if (!IsCurrentOwnerBinding(currentOwner, root))
                return null;
        }
        return new CurrentOutputCaptureInventory(currentOwner, captures.ToImmutable());
    }

    internal static bool TryRebuildCurrentOwnerEvidence(
        MafExecutionOwnerEvidenceBinding root,
        string tenantId,
        string? witnessCaptureId,
        ImmutableArray<SourceControlOutputCaptureRecord> captures,
        ImmutableArray<SourceControlMergeIntentSnapshot> mergeIntents,
        out JsonElement ownerEvidence)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ownerEvidence = default;
        if (captures.IsDefault || mergeIntents.IsDefault)
            return false;

        if (root.OutputSet.Obligations.IsEmpty)
        {
            if (witnessCaptureId is not null || !mergeIntents.IsEmpty)
                return false;
            ownerEvidence = MafExecutionOwnerEvidenceContract.Serialize(
                root, tenantId, captures, mergeIntents);
            return true;
        }

        if (string.IsNullOrWhiteSpace(witnessCaptureId) ||
            captures.Length != 1 ||
            !ValidCaptureProof(captures[0].Proof) ||
            captures[0].Proof.CaptureId != witnessCaptureId)
            return false;
        ownerEvidence = MafExecutionOwnerEvidenceContract.Serialize(
            root, tenantId, captures, mergeIntents);
        return true;
    }

    private static MafExecutionOwnerEvidenceBinding CreateOwnerEvidenceBinding(
        MafBacklogAcceptedRootEvidence root) =>
        new(
            root.Actor,
            root.Identity,
            root.AcceptedSelectionHash,
            root.ExecutionFence,
            root.CurrentDecision,
            root.Checkpoint,
            root.WorkPlan,
            root.OutputSet);

    internal static bool TryParseOwnerEvidence(
        JsonElement evidence,
        out MafExecutionOwnerEvidenceCaptureRow? capture,
        out MafExecutionOwnerEvidenceMergeRow? merge,
        out MafExecutionOwnerEvidenceNoOutputRow? noOutput)
    {
        capture = null;
        merge = null;
        noOutput = null;
        if (evidence.ValueKind != JsonValueKind.Array)
            return false;

        var count = 0;
        foreach (var row in evidence.EnumerateArray())
        {
            count++;
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("kind", out var kindValue) ||
                kindValue.ValueKind != JsonValueKind.String)
                return false;
            switch (kindValue.GetString())
            {
                case "capture" when capture is null:
                    if (!TryDeserializeOwnerEvidence(
                            row, out MafExecutionOwnerEvidenceCaptureRow? parsedCapture) ||
                        parsedCapture is null ||
                        parsedCapture.ContractVersion != MafExecutionOwnerEvidenceContract.CurrentVersion ||
                        parsedCapture.Kind != "capture" ||
                        parsedCapture.Capture is null ||
                        parsedCapture.Outputs.IsDefault)
                        return false;
                    capture = parsedCapture;
                    break;
                case "merge" when merge is null:
                    if (!TryDeserializeOwnerEvidence(
                            row, out MafExecutionOwnerEvidenceMergeRow? parsedMerge) ||
                        parsedMerge is null ||
                        parsedMerge.ContractVersion != MafExecutionOwnerEvidenceContract.CurrentVersion ||
                        parsedMerge.Kind != "merge" ||
                        string.IsNullOrWhiteSpace(parsedMerge.IntentId))
                        return false;
                    merge = parsedMerge;
                    break;
                case "no-output" when noOutput is null:
                    if (!TryDeserializeOwnerEvidence(
                            row, out MafExecutionOwnerEvidenceNoOutputRow? parsedNoOutput) ||
                        parsedNoOutput is null ||
                        parsedNoOutput.ContractVersion != MafExecutionOwnerEvidenceContract.CurrentVersion ||
                        parsedNoOutput.Kind != "no-output")
                        return false;
                    noOutput = parsedNoOutput;
                    break;
                default:
                    return false;
            }
        }

        return noOutput is not null
            ? count == 1 && capture is null && merge is null
            : capture is not null && count == (merge is null ? 1 : 2);
    }

    private static bool TryDeserializeOwnerEvidence<T>(JsonElement value, out T? result)
        where T : class
    {
        try
        {
            result = value.Deserialize<T>(OwnerEvidenceJsonOptions);
            return result is not null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            result = null;
            return false;
        }
    }

    private static bool ValidCaptureProof(ProducedRunCaptureProof proof)
    {
        try
        {
            ProducedRunCaptureContractValidation.Validate(proof);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCurrentOwnerBinding(
        SourceControlOwnerStore.OwnerBindingSnapshot owner,
        MafBacklogAcceptedRootEvidence root) =>
        owner.AcceptedSelectionHash == root.AcceptedSelectionHash &&
        owner.AcceptedIssuer == root.Actor.Issuer &&
        owner.AcceptedSubject == root.Actor.Subject &&
        owner.TenantId == root.TenantId &&
        owner.RunFence == root.ExecutionFence &&
        owner.SessionFence == root.ExecutionFence &&
        owner.RootSessionId == root.Identity.SessionId &&
        owner.WriterIssuer == root.Actor.Issuer &&
        owner.WriterSubject == root.Actor.Subject &&
        owner.StateVersion == root.CurrentDecision.StateVersion &&
        owner.DecisionState == "accepted";

    private static BacklogPrerequisiteExecutionSnapshot CreateVerifiedSnapshot(
        MafBacklogTaskClaimTuple claim,
        long executionRevision,
        MafBacklogAcceptedRootEvidence root,
        ImmutableArray<MafBacklogOwnerProof> ownerProofs)
    {
        if (claim.IsArchived || claim.IsAutomationInvocationPending ||
            claim.TaskState != BacklogTaskState.Claimed)
            return CreateUnverifiedSnapshot(claim, executionRevision);

        var mergeStepCount = root.WorkPlan.Workflow.Definition.Steps.Count(step =>
            step.Mode == WorkflowStepMode.Platform &&
            step.PlatformGate == WorkflowPlatformGate.Merge);

        if (root.OutputSet is { Obligations.IsEmpty: true } &&
            root.OutputWitness is not null &&
            mergeStepCount == 0 &&
            ownerProofs is [MafBacklogNoOutputOwnerProof])
            return CreateSnapshot(
                claim.Task,
                claim.TaskRevision,
                executionRevision,
                BacklogPrerequisiteExecutionState.Completed,
                BacklogOutputProofState.NotRequired);

        if (root.OutputSet is not { Obligations.IsEmpty: false } ||
            root.OutputWitness is not { } witness)
            return CreateUnverifiedSnapshot(claim, executionRevision);

        var executionState = mergeStepCount switch
        {
            0 when ownerProofs is [MafBacklogCaptureOwnerProof] =>
                BacklogPrerequisiteExecutionState.Completed,
            1 when ownerProofs is [MafBacklogCaptureOwnerProof, MafBacklogMergeOwnerProof] =>
                BacklogPrerequisiteExecutionState.Merged,
            _ => (BacklogPrerequisiteExecutionState?)null
        };
        if (executionState is null)
            return CreateUnverifiedSnapshot(claim, executionRevision);

        return CreateSnapshot(
            claim.Task,
            claim.TaskRevision,
            executionRevision,
            executionState.Value,
            BacklogOutputProofState.Verified,
            witness.ProofSha256);
    }

    private async Task<MafExecutionCheckpointSnapshot?> ReadLatestExecutionCheckpointInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AcceptedRootBinding binding,
        CancellationToken cancellationToken)
    {
        var checkpointBinding = CreateCheckpointBinding(binding);
        var latest = await _checkpoints.ReadLatestCheckpointForCompletedRootEvidenceInTransactionAsync(
            connection, transaction, checkpointBinding, cancellationToken).ConfigureAwait(false);
        if (latest is null)
            return null;
        try
        {
            return new MafExecutionCheckpointSnapshot(
                latest.Value.Info,
                MafExecutionCheckpointContract.Deserialize(latest.Value.Value));
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new CoordinationException(
                "maf_execution_checkpoint_corrupt", StatusCodes.Status503ServiceUnavailable, exception);
        }
    }

    private static MafCheckpointBinding CreateCheckpointBinding(AcceptedRootBinding binding)
    {
        var sdkVersion = typeof(WorkflowBuilder).Assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(sdkVersion))
            throw new CoordinationException(
                "maf_execution_checkpoint_binding_unavailable", StatusCodes.Status503ServiceUnavailable);
        return new MafCheckpointBinding(
            new SessionIdentity(binding.Root.ProjectId, binding.Root.RunId, binding.Root.RootSessionId),
            binding.Actor,
            binding.Root.ExecutionFence,
            sdkVersion,
            ReadPinnedModelReference(binding.Selection.Snapshot),
            CacheReference: null,
            StoreName: MafExecutionCheckpointContract.StoreName);
    }

    private static string ReadPinnedModelReference(JsonElement snapshot)
    {
        if (snapshot.ValueKind == JsonValueKind.Object &&
            snapshot.TryGetProperty("modelSelection", out var model) &&
            model.ValueKind == JsonValueKind.Object &&
            model.TryGetProperty("reference", out var reference) &&
            reference.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(reference.GetString()) &&
            reference.GetString()!.Length <= 256 &&
            !reference.GetString()!.Any(char.IsControl))
            return reference.GetString()!;
        throw new CoordinationException(
            "maf_execution_checkpoint_binding_unavailable", StatusCodes.Status409Conflict);
    }

    private static ImmutableArray<BacklogTaskReference> GetPrerequisites(
        BacklogDependencyGraph graph,
        BacklogTaskReference task)
    {
        if (graph.ProjectId != task.ProjectId)
            throw new CoordinationException(
                "backlog_prerequisite_binding_invalid", StatusCodes.Status409Conflict);
        return
        [
            .. graph.Dependencies
                .Where(edge => edge.Task == task)
                .Select(edge => edge.Prerequisite)
                .OrderBy(reference => reference.TaskId, StringComparer.Ordinal)
        ];
    }

    private static BacklogTaskSnapshot FindTask(
        BacklogDependencyGraph graph,
        BacklogTaskReference task) =>
        graph.Tasks.SingleOrDefault(item => item.Reference == task)
        ?? throw new CoordinationException(
            "backlog_prerequisite_binding_stale", StatusCodes.Status409Conflict);

    private static BacklogPrerequisiteExecutionSnapshot CreateUnverifiedSnapshot(
        MafBacklogTaskClaimTuple claim,
        long executionRevision)
    {
        var state = claim.IsArchived
            ? BacklogPrerequisiteExecutionState.Archived
            : claim.IsAutomationInvocationPending
                ? BacklogPrerequisiteExecutionState.Indeterminate
                : claim.Phase == "confirmed" && claim.TaskState == BacklogTaskState.Claimed
                    ? BacklogPrerequisiteExecutionState.Indeterminate
                    : BacklogPrerequisiteExecutionState.Pending;
        var outputState = claim.IsArchived
            ? BacklogOutputProofState.Stale
            : state == BacklogPrerequisiteExecutionState.Pending
                ? BacklogOutputProofState.Missing
                : BacklogOutputProofState.Indeterminate;
        return CreateSnapshot(
            claim.Task, claim.TaskRevision, executionRevision, state, outputState);
    }

    private static BacklogPrerequisiteExecutionSnapshot CreateSnapshot(
        BacklogTaskReference task,
        long taskRevision,
        long executionRevision,
        BacklogPrerequisiteExecutionState state,
        BacklogOutputProofState outputState,
        string? outputProofReference = null) =>
        new(task, taskRevision, executionRevision, state, outputState, outputProofReference);

    private static bool SameClaim(MafBacklogTaskClaimTuple left, MafBacklogTaskClaimTuple right) =>
        left.Task == right.Task &&
        left.TaskRevision == right.TaskRevision &&
        left.ClaimTaskRevision == right.ClaimTaskRevision &&
        left.TaskState == right.TaskState &&
        left.IsArchived == right.IsArchived &&
        left.IsAutomationInvocationPending == right.IsAutomationInvocationPending &&
        left.ClaimId == right.ClaimId &&
        left.Phase == right.Phase &&
        left.ClaimIdempotencyKey == right.ClaimIdempotencyKey &&
        left.ClaimRequestHash == right.ClaimRequestHash &&
        left.ClaimRunId == right.ClaimRunId &&
        left.ClaimRootSessionId == right.ClaimRootSessionId &&
        left.ClaimSelectionHash == right.ClaimSelectionHash &&
        left.ClaimExecutionFence == right.ClaimExecutionFence &&
        left.ClaimDecisionStateVersion == right.ClaimDecisionStateVersion;

    private static bool SameRootBinding(AcceptedRootBinding left, AcceptedRootBinding right) =>
        SameClaim(left.Claim, right.Claim) &&
        left.Root == right.Root &&
        left.Actor == right.Actor &&
        left.TenantId == right.TenantId &&
        left.Selection.ProjectId == right.Selection.ProjectId &&
        left.Selection.RunId == right.Selection.RunId &&
        left.Selection.ProjectRevision == right.Selection.ProjectRevision &&
        left.Selection.ProjectConfigurationRevision == right.Selection.ProjectConfigurationRevision &&
        left.Selection.PlatformRuntimeRevision == right.Selection.PlatformRuntimeRevision &&
        left.Selection.ContextRevision == right.Selection.ContextRevision &&
        left.Selection.Snapshot.GetRawText() == right.Selection.Snapshot.GetRawText() &&
        left.SelectionHash == right.SelectionHash &&
        left.DecisionStateVersion == right.DecisionStateVersion;

    private static bool SameRootBinding(
        MafBacklogAcceptedRootEvidence prepared,
        AcceptedRootBinding current) =>
        prepared.Root == current.Root &&
        prepared.Identity == new SessionIdentity(
            current.Root.ProjectId, current.Root.RunId, current.Root.RootSessionId) &&
        prepared.Actor == current.Actor &&
        prepared.TenantId == current.TenantId &&
        SameSelection(prepared.Selection, current.Selection) &&
        prepared.AcceptedSelectionHash == current.SelectionHash &&
        prepared.ExecutionFence == current.Root.ExecutionFence &&
        prepared.CurrentDecision.StateVersion == current.DecisionStateVersion &&
        prepared.CurrentDecision.State.Fence == current.Root.ExecutionFence &&
        prepared.CurrentDecision.SelectionHash == current.SelectionHash;

    private static bool SameSelection(EffectiveRunSelection left, EffectiveRunSelection right) =>
        left.ProjectId == right.ProjectId &&
        left.RunId == right.RunId &&
        left.ProjectRevision == right.ProjectRevision &&
        left.ProjectConfigurationRevision == right.ProjectConfigurationRevision &&
        left.PlatformRuntimeRevision == right.PlatformRuntimeRevision &&
        left.ContextRevision == right.ContextRevision &&
        left.Snapshot.GetRawText() == right.Snapshot.GetRawText();

    private static bool SameCheckpoint(
        MafExecutionCheckpointSnapshot left,
        MafExecutionCheckpointSnapshot right) =>
        left.Info.SessionId == right.Info.SessionId &&
        left.Info.CheckpointId == right.Info.CheckpointId &&
        MafExecutionCheckpointContract.Serialize(left.State).GetRawText() ==
        MafExecutionCheckpointContract.Serialize(right.State).GetRawText();

    private static bool SameOutputEvidence(
        MafBacklogAcceptedRootEvidence left,
        MafBacklogAcceptedRootEvidence right) =>
        SameOutputSet(left.OutputSet, right.OutputSet) &&
        left.MissingFixedAssociationIds.SequenceEqual(right.MissingFixedAssociationIds) &&
        SameOutputWitness(left.OutputWitness, right.OutputWitness);

    private static bool SameOutputSet(
        MafBacklogOutputSetSnapshot? left,
        MafBacklogOutputSetSnapshot? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.WorkPlanId == right.WorkPlanId &&
              left.Digest == right.Digest &&
              left.Obligations.SequenceEqual(right.Obligations);

    private static bool SameOutputWitness(
        MafExecutionOutputWitnessRecord? left,
        MafExecutionOutputWitnessRecord? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.RootIdentity == right.RootIdentity &&
              left.ExecutionFence == right.ExecutionFence &&
              left.WorkPlanId == right.WorkPlanId &&
              left.CheckpointId == right.CheckpointId &&
              left.CheckpointRevision == right.CheckpointRevision &&
              left.DecisionStateVersion == right.DecisionStateVersion &&
              left.AcceptedSelectionHash == right.AcceptedSelectionHash &&
              left.OutputSetSha256 == right.OutputSetSha256 &&
              left.ProofSha256 == right.ProofSha256 &&
              left.OutputSetCanonicalBytes.AsSpan().SequenceEqual(right.OutputSetCanonicalBytes.AsSpan());

    private static bool SameOwnerProofs(
        ImmutableArray<MafBacklogOwnerProof> left,
        ImmutableArray<MafBacklogOwnerProof> right)
    {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
            return false;
        for (var index = 0; index < left.Length; index++)
        {
            var equal = (left[index], right[index]) switch
            {
                (MafBacklogCaptureOwnerProof first, MafBacklogCaptureOwnerProof second) =>
                    first.Capture == second.Capture &&
                    first.State == second.State &&
                    first.ObjectKey == second.ObjectKey &&
                    first.EventPosition == second.EventPosition &&
                    first.AdmittedAt == second.AdmittedAt &&
                    first.Outputs.SequenceEqual(second.Outputs),
                (MafBacklogMergeOwnerProof first, MafBacklogMergeOwnerProof second) =>
                    first == second,
                (MafBacklogNoOutputOwnerProof first, MafBacklogNoOutputOwnerProof second) =>
                    first == second,
                _ => false
            };
            if (!equal)
                return false;
        }
        return true;
    }

    private static bool IsUnavailableBinding(CoordinationException exception) =>
        exception.Code is
            "backlog_prerequisite_binding_unavailable" or
            "coordinator_sandbox_binding_unavailable" or
            "maf_execution_checkpoint_binding_unavailable" or
            "maf_execution_checkpoint_forked" or
            "checkpoint_session_unavailable";

    private static bool IsStaleBinding(CoordinationException exception) =>
        exception.Code is
            "backlog_prerequisite_binding_stale" or
            "backlog_prerequisite_decision_stale" or
            "coordinator_sandbox_binding_stale" or
            "checkpoint_execution_fence_stale" or
            "checkpoint_model_binding_mismatch" or
            "checkpoint_run_selection_changed" or
            "maf_execution_output_witness_evidence_invalid" or
            "maf_execution_output_witness_owner_stale" or
            "source_control_output_capture_unavailable" or
            "source_control_intent_unavailable" or
            "source_control_run_binding_changed";

    private sealed record AcceptedRootBinding(
        MafBacklogTaskClaimTuple Claim,
        AcceptedRoot Root,
        CoordinationActor Actor,
        string TenantId,
        EffectiveRunSelection Selection,
        string SelectionHash,
        long DecisionStateVersion);

    private sealed record CurrentOutputCaptureInventory(
        SourceControlOwnerStore.OwnerBindingSnapshot CurrentOwner,
        ImmutableArray<SourceControlOutputCaptureRecord> Captures);
}
