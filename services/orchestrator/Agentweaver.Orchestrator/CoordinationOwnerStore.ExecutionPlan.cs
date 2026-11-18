using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed partial class CoordinationOwnerStore
{
    internal async Task<CoordinationExecutionPlanSnapshot> ReadExecutionPlanAsync(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        PostgresMafCheckpointStore checkpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (selection.Selection.ProjectId != identity.ProjectId ||
            selection.Selection.RunId != identity.RunId)
            throw ExecutionPlanUnavailable();

        var selectionHash = HashSelection(selection.Selection);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var run = await ReadAcceptedRunAsync(
            connection, transaction, identity.ProjectId, identity.RunId,
            forUpdate: false, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(run.TenantId, selection.Authorization.TenantId, StringComparison.Ordinal))
            throw new CoordinationException("accepted_run_not_found", StatusCodes.Status404NotFound);
        if (!string.Equals(run.SelectionHash, selectionHash, StringComparison.Ordinal))
            throw new CoordinationException("coordinator_session_stale", StatusCodes.Status409Conflict);

        var session = await ReadSessionAsync(
            connection, transaction, identity, forUpdate: false, cancellationToken).ConfigureAwait(false);
        RequireWriter(session, actor);
        if (session.ParentSessionId is not null ||
            session.RootSessionId != identity.SessionId ||
            session.NodeKind != "coordinator")
            throw new CoordinationException(
                "coordinator_session_unavailable", StatusCodes.Status404NotFound);
        if (session.ExecutionFence != run.Fence)
            throw new CoordinationException("execution_fence_stale", StatusCodes.Status409Conflict);

        if (_decisionStore is null)
            throw ExecutionPlanUnavailable();
        CoordinatorDecisionCurrentState decision;
        try
        {
            decision = await _decisionStore.ReadCurrentInTransactionAsync(
                connection, transaction, actor, identity, selection, cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (exception.Code == "coordinator_state_invalid")
        {
            throw ExecutionPlanUnavailable(exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or JsonException)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        if (decision.State.Fence != run.Fence)
            throw ExecutionPlanUnavailable();
        var plan = decision.State.ConfirmedWorkPlan;
        if (plan is null)
            throw new CoordinationException("execution_plan_not_found", StatusCodes.Status404NotFound);
        RequireValidPlan(plan);

        var checkpointBinding = CoordinationEndpoints.CreateExecutionCheckpointBinding(
            identity,
            actor,
            run.Fence,
            MafExecutionCheckpointStore.CurrentSdkVersion,
            PostgresMafCheckpointStore.ReadSelectedModelReference(selection.Selection.Snapshot));
        MafExecutionCheckpointSnapshot? checkpoint;
        try
        {
            checkpoint = await new MafExecutionCheckpointStore(checkpoints, checkpointBinding)
                .ReadLatestReadOnlyInTransactionAsync(
                    connection, transaction, selectionHash, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (exception.StatusCode != StatusCodes.Status403Forbidden)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or JsonException)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        if (checkpoint is not null &&
            (checkpoint.Info.SessionId != identity.SessionId ||
             checkpoint.State.WorkPlanId != plan.Plan.Id ||
             checkpoint.State.DecisionStateVersion > decision.StateVersion))
            throw ExecutionPlanUnavailable();

        var selectedRecovery = checkpoint is null
            ? null
            : await ReadSelectedExecutionRecoveryAsync(
                connection, transaction, identity, actor, selection, selectionHash,
                run.Fence, decision.StateVersion, plan, checkpoint, cancellationToken).ConfigureAwait(false);
        var children = await ReadExecutionPlanChildrenAsync(
            connection, transaction, identity, actor, selection, selectionHash,
            run.Fence, decision.StateVersion, plan, checkpoint, selectedRecovery?.Manifest,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var currentRun = await ReadAcceptedRunAsync(
                connection, transaction, identity.ProjectId, identity.RunId,
                forUpdate: true, cancellationToken).ConfigureAwait(false);
            var currentRoot = await ReadSessionAsync(
                connection, transaction, identity, forUpdate: true, cancellationToken).ConfigureAwait(false);
            if (currentRun.Fence != run.Fence ||
                currentRun.TenantId != run.TenantId ||
                !string.Equals(currentRun.SelectionHash, selectionHash, StringComparison.Ordinal) ||
                currentRoot.ParentSessionId is not null ||
                currentRoot.RootSessionId != identity.SessionId ||
                currentRoot.NodeKind != "coordinator" ||
                currentRoot.WriterIssuer != actor.Issuer ||
                currentRoot.WriterSubject != actor.Subject ||
                currentRoot.ExecutionFence != run.Fence)
                throw ExecutionPlanUnavailable();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CoordinationException exception) when (exception.StatusCode == StatusCodes.Status404NotFound)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        return new CoordinationExecutionPlanSnapshot(
            identity,
            selectionHash,
            run.Fence,
            decision.StateVersion,
            plan,
            checkpoint is null ? null : ToExecutionCheckpoint(checkpoint),
            children,
            selectedRecovery?.Snapshot);
    }

    internal static void RequireValidPlan(WorkPlanSnapshot plan)
    {
        if (plan.Workflow?.Definition is not { } definition ||
            definition.Steps.IsDefaultOrEmpty ||
            definition.Steps.Length > WorkflowDomainLimits.MaximumSteps ||
            definition.MaximumWorkItems is < 1 or > WorkflowDomainLimits.MaximumWorkItems ||
            plan.Plan is null || plan.Plan.Items.IsDefault ||
            plan.Plan.Items.Length > WorkflowDomainLimits.MaximumWorkItems ||
            plan.Plan.Items.Length > definition.MaximumWorkItems)
            throw ExecutionPlanUnavailable();
    }

    private static CoordinationExecutionCheckpointSnapshot ToExecutionCheckpoint(
        MafExecutionCheckpointSnapshot checkpoint)
    {
        var state = checkpoint.State;
        return new CoordinationExecutionCheckpointSnapshot(
            checkpoint.Info.CheckpointId,
            state.Revision,
            checkpoint.PayloadHash,
            checkpoint.ParentCheckpointId,
            checkpoint.HasParentMetadata,
            state.WorkPlanId,
            state.DecisionStateVersion,
            new CoordinationExecutionProgressSnapshot(
                ToProgress(state.Progress.WorkItems),
                ToProgress(state.Progress.NonModelSteps),
                ToProgress(state.Progress.FixedWorkItems))
            {
                PendingDispatches = state.PendingDispatches.Values
                    .OrderBy(item => item.AssociationId, StringComparer.Ordinal)
                    .Select(item => new CoordinationExecutionPendingDispatchSnapshot(
                        item.AssociationId, item.ChildSessionId, item.MessageId, item.PromptHash))
                    .ToImmutableArray()
            });
    }

    private static ImmutableArray<CoordinationExecutionProgressEntry> ToProgress<T>(
        IEnumerable<KeyValuePair<string, T>> values)
        where T : struct, Enum =>
        values.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new CoordinationExecutionProgressEntry(
                item.Key, JsonNamingPolicy.CamelCase.ConvertName(item.Value.ToString())))
            .ToImmutableArray();

    private async Task<(CoordinationExecutionRecoverySnapshot Snapshot, ChildCheckpointRecoveryManifest Manifest)?>
        ReadSelectedExecutionRecoveryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            SessionIdentity identity,
            CoordinationActor actor,
            AuthorizedRunSelection selection,
            string selectionHash,
            long executionFence,
            long decisionStateVersion,
            WorkPlanSnapshot plan,
            MafExecutionCheckpointSnapshot checkpoint,
            CancellationToken cancellationToken)
    {
        var lineage = checkpoint.State.RecoveryLineage;
        if (lineage is null)
            return null;
        if (lineage.RecoveryExecutionFence != executionFence ||
            lineage.SourceExecutionFence < 1 ||
            lineage.SourceCheckpointId == checkpoint.Info.CheckpointId)
            throw ExecutionPlanUnavailable();

        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
              AND operation_id = @operation AND operation_kind = 'recovery'
            """, connection, transaction);
        AddRunScope(command, identity.ProjectId, identity.RunId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, lineage.RecoveryOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw ExecutionPlanUnavailable();
        var resultJson = reader.GetString(0);
        var progressJson = reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);

        RunExecutionTransitionResult? operation;
        using JsonDocument progress = JsonDocument.Parse(progressJson);
        try
        {
            operation = JsonSerializer.Deserialize<RunExecutionTransitionResult>(resultJson, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw ExecutionPlanUnavailable(exception);
        }
        if (operation?.CheckpointRecovery is not { BoundaryKind: "childJoin" } status)
            return null;

        if (operation.OperationId != lineage.RecoveryOperationId ||
            operation.Session != identity ||
            operation.ExecutionFence != executionFence ||
            operation.PreviousExecutionFence != lineage.SourceExecutionFence ||
            status.ExecutionFence != executionFence ||
            status.SourceCheckpointId != lineage.SourceCheckpointId ||
            status.SourceCheckpointRevision != lineage.SourceCheckpointRevision ||
            status.SourceCheckpointHash != lineage.SourceCheckpointHash ||
            status.SourceCheckpointHash is null ||
            status.ChildManifestSha256 is null)
            throw ExecutionPlanUnavailable();

        ChildCheckpointRecoveryManifest manifest;
        try
        {
            manifest = ReadCheckpointRecoveryManifest(operation, progress.RootElement);
        }
        catch (CoordinationException exception) when (
            exception.Code is "checkpoint_recovery_manifest_invalid" or
                "checkpoint_recovery_manifest_too_large")
        {
            throw ExecutionPlanUnavailable(exception);
        }
        if (manifest.Root != identity ||
            manifest.TenantId != selection.Authorization.TenantId ||
            manifest.ActorIssuer != actor.Issuer ||
            manifest.ActorId != actor.Subject ||
            !string.Equals(manifest.SelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
            manifest.WorkPlanId != plan.Plan.Id ||
            manifest.WorkPlanHash != ChildCheckpointRecoveryManifestCodec.Hash(plan.Plan) ||
            manifest.WorkflowHash != ChildCheckpointRecoveryManifestCodec.Hash(plan.Workflow.Definition) ||
            manifest.SourceCheckpointId != lineage.SourceCheckpointId ||
            manifest.SourceCheckpointRevision != lineage.SourceCheckpointRevision ||
            manifest.SourceCheckpointHash != lineage.SourceCheckpointHash ||
            manifest.SourceExecutionFence != lineage.SourceExecutionFence ||
            manifest.SourceDecisionStateVersion > decisionStateVersion ||
            manifest.HoldFence != executionFence ||
            ChildCheckpointRecoveryManifestCodec.Hash(manifest) != status.ChildManifestSha256)
            throw ExecutionPlanUnavailable();

        var members = manifest.Members
            .OrderBy(member => member.AssociationId, StringComparer.Ordinal)
            .Select(member => new CoordinationExecutionRecoveryMemberSnapshot(
                member.AssociationId,
                member.Session.SessionId,
                member.Session.WorkPlanItemId,
                member.WorkflowStepId,
                member.AgentId,
                member.ModelSelectionReference,
                member.IsolationProviderId,
                member.MessageId,
                member.PromptHash,
                ChildCheckpointRecoveryManifestCodec.Hash(member.Startup.Binding),
                member.SpawnCommandHash,
                member.MessageStatus,
                member.MessageRequestHash,
                member.Progress,
                member.Disposition,
                member.ResultHash,
                member.TurnReceiptHash,
                ChildCheckpointRecoveryManifestCodec.Hash(member)))
            .ToImmutableArray();
        return (
            new CoordinationExecutionRecoverySnapshot(
                operation.OperationId,
                selectionHash,
                manifest.WorkPlanId,
                manifest.WorkPlanHash,
                manifest.WorkflowHash,
                manifest.SourceCheckpointId,
                manifest.SourceCheckpointRevision,
                manifest.SourceCheckpointHash,
                manifest.SourceExecutionFence,
                manifest.SourceDecisionStateVersion,
                manifest.HoldFence,
                status.ChildManifestSha256,
                members)
            {
                Phase = status.Phase,
                CurrentStep = status.CurrentStep,
                DispatchHeld = status.DispatchHeld
            },
            manifest);
    }

    private async Task<ImmutableArray<CoordinationExecutionChildSnapshot>> ReadExecutionPlanChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity root,
        CoordinationActor actor,
        AuthorizedRunSelection selection,
        string selectionHash,
        long executionFence,
        long decisionStateVersion,
        WorkPlanSnapshot plan,
        MafExecutionCheckpointSnapshot? checkpoint,
        ChildCheckpointRecoveryManifest? recoveryManifest,
        CancellationToken cancellationToken)
    {
        var state = checkpoint?.State;
        var fixedAssociations = state?.FixedWorkAssociations.Values
            .GroupBy(item => item.ChildSessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, MafExecutionFixedWorkAssociation[]>(StringComparer.Ordinal);
        var recoveryMembers = recoveryManifest?.Members
            .GroupBy(member => member.Session.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, ChildRecoveryMember[]>(StringComparer.Ordinal);
        var workItems = plan.Plan.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var childIds = new HashSet<string>(StringComparer.Ordinal);
        var referencedChildIds = new HashSet<string>(StringComparer.Ordinal);
        var associations = new HashSet<string>(StringComparer.Ordinal);
        var children = ImmutableArray.CreateBuilder<CoordinationExecutionChildSnapshot>();
        if (state is not null)
        {
            foreach (var dispatch in state.PendingDispatches.Values)
                referencedChildIds.Add(dispatch.ChildSessionId);
            foreach (var association in state.FixedWorkAssociations.Values)
                referencedChildIds.Add(association.ChildSessionId);
            foreach (var member in recoveryManifest?.Members ?? [])
                referencedChildIds.Add(member.Session.SessionId);
            foreach (var associationId in state.Results.Keys.Concat(state.ChildTurnReceipts.Keys))
            {
                if (state.FixedWorkAssociations.TryGetValue(associationId, out var fixedWork))
                {
                    referencedChildIds.Add(fixedWork.ChildSessionId);
                }
                else if (workItems.ContainsKey(associationId))
                {
                    referencedChildIds.Add(MafExecutionIds.CreateChildSessionId(
                        root, plan.Plan.Id, associationId));
                }
                else
                {
                    throw ExecutionPlanUnavailable();
                }
            }
        }

        await using var command = new NpgsqlCommand($"""
            SELECT s.session_id, s.parent_session_id, s.root_session_id, s.writer_issuer,
                   s.writer_subject, s.execution_fence, s.lifecycle_state, s.work_plan_item_id,
                   s.node_kind, s.detached, starts.intent_count, starts.startup_binding
            FROM {_sessions} s
            CROSS JOIN LATERAL (
                SELECT count(*)::integer AS intent_count,
                       (SELECT i.receipt_links -> 'childStart'
                        FROM {_schema}.runtime_run_start_intents i
                        WHERE i.tenant_id = @tenant
                          AND i.project_id = s.project_id AND i.run_id = s.run_id
                          AND i.root_session_id = s.session_id
                          AND lower(i.accepted_selection_hash) = lower(@selection_hash)
                          AND i.execution_fence = s.execution_fence
                        ORDER BY i.row_version DESC
                        LIMIT 1)::text AS startup_binding
                FROM {_schema}.runtime_run_start_intents i
                WHERE i.tenant_id = @tenant
                  AND i.project_id = s.project_id AND i.run_id = s.run_id
                  AND i.root_session_id = s.session_id
                  AND lower(i.accepted_selection_hash) = lower(@selection_hash)
                  AND i.execution_fence = s.execution_fence
            ) starts
            WHERE s.project_id = @project AND s.run_id = @run
              AND s.parent_session_id = @root
            ORDER BY s.session_id
            LIMIT 513
            """, connection, transaction);
        AddRunScope(command, root.ProjectId, root.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, selection.Authorization.TenantId);
        command.Parameters.AddWithValue("selection_hash", NpgsqlDbType.Varchar, selectionHash);

        await using (var countCommand = new NpgsqlCommand($"""
                         SELECT count(*)::integer
                         FROM (
                             SELECT 1 FROM {_sessions}
                             WHERE project_id = @project AND run_id = @run
                               AND parent_session_id = @root
                             LIMIT 513
                         ) child_limit
                         """, connection, transaction))
        {
            AddRunScope(countCommand, root.ProjectId, root.RunId);
            countCommand.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
            if ((int)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) >
                ChildCheckpointRecoveryManifestCodec.MaximumChildren)
                throw ExecutionPlanUnavailable();
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (children.Count == ChildCheckpointRecoveryManifestCodec.MaximumChildren ||
                !childIds.Add(reader.GetString(0)))
                throw ExecutionPlanUnavailable();

            var childId = reader.GetString(0);
            var parentId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var childRootId = reader.IsDBNull(2) ? null : reader.GetString(2);
            var childFence = reader.GetInt64(5);
            var workPlanItemId = reader.IsDBNull(7) ? null : reader.GetString(7);
            if (parentId != root.SessionId || childRootId != root.SessionId ||
                reader.GetString(8) != "child_work" ||
                reader.GetString(3) != actor.Issuer || reader.GetString(4) != actor.Subject)
                throw ExecutionPlanUnavailable();

            string associationId;
            string stepId;
            string agentId;
            string modelReference;
            if (workPlanItemId is not null)
            {
                if (!workItems.TryGetValue(workPlanItemId, out var item))
                    throw ExecutionPlanUnavailable();
                associationId = item.Id;
                stepId = item.WorkflowStepId;
                agentId = item.AgentId;
                modelReference = item.ModelSelectionReference;
            }
            else
            {
                if (!fixedAssociations.TryGetValue(childId, out var matches) || matches.Length != 1)
                    throw ExecutionPlanUnavailable();
                var fixedWork = matches[0];
                associationId = fixedWork.AssociationId;
                stepId = fixedWork.StepId;
                agentId = fixedWork.AgentId;
                modelReference = fixedWork.ModelSelectionReference;
            }
            if (!associations.Add(associationId))
                throw ExecutionPlanUnavailable();

            var intentCount = reader.GetInt32(10);
            var bindingJson = reader.IsDBNull(11) ? null : reader.GetString(11);
            RuntimeChildStartBinding? binding = null;
            if (intentCount > 1)
                throw ExecutionPlanUnavailable();
            if (intentCount == 1 && bindingJson is not null)
            {
                try
                {
                    binding = JsonSerializer.Deserialize<RuntimeChildStartBinding>(bindingJson, JsonOptions);
                }
                catch (JsonException exception)
                {
                    throw ExecutionPlanUnavailable(exception);
                }
            }

            ChildRecoveryMember? frozenMember = null;
            if (recoveryMembers.TryGetValue(childId, out var matchingMembers))
            {
                if (matchingMembers.Length != 1)
                    throw ExecutionPlanUnavailable();
                frozenMember = matchingMembers[0];
            }
            if (binding is null && frozenMember is not null)
                binding = frozenMember.Startup.Binding;
            if (binding is null ||
                binding.ParentRootSessionId != root.SessionId ||
                binding.ChildSessionId != childId ||
                binding.WorkPlanId != plan.Plan.Id ||
                binding.AssociationId != associationId ||
                binding.WorkPlanItemId != workPlanItemId ||
                binding.WorkflowStepId != stepId ||
                binding.AgentId != agentId ||
                binding.ModelSelectionReference != modelReference ||
                binding.ActorIssuer != actor.Issuer ||
                binding.ActorId != actor.Subject ||
                binding.DecisionStateVersion < 1 ||
                binding.DecisionStateVersion > decisionStateVersion ||
                binding.SpawnCommandId == Guid.Empty ||
                binding.DispatchMessageId == Guid.Empty ||
                !IsSha256(binding.PromptHash) ||
                childId != MafExecutionIds.CreateChildSessionId(root, plan.Plan.Id, associationId))
                throw ExecutionPlanUnavailable();

            if (checkpoint is null && frozenMember is null)
                throw ExecutionPlanUnavailable();
            if (childFence != executionFence &&
                (frozenMember is null || recoveryManifest!.SourceExecutionFence != childFence))
                throw ExecutionPlanUnavailable();
            if (frozenMember is not null &&
                (frozenMember.AssociationId != associationId ||
                 frozenMember.Session.SessionId != childId ||
                 frozenMember.Session.WorkPlanItemId != workPlanItemId ||
                 frozenMember.Startup.Binding != binding))
                throw ExecutionPlanUnavailable();
            if (state is not null &&
                state.PendingDispatches.TryGetValue(associationId, out var dispatch) &&
                (dispatch.ChildSessionId != childId ||
                 dispatch.MessageId != binding.DispatchMessageId ||
                 dispatch.PromptHash != binding.PromptHash))
                throw ExecutionPlanUnavailable();

            var resultHash = state is not null && state.Results.TryGetValue(associationId, out var result)
                ? ChildCheckpointRecoveryManifestCodec.Hash(result)
                : null;
            var receiptHash = state is not null &&
                              state.ChildTurnReceipts.TryGetValue(associationId, out var receipt)
                ? ChildCheckpointRecoveryManifestCodec.Hash(receipt)
                : null;
            children.Add(new CoordinationExecutionChildSnapshot(
                childId,
                parentId,
                workPlanItemId,
                associationId,
                reader.GetString(6),
                childFence,
                reader.GetBoolean(9),
                binding.DispatchMessageId,
                binding.PromptHash,
                ChildCheckpointRecoveryManifestCodec.Hash(binding),
                resultHash,
                receiptHash));
        }

        if (children.Count > ChildCheckpointRecoveryManifestCodec.MaximumChildren)
            throw ExecutionPlanUnavailable();
        if (referencedChildIds.Except(childIds, StringComparer.Ordinal).Any())
            throw ExecutionPlanUnavailable();
        return children.ToImmutable();
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static CoordinationException ExecutionPlanUnavailable(Exception? inner = null) =>
        new("execution_plan_unavailable", StatusCodes.Status409Conflict, inner);
}
