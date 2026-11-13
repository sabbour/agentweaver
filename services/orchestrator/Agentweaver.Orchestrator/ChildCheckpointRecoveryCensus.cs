using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal static class ChildCheckpointRecoveryCensus
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<ImmutableArray<ChildRecoverySession>> LockChildrenAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, parent_session_id, root_session_id, writer_issuer, writer_subject,
                   execution_fence, lifecycle_state, turn_state, logical_turn_ordinal,
                   state_version, work_plan_item_id, node_kind, detached, archived_at
            FROM {schema}.coordination_sessions
            WHERE project_id = @project AND run_id = @run AND session_id <> @root
            ORDER BY session_id
            LIMIT 513
            FOR UPDATE
            """, connection, transaction);
        AddRun(command, root);
        var children = ImmutableArray.CreateBuilder<ChildRecoverySession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (children.Count == ChildCheckpointRecoveryManifestCodec.MaximumChildren ||
                reader.GetString(11) != "child_work" || reader.IsDBNull(2) || reader.GetBoolean(12))
                throw InvalidBoundary();
            children.Add(new ChildRecoverySession(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetBoolean(12), reader.GetString(3), reader.GetString(4), reader.GetInt64(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
        }
        return children.ToImmutable();
    }

    internal static async Task<ChildCheckpointRecoveryManifest> BuildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        CoordinationActor actor,
        SessionIdentity root,
        string tenantId,
        string selectionHash,
        long sourceFence,
        Guid operationId,
        CheckpointRecoveryRequest request,
        ChildRecoveryRootStartup rootStartup,
        MafExecutionCheckpointSnapshot source,
        WorkPlanSnapshot plan,
        ImmutableArray<ChildRecoverySession> children,
        CancellationToken cancellationToken,
        ChildCheckpointRecoveryManifest? originManifest = null)
    {
        if (children.IsEmpty || plan.Plan.Id != source.State.WorkPlanId)
            throw InvalidBoundary();
        if (originManifest is not null &&
            (originManifest.Root != root || originManifest.HoldFence != sourceFence ||
             sourceFence <= 0 || originManifest.SourceExecutionFence != sourceFence - 1 ||
             originManifest.Members.Length != children.Length))
            throw InvalidBoundary();
        var originFence = originManifest?.SourceExecutionFence ?? sourceFence;
        var originRootStartup = originManifest?.RootStartup ?? rootStartup;
        var expected = new HashSet<string>(StringComparer.Ordinal);
        expected.UnionWith(source.State.PendingDispatches.Keys);
        expected.UnionWith(source.State.FixedWorkAssociations.Keys);
        expected.UnionWith(source.State.Results.Keys);
        expected.UnionWith(source.State.ChildTurnReceipts.Keys);
        expected.UnionWith(source.State.Progress.WorkItems
            .Where(item => item.Value != MafExecutionTaskStatus.Pending).Select(item => item.Key));
        expected.UnionWith(source.State.Progress.FixedWorkItems
            .Where(item => item.Value != MafExecutionTaskStatus.Pending).Select(item => item.Key));
        if (expected.Count != children.Length || expected.Count > ChildCheckpointRecoveryManifestCodec.MaximumChildren)
            throw InvalidBoundary();

        var budget = new ChildCheckpointRecoveryManifestCodec.ByteBudget();
        budget.Include(rootStartup);
        var members = ImmutableArray.CreateBuilder<ChildRecoveryMember>(children.Length);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (child.ParentSessionId != root.SessionId || child.RootSessionId != root.SessionId ||
                child.WriterIssuer != actor.Issuer || child.WriterSubject != actor.Subject ||
                child.ExecutionFence != sourceFence)
                throw InvalidBoundary();
            var originMatches = originManifest?.Members
                .Where(member => member.Session.SessionId == child.SessionId).ToArray();
            if (originManifest is not null && originMatches!.Length != 1)
                throw InvalidBoundary();
            var originMember = originMatches?.SingleOrDefault();
            var (startup, registration) = await ReadStartupAsync(
                connection, transaction, schema, root, child, tenantId, selectionHash, sourceFence,
                cancellationToken, originMember, originFence, originManifest?.OperationId).ConfigureAwait(false);
            var binding = startup.Binding;
            if (binding.ParentRootSessionId != root.SessionId ||
                binding.ChildSessionId != child.SessionId || binding.WorkPlanId != plan.Plan.Id ||
                binding.ActorIssuer != actor.Issuer || binding.ActorId != actor.Subject ||
                binding.DecisionStateVersion > source.State.DecisionStateVersion ||
                binding.SpawnCommandId == Guid.Empty || binding.DispatchMessageId == Guid.Empty ||
                binding.PromptHash.Length != 64 || !binding.PromptHash.All(Uri.IsHexDigit) ||
                !expected.Contains(binding.AssociationId) || !observed.Add(binding.AssociationId) ||
                child.SessionId != MafExecutionIds.CreateChildSessionId(root, plan.Plan.Id, binding.AssociationId))
                throw InvalidBoundary();
            if (originMember is not null &&
                (originMember.AssociationId != binding.AssociationId ||
                 originMember.Startup.Binding != binding ||
                 originMember.MessageId != binding.DispatchMessageId ||
                 originMember.PromptHash != binding.PromptHash))
                throw InvalidBoundary();

            var spawnHash = await ReadSpawnCommandHashAsync(
                connection, transaction, schema, root, child, binding, actor, originFence, cancellationToken)
                .ConfigureAwait(false);
            if (originMember is not null && originMember.SpawnCommandHash != spawnHash)
                throw InvalidBoundary();
            var fixedAssociation = source.State.FixedWorkAssociations.GetValueOrDefault(binding.AssociationId);
            var workItem = plan.Plan.Items.SingleOrDefault(item => item.Id == binding.AssociationId);
            if ((fixedAssociation is null) == (workItem is null))
                throw InvalidBoundary();
            var stepId = fixedAssociation?.StepId ?? workItem!.WorkflowStepId;
            var agentId = fixedAssociation?.AgentId ?? workItem!.AgentId;
            var model = fixedAssociation?.ModelSelectionReference ?? workItem!.ModelSelectionReference;
            var provider = fixedAssociation?.IsolationProviderId ?? workItem!.IsolationProviderId;
            if (binding.WorkflowStepId != stepId || binding.AgentId != agentId ||
                binding.ModelSelectionReference != model ||
                binding.WorkPlanItemId != workItem?.Id || child.WorkPlanItemId != workItem?.Id ||
                fixedAssociation is not null &&
                (fixedAssociation.ChildSessionId != child.SessionId ||
                 fixedAssociation.WorkPlanId != plan.Plan.Id ||
                 !string.Equals(
                     fixedAssociation.AcceptedSelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
                 fixedAssociation.ExecutionFence != originFence ||
                 fixedAssociation.DecisionStateVersion != binding.DecisionStateVersion ||
                 fixedAssociation.WorkflowId != plan.Workflow.Definition.Id ||
                 fixedAssociation.DefinitionRevision != plan.Workflow.Definition.Revision ||
                 fixedAssociation.CatalogVersion != plan.Workflow.Definition.CatalogVersion) ||
                registration is not null &&
                (registration.Binding.PlacementProviderId != provider ||
                 startup.Placement is not null && startup.Placement.SandboxResource.ProviderId != provider))
                throw InvalidBoundary();

            source.State.PendingDispatches.TryGetValue(binding.AssociationId, out var intent);
            if (intent is not null &&
                (intent.AssociationId != binding.AssociationId || intent.ChildSessionId != child.SessionId ||
                 intent.MessageId != binding.DispatchMessageId || intent.PromptHash != binding.PromptHash))
                throw InvalidBoundary();
            var native = await ReadNativeOriginAsync(
                connection, transaction, schema, root, child, binding, selectionHash,
                cancellationToken).ConfigureAwait(false);
            NativeChildRebindReceipt? priorNativeRebind = null;
            if (native is not null)
            {
                var rebinds = await new RuntimeExecutionEvidenceLedgerStore(schema)
                    .ReadNativeChildRebindsAsync(
                        connection, transaction, root.ProjectId, root.RunId, native.DispatchId,
                        cancellationToken).ConfigureAwait(false);
                priorNativeRebind = rebinds.LastOrDefault();
                if (originMember is not null &&
                    (originMember.PriorNativeRebindReceiptId != priorNativeRebind?.ReceiptId ||
                     originMember.PriorNativeRebindPayloadHash != priorNativeRebind?.CanonicalPayloadHash))
                    throw InvalidBoundary();
            }
            if (native is not null)
            {
                if (priorNativeRebind is null)
                {
                    if (originMember is null
                        ? registration is null || native.ExecutionFence != sourceFence ||
                          native.RuntimeInstanceId != registration.RuntimeInstanceId ||
                          native.RegistrationRevision != registration.Revision ||
                          native.RuntimeBindingHash != RuntimeContractValidation.RegistrationHash(registration)
                        : originMember.Startup.RegistrationId != native.RuntimeInstanceId ||
                          originMember.Startup.RegistrationRevision != native.RegistrationRevision ||
                          originMember.NativeOrigin?.RuntimeBindingHash != native.RuntimeBindingHash)
                        throw InvalidBoundary();
                }
                else
                {
                    var effectiveStartup = originMember?.Startup ?? startup;
                    if (priorNativeRebind.DispatchId != native.DispatchId ||
                         priorNativeRebind.OriginOwnerRevision != native.OwnerRevision ||
                         priorNativeRebind.OriginState != native.State ||
                         effectiveStartup.RegistrationId != priorNativeRebind.RuntimeInstanceId ||
                         effectiveStartup.RegistrationRevision != priorNativeRebind.RegistrationRevision ||
                         effectiveStartup.RegistrationBindingHash != priorNativeRebind.RegistrationHash)
                        throw InvalidBoundary();
                }
            }
            var originIntent = native is null
                ? intent
                : await ReadOriginIntentAsync(
                    connection, transaction, schema, root, native, binding, selectionHash, cancellationToken)
                    .ConfigureAwait(false);
            if (intent is not null && originIntent != intent)
                throw InvalidBoundary();
            if (originIntent?.EgressReceipt is { } egress &&
                (egress.Fence.Owner.TenantId != tenantId ||
                 egress.Fence.Owner.ProjectId != root.ProjectId ||
                 egress.Fence.Owner.RunId != root.RunId ||
                 egress.Fence.Owner.EnvironmentId != originRootStartup.EnvironmentId ||
                 egress.Fence.LifecycleGeneration != originRootStartup.Placement.LifecycleGeneration ||
                 egress.PolicyGeneration != originRootStartup.Placement.NetworkPolicyGeneration ||
                 egress.ContextRevision != binding.ContextRevision))
                throw InvalidBoundary();
            var (messageStatus, messageRequestHash) = await ReadMessageAsync(
                connection, transaction, schema, root, child, binding, actor, cancellationToken)
                .ConfigureAwait(false);
            var progress = workItem is not null
                ? source.State.Progress.WorkItems.GetValueOrDefault(binding.AssociationId)
                : source.State.Progress.FixedWorkItems.GetValueOrDefault(binding.AssociationId);
            if (intent is not null && (registration is null || startup.Placement is null))
                throw InvalidBoundary();
            var hasResult = source.State.Results.TryGetValue(binding.AssociationId, out var result);
            var hasReceipt = source.State.ChildTurnReceipts.TryGetValue(binding.AssociationId, out var receipt);
            var workItemBindingHash = workItem is null
                ? null : RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(workItem));
            var boundReceipt = hasReceipt && IsJoinedReceiptBound(
                receipt!, native, binding, selectionHash, workItemBindingHash);
            var (disposition, reason) = Classify(
                child, startup, native, intent, progress, hasResult, boundReceipt);
            var member = new ChildRecoveryMember(
                binding.AssociationId, child, stepId, agentId, model,
                registration?.Binding.ReasoningEffort, provider,
                fixedAssociation is null
                    ? ChildCheckpointRecoveryManifestCodec.Hash(workItem)
                    : ChildCheckpointRecoveryManifestCodec.Hash(fixedAssociation),
                spawnHash, fixedAssociation?.ActivationRevision, startup,
                binding.DispatchMessageId, binding.PromptHash,
                originIntent?.EgressReceipt is null
                    ? null : ChildCheckpointRecoveryManifestCodec.Hash(originIntent.EgressReceipt),
                messageStatus, messageRequestHash, progress.ToString(), disposition, reason,
                native, native is null,
                hasResult ? ChildCheckpointRecoveryManifestCodec.Hash(result) : null,
                hasReceipt ? ChildCheckpointRecoveryManifestCodec.Hash(receipt) : null)
            {
                SavedRegistration = disposition == "completedUnjoined" ? registration : null,
                WorkItemBindingSha256 = workItemBindingHash,
                PriorNativeRebindReceiptId = priorNativeRebind?.ReceiptId,
                PriorNativeRebindPayloadHash = priorNativeRebind?.CanonicalPayloadHash
            };
            if (priorNativeRebind is not null &&
                NativeChildRebindOriginFingerprint.Create(tenantId, root, member).Hash !=
                    priorNativeRebind.OriginFingerprint)
                throw InvalidBoundary();
            budget.Include(member);
            members.Add(member);
        }

        await RequireNoExtraNativeOriginsAsync(
            connection, transaction, schema, root, members.Count(member => member.NativeOrigin is not null),
            cancellationToken).ConfigureAwait(false);
        var pins = await ReadRepositoryPinsAsync(
            connection, transaction, schema, root, selectionHash, budget, cancellationToken).ConfigureAwait(false);
        var captures = await ReadCapturesAsync(
            connection, transaction, schema, root, children, budget, cancellationToken).ConfigureAwait(false);
        var claim = await ReadClaimAsync(
            connection, transaction, schema, root, selectionHash, cancellationToken).ConfigureAwait(false);
        if (claim is not null)
            budget.Include(claim);
        var reservations = await ReadReservationsAsync(
            connection, transaction, schema, root, budget, cancellationToken).ConfigureAwait(false);
        var otherNativeWork = await ReadOtherNativeWorkAsync(
            connection, transaction, schema, root, budget, cancellationToken).ConfigureAwait(false);
        return new ChildCheckpointRecoveryManifest(
            ChildCheckpointRecoveryManifestCodec.Version, operationId, root, tenantId, actor.Issuer,
            actor.Subject, selectionHash, plan.Plan.Id, ChildCheckpointRecoveryManifestCodec.Hash(plan.Plan),
            ChildCheckpointRecoveryManifestCodec.Hash(plan.Workflow.Definition), source.Info.CheckpointId,
            source.State.Revision, source.PayloadHash, sourceFence, source.State.DecisionStateVersion,
            checked(sourceFence + 1), request.RuntimeInstanceId, request.RegistrationRevision,
            rootStartup, members.ToImmutable(), pins, captures, claim, reservations, otherNativeWork);
    }

    internal static async Task<string> ReadSpawnCommandHashAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildRecoverySession child,
        RuntimeChildStartBinding binding,
        CoordinationActor actor,
        long sourceFence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT source_session_id, target_session_id, actor_issuer, actor_subject, execution_fence,
                   command_kind, command_hash, result
            FROM {schema}.coordination_tree_commands
            WHERE project_id = @project AND run_id = @run AND command_id = @command
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        command.Parameters.AddWithValue("command", binding.SpawnCommandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != root.SessionId || reader.GetString(1) != child.SessionId ||
            reader.GetString(2) != actor.Issuer || reader.GetString(3) != actor.Subject ||
            reader.GetInt64(4) != sourceFence || reader.GetString(5) != "spawn")
            throw InvalidBoundary();
        try
        {
            using var result = JsonDocument.Parse(reader.GetString(7));
            var node = result.RootElement.GetProperty("node");
            var identity = node.GetProperty("identity");
            if (result.RootElement.GetProperty("commandId").GetGuid() != binding.SpawnCommandId ||
                identity.GetProperty("projectId").GetString() != root.ProjectId ||
                identity.GetProperty("runId").GetString() != root.RunId ||
                identity.GetProperty("sessionId").GetString() != child.SessionId ||
                node.GetProperty("parentSessionId").GetString() != root.SessionId ||
                node.GetProperty("rootSessionId").GetString() != root.SessionId ||
                node.GetProperty("executionFence").GetInt64() != sourceFence ||
                node.GetProperty("stateVersion").GetInt64() != binding.InitialStateVersion)
                throw InvalidBoundary();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                           InvalidOperationException or FormatException)
        {
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict, exception);
        }
        return reader.GetString(6);
    }

    internal static (string Disposition, string? Reason) Classify(
        ChildRecoverySession child,
        ChildRecoveryStartup startup,
        ChildRecoveryNativeOrigin? native,
        MafExecutionDispatchIntent? intent,
        MafExecutionTaskStatus progress,
        bool hasResult,
        bool hasReceipt)
    {
        if (native is { State: "sending" or "terminal_pending_accounting" or "indeterminate" })
            return ("unresolved", "native_send_or_accounting_unresolved");
        if (intent is null &&
            progress is MafExecutionTaskStatus.Succeeded or
                MafExecutionTaskStatus.Failed or MafExecutionTaskStatus.Indeterminate)
        {
            if (!HasCompletedNativeAccounting(native) ||
                !hasResult || !hasReceipt)
                return ("unresolved", "child_terminal_evidence_unavailable");
            return ("joined", null);
        }
        if (native is { State: "completed" })
        {
            if (hasResult || hasReceipt || intent is null ||
                progress != MafExecutionTaskStatus.Running ||
                child.Lifecycle != "active" || child.TurnState != "active" ||
                startup.CleanupObligation)
                return ("unresolved", "child_boundary_inconsistent");
            if (!HasCompletedNativeAccounting(native))
                return ("unresolved", "original_child_material_unavailable");
            return ("completedUnjoined", null);
        }
        if (hasResult || hasReceipt || intent is null || progress != MafExecutionTaskStatus.Running ||
            child.Lifecycle != "active" ||
            child.TurnState is "completed" or "failed" or "indeterminate")
            return ("unresolved", "child_boundary_inconsistent");
        if (startup.RegistrationId is null || startup.RegistrationRevision is null ||
            startup.CurrentRegistrationRevision != startup.RegistrationRevision ||
            startup.RegistrationState != nameof(RuntimeRegistrationState.Active) ||
            startup.Placement is null || string.IsNullOrWhiteSpace(startup.Placement.PodUid) ||
            startup.BootstrapReceiptHash is null || startup.BootstrapConfigurationHash is null ||
            intent.EgressReceipt is null ||
            startup.CleanupObligation)
            return ("unresolved", "child_startup_evidence_unavailable");
        if (native is not null && (native.State != "prepared" || native.OwnerRevision != 1))
            return ("unresolved", "native_state_unavailable");
        return ("pendingUnsent", null);
    }

    private static bool HasCompletedNativeAccounting(ChildRecoveryNativeOrigin? native) =>
        native is
        {
            State: "completed",
            TerminalOutcome: "native-completed",
            SourceReportStatus: "partial",
            SourceReportHash: not null,
            AccountingStatus: "priced"
        };

    internal static bool IsJoinedReceiptBound(
        RuntimeChildTurnCompletionReceipt receipt,
        ChildRecoveryNativeOrigin? native,
        RuntimeChildStartBinding binding,
        string selectionHash,
        string? workItemBindingHash) =>
        native is not null &&
        native.TerminalOutcome == "native-completed" &&
        receipt.RequestId == binding.DispatchMessageId &&
        receipt.RequestId == native.DispatchId &&
        receipt.WorkPlanId == binding.WorkPlanId &&
        receipt.AssociationId == binding.AssociationId &&
        receipt.ChildSessionId == binding.ChildSessionId &&
        receipt.PromptSha256 == binding.PromptHash &&
        receipt.ExecutionFence == native.ExecutionFence &&
        receipt.RuntimeInstanceId == native.RuntimeInstanceId &&
        receipt.RegistrationRevision == native.RegistrationRevision &&
        receipt.RegistrationSha256 == native.RuntimeBindingHash &&
        receipt.SourceSha256 == native.SdkSourceReceiptHash &&
        string.Equals(receipt.AcceptedSelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) &&
        receipt.ModelSelectionReference == native.ModelSelectionReference &&
        receipt.ModelId == native.SelectedModelId &&
        receipt.NativeOwnerRevision <= native.OwnerRevision &&
        receipt.AccountingOwnerRevision <= native.OwnerRevision &&
        receipt.WorkItemBindingSha256 == workItemBindingHash;

    internal static async Task<(ChildRecoveryStartup Startup, RuntimeRegistration? Registration)> ReadStartupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildRecoverySession child,
        string tenantId,
        string selectionHash,
        long sourceFence,
        CancellationToken cancellationToken,
        ChildRecoveryMember? originMember = null,
        long? originFence = null,
        Guid? originOperationId = null)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, intent_id, bootstrap_operation_id,
                   consume_operation_id, exchange_operation_id, state, current_step, failure_code,
                   environment_id, runtime_instance_id, receipt_links, cleanup_obligation, row_version
            FROM {schema}.runtime_run_start_intents
            WHERE project_id = @project AND run_id = @run AND root_session_id = @child
            ORDER BY tenant_id, accepted_selection_hash
            LIMIT 2
            FOR UPDATE
            """, connection, transaction);
        AddRun(command, root);
        command.Parameters.AddWithValue("child", child.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != tenantId ||
            !string.Equals(reader.GetString(1), selectionHash, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt64(2) != sourceFence)
            throw InvalidBoundary();
        var intent = new RuntimeRunStartIntent(
            reader.GetString(0), root.ProjectId, root.RunId, child.SessionId, reader.GetString(1),
            reader.GetInt64(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetGuid(6),
            reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.GetString(12),
            reader.GetBoolean(13), reader.GetInt64(14));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw InvalidBoundary();
        await reader.DisposeAsync().ConfigureAwait(false);
        try
        {
            using var links = JsonDocument.Parse(intent.ReceiptLinks);
            ValidateReceiptLinks(links.RootElement);
            var binding = CoordinationRuntimeRunStartStore.ReadChildBinding(intent);
            var registration = ReadReceipt<RuntimeRegistration>(links.RootElement, "registration");
            var placement = ReadReceipt<RuntimeRunStartPlacementReceipt>(links.RootElement, "placement");
            var bootstrap = ReadReceipt<RuntimeBootstrapDeliveryReceipt>(links.RootElement, "bootstrap");
            placement?.Validate();
            var bootstrapJson = links.RootElement.TryGetProperty("bootstrap", out var bootstrapValue)
                ? bootstrapValue
                : (JsonElement?)null;
            var hasCurrentBootstrap = originMember is not null &&
                originOperationId is { } recoveryId &&
                RuntimeRunStartContinuation.HasCheckpointRecoveryBootstrapEvidence(
                    recoveryId,
                    JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        links.RootElement.GetRawText(), JsonOptions) ?? new(StringComparer.Ordinal));
            if (registration is not null &&
                (registration.RuntimeInstanceId != intent.RuntimeInstanceId ||
                 registration.Binding.ActorIssuer != binding.ActorIssuer ||
                 registration.Binding.ActorId != binding.ActorId ||
                 registration.Binding.ProjectId != root.ProjectId ||
                 registration.Binding.RunId != root.RunId ||
                 registration.Binding.SessionId != child.SessionId ||
                 registration.Binding.TenantId != tenantId ||
                 !string.Equals(registration.Binding.AcceptedSelectionHash, selectionHash, StringComparison.OrdinalIgnoreCase) ||
                 registration.Binding.ExecutionFence != sourceFence ||
                 registration.Binding.AgentId != binding.AgentId ||
                 registration.Binding.ModelSelectionReference != binding.ModelSelectionReference ||
                 registration.Binding.WorkflowStepId != binding.WorkflowStepId ||
                 registration.Binding.ContextRevision != binding.ContextRevision ||
                 placement is not null &&
                 (intent.EnvironmentId != placement.EnvironmentId ||
                  registration.Binding.EnvironmentId != placement.EnvironmentId ||
                  registration.Binding.ProfileId != placement.SourceProfileId ||
                  registration.Binding.PlacementUid != placement.SandboxResource.ResourceId ||
                  registration.Binding.PlacementGeneration != placement.SandboxResource.Generation ||
                  registration.Binding.PlacementProviderId != placement.SandboxResource.ProviderId ||
                  registration.Binding.EnvironmentLifecycleGeneration != placement.LifecycleGeneration ||
                  registration.Binding.EnvironmentLeaseRevision != placement.LeaseRevision ||
                  registration.Binding.EnvironmentCurrentFencingGeneration != placement.CurrentFencingGeneration ||
                  registration.Binding.EnvironmentProviderFencingGeneration != placement.ProviderFencingGeneration)))
                throw InvalidBoundary();
            if (originMember is not null)
            {
                var previous = originMember.Startup;
                var originalPlacement = previous.Placement;
                var bootstrapMismatch = bootstrap is null || registration is null ||
                     hasCurrentBootstrap
                         ? bootstrap is null || registration is null ||
                           bootstrap.OperationId != intent.BootstrapOperationId ||
                           bootstrap.RuntimeInstanceId != registration.RuntimeInstanceId ||
                           bootstrap.RegistrationRevision != registration.Revision ||
                           bootstrap.PlacementUid != registration.Binding.PlacementUid ||
                           bootstrap.PlacementGeneration != registration.Binding.PlacementGeneration ||
                           bootstrap.ExecutionFence != registration.Binding.ExecutionFence ||
                           bootstrap.EnvironmentCurrentFencingGeneration !=
                               registration.Binding.EnvironmentCurrentFencingGeneration ||
                           bootstrap.EnvironmentProviderFencingGeneration !=
                               registration.Binding.EnvironmentProviderFencingGeneration
                         : bootstrap.OperationId != previous.BootstrapOperationId ||
                           bootstrap.RuntimeInstanceId != previous.RegistrationId ||
                           bootstrap.RegistrationRevision != previous.RegistrationRevision ||
                           originalPlacement is null ||
                           bootstrap.PlacementUid != originalPlacement.SandboxResource.ResourceId ||
                           bootstrap.PlacementGeneration != originalPlacement.SandboxResource.Generation ||
                           bootstrap.ExecutionFence != originFence ||
                           bootstrap.ConfigurationHash != previous.BootstrapConfigurationHash;
                if (originFence is null || originOperationId is null ||
                     previous.BootstrapReceiptHash is null ||
                     previous.BootstrapConfigurationHash is null ||
                     bootstrapJson is null ||
                     HashReceipt(bootstrapJson.Value) != previous.BootstrapReceiptHash ||
                     bootstrapMismatch)
                     throw InvalidBoundary();
            }
            else if (bootstrap is not null &&
                      (registration is null || bootstrap.OperationId != intent.BootstrapOperationId ||
                       bootstrap.RuntimeInstanceId != registration.RuntimeInstanceId ||
                       bootstrap.RegistrationRevision != registration.Revision ||
                       bootstrap.PlacementUid != registration.Binding.PlacementUid ||
                       bootstrap.PlacementGeneration != registration.Binding.PlacementGeneration ||
                       bootstrap.ExecutionFence != registration.Binding.ExecutionFence ||
                       bootstrap.EnvironmentCurrentFencingGeneration !=
                           registration.Binding.EnvironmentCurrentFencingGeneration ||
                       bootstrap.EnvironmentProviderFencingGeneration !=
                           registration.Binding.EnvironmentProviderFencingGeneration))
                throw InvalidBoundary();
            var (currentRevision, bindingHash) = registration is null
                ? ((long?)null, (string?)null)
                : await ReadRegistrationAsync(connection, transaction, schema, registration, cancellationToken)
                    .ConfigureAwait(false);
            var bootstrapHash = bootstrapJson is { } savedBootstrap
                ? HashReceipt(savedBootstrap)
                : null;
            var priorStarts = registration is null
                ? ImmutableArray<ChildRecoveryPriorStartup>.Empty
                : await ReadPriorStartupsAsync(
                    connection, transaction, schema, links.RootElement, registration.Binding, cancellationToken)
                    .ConfigureAwait(false);
            if (registration is null &&
                (links.RootElement.TryGetProperty("retiredBootstrap", out _) ||
                 links.RootElement.TryGetProperty("previousReplacementAttempt", out _)))
                throw InvalidBoundary();
            return (new ChildRecoveryStartup(
                binding, intent.IntentId, intent.BootstrapOperationId, intent.ConsumeOperationId,
                intent.ExchangeOperationId, intent.State, intent.CurrentStep, intent.EnvironmentId,
                intent.RuntimeInstanceId, intent.RowVersion, intent.CleanupObligation,
                ChildCheckpointRecoveryManifestCodec.Hash(links.RootElement), registration?.RuntimeInstanceId,
                registration?.Revision, currentRevision, registration?.State.ToString(), registration?.ExpiresAt,
                bindingHash, placement, bootstrapHash, bootstrap?.GrantId, bootstrap?.ConfigurationHash,
                priorStarts), registration);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict, exception);
        }
    }

    internal static T? ReadReceipt<T>(JsonElement links, string key) where T : class
    {
        if (!links.TryGetProperty(key, out var value))
            return null;
        _ = HashReceipt(value);
        return value.Deserialize<T>(JsonOptions) ?? throw InvalidBoundary();
    }

    internal static string HashReceipt(JsonElement receipt)
    {
        if (Encoding.UTF8.GetByteCount(receipt.GetRawText()) > ChildCheckpointRecoveryManifestCodec.MaximumReceiptBytes)
            throw InvalidBoundary();
        return ChildCheckpointRecoveryManifestCodec.Hash(receipt);
    }

    internal static void ValidateReceiptLinks(JsonElement links)
    {
        if (links.ValueKind != JsonValueKind.Object ||
            links.EnumerateObject().Any(property =>
                Encoding.UTF8.GetByteCount(property.Value.GetRawText()) >
                    ChildCheckpointRecoveryManifestCodec.MaximumReceiptBytes))
            throw InvalidBoundary();
    }

    internal static async Task<ImmutableArray<ChildRecoveryPriorStartup>> ReadPriorStartupsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        JsonElement links,
        RuntimeBinding current,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ChildRecoveryPriorStartup>();
        foreach (var key in new[] { "retiredBootstrap", "previousReplacementAttempt" })
        {
            if (!links.TryGetProperty(key, out var saved))
                continue;
            try
            {
                var savedHash = HashReceipt(saved);
                var registration = ReadReceipt<RuntimeRegistration>(saved, "registration")
                    ?? throw InvalidBoundary();
                var placement = ReadReceipt<RuntimeRunStartPlacementReceipt>(saved, "placement")
                    ?? throw InvalidBoundary();
                placement.Validate();
                var binding = registration.Binding;
                var bootstrap = saved.GetProperty("bootstrapOperationId").GetGuid();
                var consume = saved.GetProperty("consumeOperationId").GetGuid();
                var exchange = saved.GetProperty("exchangeOperationId").GetGuid();
                if (bootstrap == Guid.Empty || consume == Guid.Empty || exchange == Guid.Empty ||
                    binding.ActorIssuer != current.ActorIssuer || binding.ActorId != current.ActorId ||
                    binding.TenantId != current.TenantId || binding.ProjectId != current.ProjectId ||
                    binding.RunId != current.RunId || binding.SessionId != current.SessionId ||
                    !string.Equals(binding.AcceptedSelectionHash, current.AcceptedSelectionHash,
                        StringComparison.OrdinalIgnoreCase) ||
                    binding.EnvironmentId != placement.EnvironmentId ||
                    binding.ProfileId != placement.SourceProfileId ||
                    binding.PlacementUid != placement.SandboxResource.ResourceId ||
                    binding.PlacementGeneration != placement.SandboxResource.Generation ||
                    binding.PlacementProviderId != placement.SandboxResource.ProviderId)
                    throw InvalidBoundary();
                var (revision, bindingHash) = await ReadRegistrationAsync(
                    connection, transaction, schema, registration, cancellationToken).ConfigureAwait(false);
                if (revision is null || revision < registration.Revision || bindingHash is null)
                    throw InvalidBoundary();
                var retirementHash = saved.TryGetProperty("retirement", out var retirement)
                    ? HashReceipt(retirement) : null;
                result.Add(new ChildRecoveryPriorStartup(
                    key, bootstrap, consume, exchange, registration.RuntimeInstanceId, registration.Revision,
                    revision.Value, bindingHash, placement, retirementHash, savedHash));
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                               InvalidOperationException or FormatException or RuntimeAuthorizationException)
            {
                throw new CoordinationException(
                    "checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict, exception);
            }
        }
        return result.ToImmutable();
    }

    internal static async Task<(long?, string?)> ReadRegistrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        // Replacement may hold the registration head while rechecking the run.
        await using var command = new NpgsqlCommand($"""
            SELECT head.current_revision, revision.binding_json, revision.binding_hash, revision.state,
                   revision.expires_at
            FROM {schema}.runtime_registration_heads AS head
            JOIN {schema}.runtime_registration_revisions AS revision
              ON revision.runtime_instance_id = head.runtime_instance_id AND revision.revision = @revision
            WHERE head.runtime_instance_id = @runtime
            FOR SHARE OF head, revision NOWAIT
            """, connection, transaction);
        command.Parameters.AddWithValue("runtime", registration.RuntimeInstanceId);
        command.Parameters.AddWithValue("revision", registration.Revision);
        NpgsqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new CoordinationException(
                "checkpoint_recovery_registration_busy", StatusCodes.Status409Conflict, exception);
        }
        await using var ownedReader = reader;
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (null, null);
        using var persisted = JsonDocument.Parse(reader.GetString(1));
        if (ChildCheckpointRecoveryManifestCodec.Hash(persisted.RootElement) !=
            ChildCheckpointRecoveryManifestCodec.Hash(
                JsonSerializer.SerializeToElement(registration.Binding)) ||
            reader.GetInt32(3) != (int)registration.State ||
            reader.GetFieldValue<DateTimeOffset>(4) != registration.ExpiresAt)
            throw InvalidBoundary();
        return (reader.GetInt64(0), reader.GetString(2));
    }

    internal static async Task<ChildRecoveryNativeOrigin?> ReadNativeOriginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildRecoverySession child,
        RuntimeChildStartBinding binding,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT association_id, work_plan_id, root_session_id, dispatch_id, checkpoint_id,
                   checkpoint_revision, decision_state_version, execution_fence, accepted_selection_hash,
                   prompt_hash, request_hash, owner_revision, runtime_instance_id, registration_revision,
                   runtime_binding_hash, sdk_source_receipt_hash, model_selection_reference, selected_model_id,
                   jsonb_build_object('byok', accepted_byok_descriptor, 'cost', cost_binding_identity)::text,
                   is_cost_producing, hard_cap_serialized, dispatch_state, terminal_outcome,
                   source_report_status, source_report_hash, source_completion_manifest_hash,
                   acknowledgment_references::text, accounting_status, usage_witness_watermark,
                   usage_witness_digest, retired_through_event_position, created_at
            FROM {schema}.maf_execution_dispatches
            WHERE project_id = @project AND run_id = @run AND child_session_id = @child
            ORDER BY association_id, dispatch_id
            LIMIT 2
            FOR UPDATE
            """, connection, transaction);
        AddRun(command, root);
        command.Parameters.AddWithValue("child", child.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetString(0) != binding.AssociationId || reader.GetString(1) != binding.WorkPlanId ||
            reader.GetString(2) != root.SessionId || reader.GetGuid(3) != binding.DispatchMessageId ||
            !string.Equals(reader.GetString(8), selectionHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(9), binding.PromptHash, StringComparison.OrdinalIgnoreCase) ||
            reader.GetString(16) != binding.ModelSelectionReference)
            throw InvalidBoundary();
        using var cost = JsonDocument.Parse(reader.GetString(18));
        using var acknowledgments = JsonDocument.Parse(reader.GetString(26));
        var origin = new ChildRecoveryNativeOrigin(
            reader.GetGuid(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.GetInt64(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
            reader.GetInt64(11), reader.GetGuid(12), reader.GetInt64(13), reader.GetString(14),
            reader.GetString(15), reader.GetString(16), reader.GetString(17),
            ChildCheckpointRecoveryManifestCodec.Hash(cost.RootElement), reader.GetBoolean(19),
            reader.GetBoolean(20), reader.GetString(21), reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.GetString(23), reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            ChildCheckpointRecoveryManifestCodec.Hash(acknowledgments.RootElement), reader.GetString(27),
            reader.IsDBNull(28) ? null : reader.GetInt64(28),
            reader.IsDBNull(29) ? null : reader.GetString(29),
            reader.IsDBNull(30) ? null : reader.GetInt64(30),
            reader.GetFieldValue<DateTimeOffset>(31));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw InvalidBoundary();
        return origin;
    }

    private static async Task<MafExecutionDispatchIntent> ReadOriginIntentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildRecoveryNativeOrigin native,
        RuntimeChildStartBinding binding,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload, execution_fence, accepted_selection_hash
            FROM {schema}.maf_workflow_checkpoints
            WHERE project_id = @project AND run_id = @run AND session_id = @root
              AND store_name = 'coordinator-execution' AND checkpoint_id = @checkpoint
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        command.Parameters.AddWithValue("checkpoint", native.CheckpointId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(1) != native.ExecutionFence ||
            !string.Equals(reader.GetString(2), selectionHash, StringComparison.OrdinalIgnoreCase))
            throw InvalidBoundary();
        using var payload = JsonDocument.Parse(reader.GetString(0));
        var state = MafExecutionCheckpointContract.Deserialize(payload.RootElement);
        if (state.Revision != native.CheckpointRevision ||
            state.DecisionStateVersion != native.DecisionStateVersion ||
            state.WorkPlanId != binding.WorkPlanId ||
            !state.PendingDispatches.TryGetValue(binding.AssociationId, out var intent) ||
            intent.ChildSessionId != binding.ChildSessionId ||
            intent.MessageId != binding.DispatchMessageId || intent.PromptHash != binding.PromptHash)
            throw InvalidBoundary();
        return intent;
    }

    internal static async Task<(string?, string?)> ReadMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildRecoverySession child,
        RuntimeChildStartBinding binding,
        CoordinationActor actor,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT sender_session_id, recipient_session_id, writer_issuer, writer_subject,
                   status, canonical_request
            FROM {schema}.coordination_messages
            WHERE project_id = @project AND run_id = @run AND message_id = @message
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        command.Parameters.AddWithValue("message", binding.DispatchMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (null, null);
        if (reader.GetString(0) != root.SessionId || reader.GetString(1) != child.SessionId ||
            reader.GetString(2) != actor.Issuer || reader.GetString(3) != actor.Subject)
            throw InvalidBoundary();
        using var request = JsonDocument.Parse(reader.GetString(5));
        return (reader.GetString(4), ChildCheckpointRecoveryManifestCodec.Hash(request.RootElement));
    }

    private static async Task RequireNoExtraNativeOriginsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        int observedCount,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT count(*)
            FROM {schema}.maf_execution_dispatches
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction);
        AddRun(command, root);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != observedCount)
            throw InvalidBoundary();
    }

    private static async Task<ImmutableArray<ChildRecoveryRepositoryPin>> ReadRepositoryPinsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        string selectionHash,
        ChildCheckpointRecoveryManifestCodec.ByteBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT pin_id, accepted_selection_hash, execution_fence, pin
            FROM {schema}.source_control_repository_pins
            WHERE project_id = @project AND run_id = @run
            ORDER BY pin_id
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        var pins = ImmutableArray.CreateBuilder<ChildRecoveryRepositoryPin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(reader.GetString(1), selectionHash, StringComparison.OrdinalIgnoreCase))
                throw InvalidBoundary();
            using var pin = JsonDocument.Parse(reader.GetString(3));
            var item = new ChildRecoveryRepositoryPin(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                ChildCheckpointRecoveryManifestCodec.Hash(pin.RootElement));
            budget.Include(item);
            pins.Add(item);
        }
        return pins.ToImmutable();
    }

    private static async Task<ImmutableArray<ChildRecoveryCapture>> ReadCapturesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ImmutableArray<ChildRecoverySession> children,
        ChildCheckpointRecoveryManifestCodec.ByteBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT session_id, capture_id, event_id, pin_id, capture_state, workspace_id,
                   workspace_incarnation_id, repository_id, resource_generation, base_sha, output_tree_sha,
                   manifest_sha256, patch_sha256, package_sha256, object_key, event_position
            FROM {schema}.source_control_output_captures
            WHERE project_id = @project AND run_id = @run
            ORDER BY session_id, capture_id
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        var sessionIds = children.Select(child => child.SessionId).ToHashSet(StringComparer.Ordinal);
        sessionIds.Add(root.SessionId);
        var captures = ImmutableArray.CreateBuilder<ChildRecoveryCapture>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!sessionIds.Contains(reader.GetString(0)))
                throw InvalidBoundary();
            var item = new ChildRecoveryCapture(
                reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetGuid(6), reader.GetString(7),
                reader.GetInt64(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15));
            budget.Include(item);
            captures.Add(item);
        }
        return captures.ToImmutable();
    }

    private static async Task<ChildRecoveryClaim?> ReadClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        string selectionHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT task_id, claim_id, task_revision, claim_task_revision, claim_phase, claim_request_hash,
                   claim_execution_fence, claim_decision_state_version, title, description,
                   claim_root_session_id, claim_selection_hash
            FROM {schema}.backlog_tasks
            WHERE project_id = @project AND claim_run_id = @run AND claim_id IS NOT NULL
            ORDER BY task_id
            LIMIT 2
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt64(2) != reader.GetInt64(3) || reader.GetString(10) != root.SessionId ||
            !string.Equals(reader.GetString(11), selectionHash, StringComparison.OrdinalIgnoreCase))
            throw InvalidBoundary();
        var claim = new ChildRecoveryClaim(
            reader.GetString(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7),
            ChildCheckpointRecoveryManifestCodec.Hash(new
            {
                title = reader.GetString(8),
                description = reader.IsDBNull(9) ? null : reader.GetString(9)
            }));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw InvalidBoundary();
        return claim;
    }

    private static async Task<ImmutableArray<ChildRecoveryReservation>> ReadReservationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildCheckpointRecoveryManifestCodec.ByteBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_id, bucket_key, budget_id, budget_revision, reservation_state,
                   reserve_amount, retired_cursor, to_jsonb(reservation)::text
            FROM {schema}.spend_budget_reservations AS reservation
            WHERE project_id = @project AND run_id = @run
            ORDER BY dispatch_id, bucket_key
            FOR SHARE
            """, connection, transaction);
        AddRun(command, root);
        var reservations = ImmutableArray.CreateBuilder<ChildRecoveryReservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var record = JsonDocument.Parse(reader.GetString(7));
            var item = new ChildRecoveryReservation(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetString(4), reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                ChildCheckpointRecoveryManifestCodec.Hash(record.RootElement));
            budget.Include(item);
            reservations.Add(item);
        }
        return reservations.ToImmutable();
    }

    private static async Task<ImmutableArray<ChildRecoveryOtherNativeWork>> ReadOtherNativeWorkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        SessionIdentity root,
        ChildCheckpointRecoveryManifestCodec.ByteBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT kind, id, state, evidence FROM (
                SELECT 'rootModelTurn' AS kind, admission.request_id::text AS id, 'admitted' AS state,
                       to_jsonb(admission)::text AS evidence
                FROM {schema}.maf_root_model_turn_admissions AS admission
                WHERE admission.project_id = @project AND admission.run_id = @run
                  AND NOT EXISTS (
                      SELECT 1 FROM {schema}.maf_runtime_evidence_receipts AS receipt
                      WHERE receipt.project_id = admission.project_id AND receipt.run_id = admission.run_id
                        AND receipt.attempt_id = admission.request_id
                        AND receipt.receipt_kind IN ('native-root-turn-completion', 'numeric-limit-native-no-send'))
                  AND NOT EXISTS (
                      SELECT 1 FROM {schema}.workflow_generation_jobs AS job
                      WHERE job.project_id = admission.project_id AND job.run_id = admission.run_id
                        AND job.native_request_id = admission.request_id AND job.execution_phase = 'quiesced')
                UNION ALL
                SELECT 'workflowGeneration', job.job_id::text, job.status, to_jsonb(job)::text
                FROM {schema}.workflow_generation_jobs AS job
                WHERE job.project_id = @project AND job.run_id = @run
                  AND job.execution_phase = 'send-admitted'
                  AND job.status IN ('running', 'cancelled', 'failed')
                UNION ALL
                SELECT 'sourceControlPullRequest', effect.operation_id::text, 'pending', to_jsonb(effect)::text
                FROM {schema}.source_control_pull_request_operations AS effect
                WHERE effect.project_id = @project AND effect.run_id = @run AND effect.completed_at IS NULL
                UNION ALL
                SELECT 'sourceControlMerge', effect.intent_id, effect.intent_state, to_jsonb(effect)::text
                FROM {schema}.source_control_merge_intents AS effect
                WHERE effect.project_id = @project AND effect.run_id = @run
                  AND effect.intent_state IN ('merge_started', 'outcome_uncertain')
                UNION ALL
                SELECT 'toolInvocationAdmission', event.id::text, 'admitted', event.payload::text
                FROM {schema}.outbox_events AS event
                WHERE event.event_type = 'orchestrator.runtime.tool_invocation_admitted'
                  AND event.payload ->> 'projectId' = @project
                  AND event.payload ->> 'runId' = @run
            ) AS unresolved
            ORDER BY kind, id
            """, connection, transaction);
        AddRun(command, root);
        var work = ImmutableArray.CreateBuilder<ChildRecoveryOtherNativeWork>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var evidence = JsonDocument.Parse(reader.GetString(3));
            var item = new ChildRecoveryOtherNativeWork(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                ChildCheckpointRecoveryManifestCodec.Hash(evidence.RootElement));
            budget.Include(item);
            work.Add(item);
        }
        return work.ToImmutable();
    }

    private static CoordinationException InvalidBoundary() =>
        new("checkpoint_recovery_boundary_unsupported", StatusCodes.Status409Conflict);

    private static void AddRun(NpgsqlCommand command, SessionIdentity root)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, root.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, root.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
    }
}
