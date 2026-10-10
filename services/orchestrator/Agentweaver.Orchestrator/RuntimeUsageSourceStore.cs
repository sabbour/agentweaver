using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeUsageSourceStore(
    NpgsqlDataSource dataSource, OrchestratorOptions options, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _sources = $"\"{options.Schema}\".runtime_sdk_sources";
    private readonly string _observations = $"\"{options.Schema}\".runtime_usage_observations";
    private readonly string _dispatches = $"\"{options.Schema}\".maf_execution_dispatches";
    private readonly string _checkpoints = $"\"{options.Schema}\".maf_workflow_checkpoints";
    private readonly string _sessions = $"\"{options.Schema}\".coordination_sessions";
    private readonly string _decisions = $"\"{options.Schema}\".coordinator_decisions";
    private readonly string _guards = $"\"{options.Schema}\".maf_execution_run_guards";
    private readonly PostgresOutbox _outbox = new(dataSource, options.Schema);

    internal async Task<T> ExecuteLockedAsync<T>(
        Guid runtimeInstanceId, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.runtime.usage'), hashtext(@runtime))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("runtime", NpgsqlDbType.Text, runtimeInstanceId.ToString("D"));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return await action(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeSdkSourceReceipt> RegisterWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, SdkSessionFacts facts, CancellationToken cancellationToken)
    {
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, facts);
        var hash = RuntimeUsageSourceReceiptContract.HashSource(registration, facts);
        var existing = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSource(existing, registration, grant);
            if (existing.Receipt.Source != facts || existing.Receipt.CanonicalPayloadHash != hash)
                throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
            return existing.Receipt;
        }
        var receipt = new RuntimeSdkSourceReceipt(registration.RuntimeInstanceId, registration.Revision,
            grant.GrantId, facts, hash, RecordedAt());
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_sources}
                (runtime_instance_id, registration_revision, source_grant_id, configuration_hash,
                 canonical_payload_hash, receipt_json, recorded_at)
            VALUES (@runtime, @revision, @grant, @configuration, @hash, @receipt, @recorded)
            """, connection, transaction);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        insert.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, registration.Revision);
        insert.Parameters.AddWithValue("grant", NpgsqlDbType.Uuid, grant.GrantId);
        insert.Parameters.AddWithValue("configuration", NpgsqlDbType.Char, grant.ConfigurationHash);
        AddReceipt(insert, hash, receipt, receipt.RecordedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<RuntimeUsageSourceReceipt> AppendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, SdkUsageObservation observation, CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source.Receipt.Source, observation);
        var hash = RuntimeUsageSourceReceiptContract.Hash(registration, usage);
        await using (var read = new NpgsqlCommand(
            $"SELECT receipt_json FROM {_observations} WHERE event_id = @event", connection, transaction))
        {
            read.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
            var stored = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (stored is string json)
            {
                var previous = Deserialize<RuntimeUsageSourceReceipt>(json);
                RuntimeUsageSourceReceiptContract.Validate(previous);
                if (previous.CanonicalPayloadHash != hash)
                    throw new RuntimeAuthorizationException("runtime_usage_event_conflict");
                return previous;
            }
        }
        var receipt = new RuntimeUsageSourceReceipt(1, Guid.NewGuid(), registration, usage, hash, RecordedAt());
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_observations}
                (receipt_id, runtime_instance_id, event_id, sdk_event_id, tenant_id, project_id,
                 run_id, session_id, canonical_payload_hash, receipt_json, recorded_at)
            VALUES (@id, @runtime, @event, @native, @tenant, @project, @run, @session, @hash, @receipt, @recorded)
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, receipt.ReceiptId);
        insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
        insert.Parameters.AddWithValue("native", NpgsqlDbType.Uuid, Guid.ParseExact(usage.SdkEventId!, "D"));
        insert.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, registration.Binding.TenantId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        insert.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, registration.Binding.SessionId);
        AddReceipt(insert, hash, receipt, receipt.RecordedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    internal async Task<RuntimeUsageSourceReceipt?> ReadReceiptAsync(
        Guid receiptId, string projectId, string runId, string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json FROM {_observations}
            WHERE receipt_id = @id AND project_id = @project AND run_id = @run AND session_id = @session
            """, connection);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, receiptId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is not string stored)
            return null;
        var receipt = Deserialize<RuntimeUsageSourceReceipt>(stored);
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        return receipt;
    }

    internal async Task<RuntimeNativeTurnAdmissionReceipt?> BeginNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnBeginRequest request,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> readCostSnapshot,
        CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        if (source.Receipt.Source != request.Source || request.Message?.Message is not { } message ||
            message.Metadata?.Runtime?.Registration != registration || message.ContextId != request.Source.SdkSessionId ||
            message.Metadata.Runtime.SourceGrantId != grant.GrantId ||
            message.Metadata.Runtime.SourceGrantRevision != grant.Revision ||
            message.MessageId == Guid.Empty || message.Parts.IsDefault || message.Parts.Length != 1 ||
            message.Parts[0] is not { Kind: "text", Text: { Length: > 0 } } part ||
            part.Text.Length > AddressedMessageValidation.MaximumTextLength)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_invalid");
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, message.MessageId, cancellationToken).ConfigureAwait(false);
        var admission = new RuntimeNativeTurnAdmissionReceipt(registration, request.Source, message.MessageId,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(part.Text)),
            RuntimeNativeTurnContract.RequestHash(request.Message), checked(dispatch.OwnerRevision + 1));
        RuntimeNativeTurnContract.ValidateAdmission(admission, registration, request.Source, request.Message);
        RequireNativeDispatch(dispatch, admission, source.Receipt.CanonicalPayloadHash);
        if (dispatch.State != "prepared")
            throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
        await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, dispatch, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        if (RequiresCopilotCreditSnapshot(binding))
        {
            if (binding.CopilotHardCreditLimit is not null)
            {
                await using var open = new NpgsqlCommand($"""
                    SELECT 1 FROM {_dispatches}
                    WHERE project_id = @project AND run_id = @run AND root_session_id = @root
                        AND dispatch_id <> @message
                        AND dispatch_state IN ('sending', 'terminal_pending_accounting', 'indeterminate')
                    LIMIT 1
                    """, connection, transaction);
                AddNativeScope(open, registration, message.MessageId);
                open.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, dispatch.RootSessionId);
                if (await open.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new RuntimeAuthorizationException("runtime_native_accounting_pending");
            }
            var snapshot = await readCostSnapshot(source.Receipt.Source, cancellationToken).ConfigureAwait(false);
            if (!await ReconcileNativeCreditLimitsAsync(
                connection, transaction, registration, source.Receipt.Source, dispatch.RootSessionId,
                snapshot, cancellationToken).ConfigureAwait(false))
                return null;
        }
        await using var update = new NpgsqlCommand($"""
            UPDATE {_dispatches}
            SET dispatch_state = 'sending', owner_revision = owner_revision + 1, updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
                AND owner_revision = @revision AND dispatch_state = 'prepared'
            """, connection, transaction);
        AddNativeScope(update, registration, message.MessageId);
        update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, dispatch.OwnerRevision);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
        return admission;
    }

    internal static void ValidateNativePreparation(
        SessionIdentity root, MafExecutionCheckpointSnapshot checkpoint, MafExecutionDispatchIntent intent,
        RuntimeRegistration registration, SdkSessionFacts source, RuntimeA2ASendRequest message)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(message);
        var binding = registration.Binding;
        var state = checkpoint.State;
        RuntimeNativeTurnContract.ValidateAdmission(new(
            registration, source, intent.MessageId, intent.PromptHash,
            RuntimeNativeTurnContract.RequestHash(message), 2), registration, source, message);
        MafExecutionCheckpointContract.ValidateState(state);
        if (binding.ProjectId != root.ProjectId || binding.RunId != root.RunId ||
            root.SessionId == binding.SessionId || checkpoint.Info.SessionId != root.SessionId ||
            string.IsNullOrWhiteSpace(checkpoint.Info.CheckpointId) || binding.WorkflowStepId is null ||
            binding.SessionId != intent.ChildSessionId || state.Results.ContainsKey(intent.AssociationId) ||
            !state.PendingDispatches.TryGetValue(intent.AssociationId, out var pending) || pending != intent ||
            !IsNativeDispatchRunning(state, intent.AssociationId))
            throw new RuntimeAuthorizationException("runtime_native_turn_preparation_invalid");
    }

    internal static (bool SoftReached, bool HardReached) EvaluateObservedCreditLimits(
        RuntimeBinding binding, decimal observedCredits)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (observedCredits < 0 || binding.CopilotSoftCreditLimit < 0 ||
            binding.CopilotHardCreditLimit < 0 ||
            binding.CopilotSoftCreditLimit is { } soft && binding.CopilotHardCreditLimit is { } hard && soft > hard)
            throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_invalid");
        return (binding.CopilotSoftCreditLimit is { } softLimit && observedCredits >= softLimit,
            binding.CopilotHardCreditLimit is { } hardLimit && observedCredits >= hardLimit);
    }

    internal static bool RequiresCopilotCreditSnapshot(RuntimeBinding binding) =>
        binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
        (binding.CopilotSoftCreditLimit is not null || binding.CopilotHardCreditLimit is not null);

    private async Task<bool> ReconcileNativeCreditLimitsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        SdkSessionFacts source, string rootSessionId, RuntimeUsageCostSnapshotReceipt snapshot,
        CancellationToken cancellationToken)
    {
        var observed = RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, new(1, registration, source));
        var binding = registration.Binding;
        var limits = EvaluateObservedCreditLimits(binding, observed);
        var card = snapshot.Binding!.RateCard;
        var costIdentity = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            snapshot.Binding.MeterSource, snapshot.Binding.ProviderId, snapshot.Binding.AdapterVersion,
            snapshot.Binding.OptionsSchemaVersion, snapshot.Binding.OptionsRevision,
            snapshot.Binding.ResourceId, snapshot.Binding.ResourceGeneration,
            capabilities = snapshot.Binding.NegotiatedCapabilities.Order(StringComparer.Ordinal),
            rateCard = new
            {
                card.Id, card.Version, card.MeterSource, card.Unit, card.NanoUnitsPerUnit,
                models = card.ModelMultipliers.OrderBy(model => model.Key, StringComparer.Ordinal)
            }
        }));
        var digest = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot.CopilotTotals));
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_guards}
                (project_id, run_id, root_session_id, tenant_id, accepted_selection_hash, execution_fence,
                 copilot_soft_credit_limit, copilot_hard_credit_limit, cost_rate_scope_identity)
            VALUES (@project, @run, @root, @tenant, @selection, @fence, @soft, @hard, @cost)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddNativeScope(insert, registration, Guid.Empty);
            AddCreditGuardScope(insert, binding, rootSessionId, costIdentity);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long revision;
        bool warningEmitted;
        bool hardExhausted;
        await using (var read = new NpgsqlCommand($"""
            SELECT root_session_id, tenant_id, accepted_selection_hash, execution_fence,
                copilot_soft_credit_limit, copilot_hard_credit_limit, cost_rate_scope_identity,
                copilot_aic_floor, accounted_through_event_position, usage_witness_digest,
                owner_revision, soft_warning_emitted, hard_cap_exhausted
            FROM {_guards} WHERE project_id = @project AND run_id = @run FOR UPDATE
            """, connection, transaction))
        {
            AddNativeScope(read, registration, Guid.Empty);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != rootSessionId || reader.GetString(1) != binding.TenantId ||
                reader.GetString(2) != binding.AcceptedSelectionHash || reader.GetInt64(3) != binding.ExecutionFence ||
                (reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4)) != binding.CopilotSoftCreditLimit ||
                (reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5)) != binding.CopilotHardCreditLimit ||
                reader.GetString(6) != costIdentity ||
                !reader.IsDBNull(7) && reader.GetDecimal(7) > observed ||
                !reader.IsDBNull(8) && (reader.GetInt64(8) > snapshot.CopilotTotals.Events ||
                    reader.GetInt64(8) == snapshot.CopilotTotals.Events && reader.GetString(9) != digest))
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_stale");
            revision = reader.GetInt64(10);
            warningEmitted = reader.GetBoolean(11);
            hardExhausted = reader.GetBoolean(12);
        }
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_guards}
            SET copilot_aic_floor = @floor, accounted_through_event_position = @position,
                usage_witness_digest = @digest, soft_warning_emitted = @warning,
                hard_cap_exhausted = @exhausted, owner_revision = owner_revision + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND owner_revision = @revision
            """, connection, transaction))
        {
            AddNativeScope(update, registration, Guid.Empty);
            update.Parameters.AddWithValue("floor", NpgsqlDbType.Numeric, observed);
            update.Parameters.AddWithValue("position", NpgsqlDbType.Bigint, snapshot.CopilotTotals.Events);
            update.Parameters.AddWithValue("digest", NpgsqlDbType.Char, digest);
            update.Parameters.AddWithValue("warning", NpgsqlDbType.Boolean, warningEmitted || limits.SoftReached);
            update.Parameters.AddWithValue("exhausted", NpgsqlDbType.Boolean, hardExhausted || limits.HardReached);
            update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, revision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_stale");
        }
        if (!warningEmitted && limits.SoftReached)
        {
            var notificationId = Guid.NewGuid();
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                notificationId, $"coordination/{binding.ProjectId}/{binding.RunId}/{rootSessionId}",
                "copilot-soft-credit-limit", "orchestrator.run.copilot_soft_credit_limit_reached", 1,
                JsonSerializer.SerializeToElement(new
                {
                    notificationId, binding.ProjectId, binding.RunId, rootSessionId,
                    binding.ExecutionFence, binding.CopilotSoftCreditLimit, observedCredits = observed,
                    accountingPosition = snapshot.CopilotTotals.Events, costIdentity
                }), timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        return !hardExhausted && !limits.HardReached;
    }

    private static void AddCreditGuardScope(
        NpgsqlCommand command, RuntimeBinding binding, string rootSessionId, string costIdentity)
    {
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue("soft", NpgsqlDbType.Numeric, (object?)binding.CopilotSoftCreditLimit ?? DBNull.Value);
        command.Parameters.AddWithValue("hard", NpgsqlDbType.Numeric, (object?)binding.CopilotHardCreditLimit ?? DBNull.Value);
        command.Parameters.AddWithValue("cost", NpgsqlDbType.Varchar, costIdentity);
    }

    internal async Task<bool> PrepareNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionIdentity root,
        MafExecutionCheckpointSnapshot checkpoint, MafExecutionDispatchIntent intent,
        RuntimeRegistration registration, RuntimeA2ASendRequest message,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> readCostSnapshot,
        CancellationToken cancellationToken)
    {
        var source = await ReadCurrentSourceAsync(connection, transaction, registration, cancellationToken)
            .ConfigureAwait(false);
        ValidateNativePreparation(root, checkpoint, intent, registration, source.Source, message);
        if (source.SourceGrantId != message.Message.Metadata.Runtime.SourceGrantId)
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
        await using (var owner = new NpgsqlCommand($"""
            SELECT session_id FROM {_sessions}
            WHERE project_id = @project AND run_id = @run AND session_id = @root
                AND lifecycle_state = 'active' AND execution_fence = @fence
            FOR UPDATE
            """, connection, transaction))
        {
            AddNativeScope(owner, registration, intent.MessageId);
            owner.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, root.SessionId);
            owner.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
            if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string)
                throw new RuntimeAuthorizationException("runtime_native_turn_preparation_invalid");
        }
        await AcquireNativeExecutionLockAsync(connection, transaction, root, cancellationToken).ConfigureAwait(false);
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Source, intent.MessageId, intent.PromptHash,
            RuntimeNativeTurnContract.RequestHash(message), 2);
        var prepared = new NativeDispatch(root.SessionId, intent.ChildSessionId, registration.RuntimeInstanceId,
            registration.Revision, registration.Binding.ExecutionFence,
            RuntimeContractValidation.RegistrationHash(registration), source.CanonicalPayloadHash,
            source.Source.ModelSelectionReference, source.Source.ModelId, intent.PromptHash, admission.RequestHash,
            1, "prepared", checkpoint.State.WorkPlanId, intent.AssociationId, checkpoint.Info.CheckpointId,
            checkpoint.State.Revision, checkpoint.State.DecisionStateVersion, null, null);
        await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, prepared, cancellationToken).ConfigureAwait(false);
        if (registration.Binding.MaxModelTurns is { } maxModelTurns)
        {
            await using var count = new NpgsqlCommand($"""
                SELECT count(*) FROM {_dispatches}
                WHERE project_id = @project AND run_id = @run AND is_cost_producing
                    AND dispatch_id <> @message
                """, connection, transaction);
            AddNativeScope(count, registration, intent.MessageId);
            RuntimeActionContract.RequireInvocationCapacity(maxModelTurns,
                (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!,
                "runtime_model_turn_limit_exhausted");
        }
        if (RequiresCopilotCreditSnapshot(registration.Binding))
        {
            var snapshot = await readCostSnapshot(source.Source, cancellationToken).ConfigureAwait(false);
            if (!await ReconcileNativeCreditLimitsAsync(
                connection, transaction, registration, source.Source, root.SessionId, snapshot, cancellationToken)
                .ConfigureAwait(false))
                return false;
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_dispatches}
                (project_id, run_id, dispatch_id, root_session_id, child_session_id, work_plan_id,
                 association_id, checkpoint_store_name, checkpoint_id, checkpoint_revision, decision_state_version,
                 execution_fence, accepted_selection_hash, prompt_hash, request_hash,
                 runtime_instance_id, registration_revision, runtime_binding_hash, sdk_source_receipt_hash,
                 model_selection_reference, selected_model_id, is_cost_producing, hard_cap_serialized, dispatch_state)
            VALUES (@project, @run, @message, @root, @child, @plan, @association, 'coordinator-execution',
                @checkpoint, @checkpoint_revision, @decision, @fence, @selection, @prompt, @request,
                @runtime, @registration_revision, @binding, @source, @model_reference, @model, true, false, 'prepared')
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddNativeScope(insert, registration, intent.MessageId);
            insert.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, prepared.RootSessionId);
            insert.Parameters.AddWithValue("child", NpgsqlDbType.Varchar, prepared.ChildSessionId);
            insert.Parameters.AddWithValue("plan", NpgsqlDbType.Varchar, prepared.WorkPlanId);
            insert.Parameters.AddWithValue("association", NpgsqlDbType.Varchar, prepared.AssociationId);
            insert.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, prepared.CheckpointId);
            insert.Parameters.AddWithValue("checkpoint_revision", NpgsqlDbType.Bigint, prepared.CheckpointRevision);
            insert.Parameters.AddWithValue("decision", NpgsqlDbType.Bigint, prepared.DecisionStateVersion);
            insert.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, prepared.ExecutionFence);
            insert.Parameters.AddWithValue("selection", NpgsqlDbType.Char, registration.Binding.AcceptedSelectionHash);
            insert.Parameters.AddWithValue("prompt", NpgsqlDbType.Char, prepared.PromptHash);
            insert.Parameters.AddWithValue("request", NpgsqlDbType.Char, prepared.RequestHash);
            insert.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, prepared.RuntimeInstanceId);
            insert.Parameters.AddWithValue("registration_revision", NpgsqlDbType.Bigint, prepared.RegistrationRevision);
            insert.Parameters.AddWithValue("binding", NpgsqlDbType.Char, prepared.BindingHash);
            insert.Parameters.AddWithValue("source", NpgsqlDbType.Char, prepared.SourceHash);
            insert.Parameters.AddWithValue("model_reference", NpgsqlDbType.Varchar, prepared.ModelSelectionReference);
            insert.Parameters.AddWithValue("model", NpgsqlDbType.Varchar, prepared.ModelId);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var retained = await ReadNativeDispatchAsync(
            connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
        RequireNativeDispatch(retained, admission, source.CanonicalPayloadHash);
        if (retained.RootSessionId != prepared.RootSessionId || retained.WorkPlanId != prepared.WorkPlanId ||
            retained.AssociationId != prepared.AssociationId || retained.CheckpointId != prepared.CheckpointId ||
            retained.CheckpointRevision != prepared.CheckpointRevision ||
            retained.DecisionStateVersion != prepared.DecisionStateVersion)
            throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
        if (retained.State != "prepared" || retained.OwnerRevision != 1)
            throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
        return true;
    }

    internal async Task<RuntimeNativeTurnRecordedReceipt> RecordNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnObservationRequest request, CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        if (request.Admission.Registration != registration || request.Admission.Source != source.Receipt.Source)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_invalid");
        RuntimeNativeTurnContract.ValidateObservation(request.Admission, request.Observation);
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, request.Admission.MessageId, cancellationToken).ConfigureAwait(false);
        RequireNativeDispatch(dispatch, request.Admission, source.Receipt.CanonicalPayloadHash);
        var hash = RuntimeNativeTurnContract.ObservationHash(request.Observation);
        if (dispatch.ReportHash is not null)
        {
            if (dispatch.ReportHash != hash || dispatch.ReportJson is null ||
                RuntimeNativeTurnContract.ObservationHash(
                    Deserialize<RuntimeNativeTurnObservation>(dispatch.ReportJson)) != hash)
                throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
            var replay = new RuntimeNativeTurnRecordedReceipt(
                request.Admission, request.Observation, hash,
                dispatch.State == "completed" ? dispatch.OwnerRevision - 1 : dispatch.OwnerRevision);
            RuntimeNativeTurnContract.ValidateRecorded(replay);
            return replay;
        }
        if (dispatch.State is not ("sending" or "indeterminate") ||
            dispatch.OwnerRevision < request.Admission.OwnerRevision ||
            request.Observation.CompletedAt > timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_native_turn_observation_invalid");
        var receipt = new RuntimeNativeTurnRecordedReceipt(
            request.Admission, request.Observation, hash, checked(dispatch.OwnerRevision + 1));
        RuntimeNativeTurnContract.ValidateRecorded(receipt);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_dispatches}
            SET dispatch_state = 'terminal_pending_accounting', terminal_outcome = 'native-completed',
                source_report_status = 'partial', source_report_hash = @hash, source_report_json = @report,
                owner_revision = owner_revision + 1, updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message AND owner_revision = @revision
            """, connection, transaction);
        AddNativeScope(update, registration, request.Admission.MessageId);
        update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, dispatch.OwnerRevision);
        update.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash);
        update.Parameters.AddWithValue("report", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(request.Observation, JsonOptions));
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
        return receipt;
    }

    private async Task<NativeDispatch> ReadNativeDispatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        Guid messageId, CancellationToken cancellationToken)
    {
        await using (var root = new NpgsqlCommand($"""
            SELECT owner.session_id
            FROM {_sessions} owner
            JOIN {_dispatches} dispatch
                ON owner.project_id = dispatch.project_id AND owner.run_id = dispatch.run_id
                    AND owner.session_id = dispatch.root_session_id
            WHERE dispatch.project_id = @project AND dispatch.run_id = @run AND dispatch.dispatch_id = @message
                AND owner.lifecycle_state = 'active' AND owner.execution_fence = @fence
            FOR UPDATE OF owner
            """, connection, transaction))
        {
            AddNativeScope(root, registration, messageId);
            root.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
            if (await root.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string rootId)
                throw new RuntimeAuthorizationException("runtime_native_turn_admission_unavailable");
            await AcquireNativeExecutionLockAsync(connection, transaction,
                new(registration.Binding.ProjectId, registration.Binding.RunId, rootId), cancellationToken)
                .ConfigureAwait(false);
        }
        await using var command = new NpgsqlCommand($"""
            SELECT root_session_id, child_session_id, runtime_instance_id, registration_revision,
                execution_fence, runtime_binding_hash, sdk_source_receipt_hash, model_selection_reference,
                selected_model_id, prompt_hash, request_hash, owner_revision, dispatch_state,
                work_plan_id, association_id, checkpoint_id, checkpoint_revision, decision_state_version,
                source_report_hash, source_report_json::text
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
            FOR UPDATE
            """, connection, transaction);
        AddNativeScope(command, registration, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_unavailable");
        return new(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt64(11),
            reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
            reader.GetInt64(16), reader.GetInt64(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19));
    }

    internal async Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnAccountingRequest request,
        Func<CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> readSnapshot,
        CancellationToken cancellationToken)
    {
        var snapshotRequest = RuntimeNativeTurnContract.AccountingSnapshotRequest(
            request.Recorded, request.RequiredReceipts);
        var admission = request.Recorded.Admission;
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        if (admission.Registration != registration || admission.Source != source.Receipt.Source)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, admission.MessageId, cancellationToken).ConfigureAwait(false);
        RequireNativeDispatch(dispatch, admission, source.Receipt.CanonicalPayloadHash);
        if (dispatch.State is not ("terminal_pending_accounting" or "completed") ||
            dispatch.ReportHash != request.Recorded.CanonicalPayloadHash || dispatch.ReportJson is null ||
            RuntimeNativeTurnContract.ObservationHash(
                Deserialize<RuntimeNativeTurnObservation>(dispatch.ReportJson)) != dispatch.ReportHash ||
            request.Recorded.OwnerRevision !=
                (dispatch.State == "completed" ? dispatch.OwnerRevision - 1 : dispatch.OwnerRevision))
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, dispatch, cancellationToken).ConfigureAwait(false);
        var snapshot = await readSnapshot(cancellationToken).ConfigureAwait(false);
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, snapshotRequest);
        var references = snapshot.RepresentedReceipts.OrderBy(reference => reference.SourceReceiptId)
            .ToImmutableArray();
        var referencesJson = JsonSerializer.Serialize(references, JsonOptions);
        await using (var read = new NpgsqlCommand($"""
            SELECT acknowledgment_references::text FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
            """, connection, transaction))
        {
            AddNativeScope(read, registration, admission.MessageId);
            if (await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string storedJson)
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
            var stored = Deserialize<ImmutableArray<RuntimeUsageCostReceiptReference>>(storedJson);
            if (dispatch.State == "completed")
            {
                if (!stored.SequenceEqual(references))
                    throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
                var replay = new RuntimeNativeTurnAccountingReceipt(request.Recorded, snapshot, dispatch.OwnerRevision);
                RuntimeNativeTurnContract.ValidateAccounted(replay, request.Recorded, references);
                return replay;
            }
        }
        var accounted = new RuntimeNativeTurnAccountingReceipt(
            request.Recorded, snapshot, checked(dispatch.OwnerRevision + 1));
        RuntimeNativeTurnContract.ValidateAccounted(accounted, request.Recorded, references);
        await using var update = new NpgsqlCommand($"""
            UPDATE {_dispatches}
            SET dispatch_state = 'completed', acknowledgment_references = @references,
                accounting_status = @status, owner_revision = owner_revision + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
                AND owner_revision = @revision AND dispatch_state = 'terminal_pending_accounting'
            """, connection, transaction);
        AddNativeScope(update, registration, admission.MessageId);
        update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, dispatch.OwnerRevision);
        update.Parameters.AddWithValue("references", NpgsqlDbType.Jsonb, referencesJson);
        update.Parameters.AddWithValue("status", NpgsqlDbType.Varchar,
            snapshot.Quote.Disposition == CostDisposition.Unpriced ||
            references.Any(reference => reference.Accounting.Disposition == CostDisposition.Unpriced)
                ? "unpriced" : "priced");
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
        return accounted;
    }

    internal async Task RequireNativeTurnAccountedWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeA2ASendRequest message, string answer, CancellationToken cancellationToken)
    {
        var source = await ReadCurrentSourceAsync(connection, transaction, registration, cancellationToken)
            .ConfigureAwait(false);
        var admission = new RuntimeNativeTurnAdmissionReceipt(registration, source.Source, message.Message.MessageId,
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(message.Message.Parts[0].Text)),
            RuntimeNativeTurnContract.RequestHash(message), 2);
        RuntimeNativeTurnContract.ValidateAdmission(admission, registration, source.Source, message);
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, admission.MessageId, cancellationToken).ConfigureAwait(false);
        RequireNativeDispatch(dispatch, admission, source.CanonicalPayloadHash);
        if (dispatch.State != "completed" || dispatch.ReportJson is null || dispatch.ReportHash is null)
            throw new RuntimeAuthorizationException("runtime_native_accounting_pending");
        var observation = Deserialize<RuntimeNativeTurnObservation>(dispatch.ReportJson);
        RuntimeNativeTurnContract.ValidateObservation(admission, observation);
        if (dispatch.ReportHash != RuntimeNativeTurnContract.ObservationHash(observation) ||
            observation.OutputHash != RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(answer)))
            throw new RuntimeAuthorizationException("runtime_native_turn_output_mismatch");
        await using var read = new NpgsqlCommand($"""
            SELECT acknowledgment_references::text, accounting_status
            FROM {_dispatches} WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
            """, connection, transaction);
        AddNativeScope(read, registration, admission.MessageId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(1) is not ("priced" or "unpriced"))
            throw new RuntimeAuthorizationException("runtime_native_accounting_pending");
        var references = Deserialize<ImmutableArray<RuntimeUsageCostReceiptReference>>(reader.GetString(0));
        RuntimeNativeTurnContract.AccountingSnapshotRequest(
            new(admission, observation, dispatch.ReportHash, dispatch.OwnerRevision - 1), references);
    }

    private static void RequireNativeDispatch(
        NativeDispatch dispatch, RuntimeNativeTurnAdmissionReceipt admission, string sourceHash)
    {
        var registration = admission.Registration;
        if (dispatch.ChildSessionId != registration.Binding.SessionId ||
            dispatch.RuntimeInstanceId != registration.RuntimeInstanceId ||
            dispatch.RegistrationRevision != registration.Revision ||
            dispatch.ExecutionFence != registration.Binding.ExecutionFence ||
            dispatch.BindingHash != RuntimeContractValidation.RegistrationHash(registration) ||
            dispatch.SourceHash != sourceHash || dispatch.ModelSelectionReference != admission.Source.ModelSelectionReference ||
            dispatch.ModelId != admission.Source.ModelId || dispatch.PromptHash != admission.PromptHash ||
            dispatch.RequestHash != admission.RequestHash)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_invalid");
    }

    private async Task RequireNativeCheckpointAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeNativeTurnAdmissionReceipt admission, NativeDispatch dispatch, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT checkpoint.payload::text
            FROM {_checkpoints} checkpoint
            WHERE checkpoint.project_id = @project AND checkpoint.run_id = @run
                AND checkpoint.session_id = @root AND checkpoint.store_name = 'coordinator-execution'
                AND checkpoint.checkpoint_id = @checkpoint AND checkpoint.execution_fence = @fence
                AND EXISTS (
                    SELECT 1 FROM {_decisions} decision
                    WHERE decision.project_id = checkpoint.project_id AND decision.run_id = checkpoint.run_id
                        AND decision.session_id = checkpoint.session_id AND decision.state_version = @decision
                        AND NOT EXISTS (
                            SELECT 1 FROM {_decisions} next_decision
                            WHERE next_decision.project_id = decision.project_id AND next_decision.run_id = decision.run_id
                                AND next_decision.session_id = decision.session_id
                                AND next_decision.state_version > decision.state_version))
                AND NOT EXISTS (
                    SELECT 1 FROM {_checkpoints} next
                    WHERE next.project_id = checkpoint.project_id AND next.run_id = checkpoint.run_id
                        AND next.session_id = checkpoint.session_id AND next.store_name = checkpoint.store_name
                        AND next.parent_checkpoint_id = checkpoint.checkpoint_id)
            FOR SHARE OF checkpoint
            """, connection, transaction);
        AddNativeScope(command, registration, admission.MessageId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, dispatch.RootSessionId);
        command.Parameters.AddWithValue("checkpoint", NpgsqlDbType.Varchar, dispatch.CheckpointId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, dispatch.ExecutionFence);
        command.Parameters.AddWithValue("decision", NpgsqlDbType.Bigint, dispatch.DecisionStateVersion);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (json is not string payload)
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
        using var document = JsonDocument.Parse(payload);
        var checkpoint = MafExecutionCheckpointContract.Deserialize(document.RootElement);
        if (checkpoint.Revision != dispatch.CheckpointRevision ||
            checkpoint.DecisionStateVersion != dispatch.DecisionStateVersion ||
            checkpoint.WorkPlanId != dispatch.WorkPlanId ||
            !checkpoint.PendingDispatches.TryGetValue(dispatch.AssociationId, out var pending) ||
            pending.ChildSessionId != dispatch.ChildSessionId || pending.MessageId != admission.MessageId ||
            pending.PromptHash != admission.PromptHash ||
            checkpoint.Results.ContainsKey(dispatch.AssociationId) ||
            !IsNativeDispatchRunning(checkpoint, dispatch.AssociationId))
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
    }

    private static bool IsNativeDispatchRunning(MafExecutionCheckpoint checkpoint, string associationId) =>
        checkpoint.Progress.WorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Running ||
        checkpoint.Progress.FixedWorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Running;

    private static void AddNativeScope(NpgsqlCommand command, RuntimeRegistration registration, Guid messageId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
    }

    private static async Task AcquireNativeExecutionLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionIdentity root,
        CancellationToken cancellationToken)
    {
        await using var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.maf.execution'), hashtext(@scope))",
            connection, transaction);
        acquire.Parameters.AddWithValue("scope", NpgsqlDbType.Text,
            JsonSerializer.Serialize(new[] { root.ProjectId, root.RunId, root.SessionId }));
        await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record NativeDispatch(
        string RootSessionId, string ChildSessionId, Guid RuntimeInstanceId, long RegistrationRevision,
        long ExecutionFence, string BindingHash, string SourceHash, string ModelSelectionReference,
        string ModelId, string PromptHash, string RequestHash, long OwnerRevision, string State,
        string WorkPlanId, string AssociationId, string CheckpointId, long CheckpointRevision,
        long DecisionStateVersion, string? ReportHash, string? ReportJson);

    internal async Task<RuntimeSdkSourceReceipt> ReadCurrentSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        var stored = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, stored.Receipt.Source);
        if (stored.Receipt.RegistrationRevision != registration.Revision ||
            stored.Receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, stored.Receipt.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
        return stored.Receipt;
    }

    internal async Task<(UsageDispatchSourceCompletionManifest Manifest, string SelectionHash)?>
        ReadSourceCompletionAsync(
            string projectId, string runId, string sessionId, Guid dispatchId,
            CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch.source_completion_manifest_json::text, dispatch.source_completion_manifest_hash,
                dispatch.runtime_instance_id, dispatch.registration_revision, dispatch.execution_fence,
                dispatch.accepted_selection_hash, source.receipt_json::text, dispatch.sdk_source_receipt_hash
            FROM {_dispatches} dispatch
            JOIN {_sources} source ON source.runtime_instance_id = dispatch.runtime_instance_id
                AND source.registration_revision = dispatch.registration_revision
                AND source.canonical_payload_hash = dispatch.sdk_source_receipt_hash
            WHERE dispatch.project_id = @project AND dispatch.run_id = @run AND dispatch.child_session_id = @session
                AND dispatch.dispatch_id = @dispatch AND dispatch.source_report_status = 'source-complete'
                AND dispatch.source_completion_manifest_json IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var manifest = Deserialize<UsageDispatchSourceCompletionManifest>(reader.GetString(0));
        var source = Deserialize<RuntimeSdkSourceReceipt>(reader.GetString(6));
        UsageDispatchSourceCompletionManifestContract.Validate(manifest);
        if (manifest.DispatchId != dispatchId.ToString("D") || manifest.ProjectId != projectId ||
            manifest.RunId != runId || manifest.SessionId != sessionId ||
            manifest.ReceiptDigest != reader.GetString(1) || manifest.RuntimeInstanceId != reader.GetGuid(2) ||
            manifest.RegistrationRevision != reader.GetInt64(3) || manifest.ExecutionFence != reader.GetInt64(4) ||
            source.RuntimeInstanceId != manifest.RuntimeInstanceId ||
            source.RegistrationRevision != manifest.RegistrationRevision ||
            source.CanonicalPayloadHash != reader.GetString(7))
            throw new RuntimeAuthorizationException("runtime_usage_source_completion_invalid");
        if (source.Source.SourceMode != "hosted-copilot" ||
            source.Source.MeterSource != SdkMeterSources.CopilotNanoAiu)
            return null;
        return (manifest, reader.GetString(5));
    }

    private async Task<StoredSource?> ReadSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid runtimeInstanceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_json, configuration_hash, canonical_payload_hash, source_grant_id, registration_revision
            FROM {_sources} WHERE runtime_instance_id = @runtime
            """, connection, transaction);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, runtimeInstanceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var receipt = Deserialize<RuntimeSdkSourceReceipt>(reader.GetString(0));
        if (receipt.RuntimeInstanceId != runtimeInstanceId ||
            receipt.CanonicalPayloadHash != reader.GetString(2) ||
            receipt.SourceGrantId != reader.GetGuid(3) || receipt.RegistrationRevision != reader.GetInt64(4))
            throw new InvalidOperationException("A stored native SDK source receipt is inconsistent.");
        return new(receipt, reader.GetString(1));
    }

    private static void RequireSource(
        StoredSource source, RuntimeRegistration registration, RuntimeGrantReceipt grant)
    {
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source.Receipt.Source);
        if (source.Receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            source.Receipt.RegistrationRevision != registration.Revision ||
            source.Receipt.SourceGrantId != grant.GrantId || source.ConfigurationHash != grant.ConfigurationHash ||
            source.Receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                registration, source.Receipt.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
    }

    private DateTimeOffset RecordedAt()
    {
        var now = timeProvider.GetUtcNow();
        return new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native runtime source receipt is invalid.");

    private static void AddReceipt<T>(NpgsqlCommand command, string hash, T receipt, DateTimeOffset recordedAt)
    {
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Varchar, JsonSerializer.Serialize(receipt, JsonOptions));
        command.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, recordedAt);
    }

    private sealed record StoredSource(RuntimeSdkSourceReceipt Receipt, string ConfigurationHash);
}
