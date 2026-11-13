using System.Collections.Immutable;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// A consistent set of usage evidence exposed to runtime observation.
/// It combines the credit guard with bounded dispatch and tool-reservation history.
/// </summary>
internal sealed record RuntimeObservationEvidence(
    RuntimeObservationCreditGuard? CreditGuard,
    RuntimeObservationDispatchCollection Dispatches,
    RuntimeObservationToolReservationCollection ToolReservations);

internal sealed record RuntimeNativeActionDispatchEvidence(
    string RootSessionId,
    string ChildSessionId,
    Guid DispatchId,
    string State,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    string SourceHash,
    string RequestHash,
    string CheckpointId,
    long CheckpointRevision,
    long DecisionStateVersion,
    string? CheckpointHash,
    string WorkPlanId,
    string AssociationId);

/// <summary>
/// Persists runtime SDK source receipts, usage observations, native-turn dispatch state, and credit-limit evidence.
/// The store uses PostgreSQL transactions and outbox events to make admission, accounting, and denials replay-safe.
/// </summary>
internal sealed class RuntimeUsageSourceStore(
    NpgsqlDataSource dataSource, OrchestratorOptions options, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _schema = $"\"{options.Schema}\"";
    private readonly string _sources = $"\"{options.Schema}\".runtime_sdk_sources";
    private readonly string _observations = $"\"{options.Schema}\".runtime_usage_observations";
    private readonly string _dispatches = $"\"{options.Schema}\".maf_execution_dispatches";
    private readonly string _checkpoints = $"\"{options.Schema}\".maf_workflow_checkpoints";
    private readonly string _sessions = $"\"{options.Schema}\".coordination_sessions";
    private readonly string _decisions = $"\"{options.Schema}\".coordinator_decisions";
    private readonly string _executionOperations = $"\"{options.Schema}\".coordination_execution_operations";
    private readonly string _guards = $"\"{options.Schema}\".maf_execution_run_guards";
    private readonly string _outboxEvents = $"\"{options.Schema}\".outbox_events";
    private readonly PostgresOutbox _outbox = new(dataSource, options.Schema);
    private readonly SpendBudgetExposureStore _spendBudgets = new(dataSource, options, timeProvider);
    private readonly RuntimeExecutionEvidenceLedgerStore _executionEvidence = new(options.Schema);
    private readonly RuntimeRegistrationStore _registrations = new(dataSource, options.Schema, timeProvider);
    private readonly RuntimeObservationV2Store _runtimeObservationV2 = new(dataSource, options.Schema);

    internal Task<RuntimeObservationV2StoreResult> ReadObservationV2Async(
        RuntimeObservationV2ReadScope scope,
        int pageSize,
        string? cursor,
        Guid? attemptId,
        CancellationToken cancellationToken) =>
        _runtimeObservationV2.ReadAsync(scope, pageSize, cursor, attemptId, cancellationToken);

    internal Task AppendObservationV2Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        RuntimeObservationV2Fact fact,
        CancellationToken cancellationToken) =>
        _runtimeObservationV2.AppendAsync(connection, transaction, projectId, runId, fact, cancellationToken);

    /// <summary>
    /// Reads credit-guard, dispatch, and tool-reservation evidence from one repeatable-read snapshot.
    /// </summary>
    /// <param name="registration">The active runtime registration whose evidence is observed.</param>
    /// <param name="rootSessionId">The root session associated with the dispatch history.</param>
    /// <param name="cancellationToken">Cancels the transaction and queries.</param>
    /// <returns>Validated, bounded observation evidence.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the registration is invalid, stale, or owner evidence conflicts.</exception>
    internal async Task<RuntimeObservationEvidence> ReadObservationEvidenceAsync(
        RuntimeRegistration registration,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        RuntimeContractValidation.ValidateIdentifier(rootSessionId);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var intent = RuntimeCreditGuardObservationContract.CreateIntent(registration, rootSessionId);
        var creditGuard = await ReadCreditGuardWithinTransactionAsync(
            connection, transaction, registration, intent, cancellationToken).ConfigureAwait(false);
        var dispatches = await ReadObservationDispatchesAsync(
            connection, transaction, registration, rootSessionId, cancellationToken).ConfigureAwait(false);
        var toolReservations = await ReadObservationToolReservationsAsync(
            connection, transaction, registration, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RuntimeObservationEvidence(
            creditGuard is null
                ? null
                : new RuntimeObservationCreditGuard(
                    creditGuard.CopilotAicFloor,
                    creditGuard.AccountedThroughLedgerEventCount,
                    creditGuard.LedgerDigest,
                    creditGuard.GuardRevision,
                    creditGuard.SoftWarningEmitted,
                    creditGuard.HardCapExhausted,
                    creditGuard.UpdatedAt).Validate(),
            dispatches,
            toolReservations);
    }

    /// <summary>
    /// Reads the newest dispatch rows for the registered child and reports whether the bounded list was truncated.
    /// </summary>
    private async Task<RuntimeObservationDispatchCollection> ReadObservationDispatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        string rootSessionId,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var storedRows = new List<(
            Guid DispatchId,
            NativeDispatch Dispatch,
            string ReportState,
            string AccountingState,
            long? WitnessWatermark,
            string? WitnessDigest,
            DateTimeOffset UpdatedAt)>(
            RuntimeObservationDispatchCollection.MaximumItems + 1);
        await using var command = new NpgsqlCommand($"""
            SELECT d.dispatch_id, d.root_session_id, d.child_session_id, d.runtime_instance_id, d.registration_revision,
                d.execution_fence, d.runtime_binding_hash, d.sdk_source_receipt_hash,
                d.model_selection_reference, d.selected_model_id, d.prompt_hash, d.request_hash,
                d.owner_revision, d.dispatch_state, d.work_plan_id, d.association_id,
                d.checkpoint_id, d.checkpoint_revision, d.decision_state_version,
                d.source_report_hash, d.source_report_json::text, d.accepted_selection_hash,
                jsonb_build_object('byok', d.accepted_byok_descriptor, 'cost', d.cost_binding_identity)::text,
                d.is_cost_producing, d.hard_cap_serialized, d.created_at, d.updated_at,
                d.source_report_status, d.accounting_status, d.usage_witness_watermark,
                d.usage_witness_digest
            FROM {_dispatches} d
            LEFT JOIN LATERAL (
                SELECT receipt_json
                FROM {_schema}.maf_runtime_evidence_receipts receipt
                WHERE receipt.project_id = d.project_id AND receipt.run_id = d.run_id
                  AND receipt.receipt_kind = 'native-child-rebind'
                  AND receipt.receipt_json->>'dispatchId' = d.dispatch_id::text
                ORDER BY (receipt.receipt_json->>'destinationFence')::bigint DESC
                LIMIT 1
            ) rebind ON true
            WHERE d.project_id = @project AND d.run_id = @run AND d.root_session_id = @root
                AND d.child_session_id = @session
                AND (
                    (d.runtime_instance_id = @runtime AND d.registration_revision = @registrationRevision
                     AND d.execution_fence = @fence AND d.accepted_selection_hash = @selectionHash)
                    OR
                    (rebind.receipt_json->>'runtimeInstanceId' = @runtimeText
                     AND (rebind.receipt_json->>'registrationRevision')::bigint = @registrationRevision
                     AND (rebind.receipt_json->>'destinationFence')::bigint = @fence
                     AND upper(rebind.receipt_json->>'selectionHash') = upper(trim(@selectionHash)))
                )
            ORDER BY d.created_at DESC, d.dispatch_id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        command.Parameters.AddWithValue("runtimeText", NpgsqlDbType.Text, registration.RuntimeInstanceId.ToString("D"));
        command.Parameters.AddWithValue("registrationRevision", NpgsqlDbType.Bigint, registration.Revision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        command.Parameters.AddWithValue(
            "limit", NpgsqlDbType.Integer, RuntimeObservationDispatchCollection.MaximumItems + 1);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                using var cost = JsonDocument.Parse(reader.GetString(22));
                storedRows.Add((
                    reader.GetGuid(0),
                    new NativeDispatch(
                        reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt64(4),
                        reader.GetInt64(5), reader.GetString(6).Trim(), reader.GetString(7).Trim(),
                        reader.GetString(8), reader.GetString(9), reader.GetString(10).Trim(),
                        reader.GetString(11).Trim(), reader.GetInt64(12), reader.GetString(13),
                        reader.GetString(14), reader.GetString(15), reader.GetString(16),
                        reader.GetInt64(17), reader.GetInt64(18),
                        reader.IsDBNull(19) ? null : reader.GetString(19).Trim(),
                        reader.IsDBNull(20) ? null : reader.GetString(20),
                        reader.GetString(21).Trim(), ChildCheckpointRecoveryManifestCodec.Hash(cost.RootElement),
                        reader.GetBoolean(23), reader.GetBoolean(24), reader.GetFieldValue<DateTimeOffset>(25)),
                    reader.GetString(27), reader.GetString(28),
                    reader.IsDBNull(29) ? null : reader.GetInt64(29),
                    reader.IsDBNull(30) ? null : reader.GetString(30),
                    reader.GetFieldValue<DateTimeOffset>(26)));
            }
        }

        RuntimeSdkSourceReceipt? source = null;
        var rows = new List<RuntimeObservationDispatch>(storedRows.Count);
        foreach (var (dispatchId, dispatch, reportState, accountingState,
                     witnessWatermark, witnessDigest, updatedAt) in storedRows)
        {
            var receipts = await _executionEvidence.ReadNativeChildRebindsAsync(
                connection, transaction, binding.ProjectId, binding.RunId,
                dispatchId, cancellationToken).ConfigureAwait(false);
            NativeChildRebindReceipt? rebind = null;
            if (!receipts.IsEmpty)
            {
                source ??= await ReadCurrentSourceAsync(
                    connection, transaction, registration, cancellationToken).ConfigureAwait(false);
                var admission = new RuntimeNativeTurnAdmissionReceipt(
                    registration, source.Source, dispatchId,
                    dispatch.PromptHash, receipts[^1].EffectiveRequestHash, dispatch.OwnerRevision);
                rebind = await RequireNativeDispatchBindingAsync(
                    connection, transaction, dispatch, admission, source, cancellationToken).ConfigureAwait(false);
            }
            rows.Add(new RuntimeObservationDispatch(
                dispatchId,
                dispatch.RootSessionId,
                dispatch.ChildSessionId,
                dispatch.WorkPlanId,
                dispatch.AssociationId,
                dispatch.CheckpointId,
                dispatch.CheckpointRevision,
                dispatch.ExecutionFence,
                dispatch.OwnerRevision,
                dispatch.State,
                reportState,
                accountingState,
                witnessWatermark,
                witnessDigest,
                dispatch.CreatedAt,
                updatedAt)
            {
                NativeRebind = rebind is null
                    ? null
                    : new RuntimeObservationNativeRebind(
                        new(dispatch.RuntimeInstanceId, dispatch.RegistrationRevision, dispatch.ExecutionFence,
                            dispatch.CheckpointId, dispatch.CheckpointRevision, dispatch.SourceHash),
                        new(rebind.RuntimeInstanceId, rebind.RegistrationRevision, rebind.DestinationFence,
                            rebind.AnchorCheckpointId, rebind.AnchorCheckpointRevision,
                            rebind.SdkSourceReceiptHash),
                        rebind.ReceiptId,
                        rebind.CanonicalPayloadHash,
                        rebind.RecoveryOperationId,
                        rebind.EffectiveRequestHash)
            });
        }

        var truncated = rows.Count > RuntimeObservationDispatchCollection.MaximumItems;
        return new RuntimeObservationDispatchCollection(
            rows.Take(RuntimeObservationDispatchCollection.MaximumItems).ToArray(),
            truncated).Validate();
    }

    /// <summary>
    /// Reads recent tool-invocation admission events for the runtime session with a bounded result count.
    /// </summary>
    private async Task<RuntimeObservationToolReservationCollection> ReadObservationToolReservationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        CancellationToken cancellationToken)
    {
        const string eventType = "orchestrator.runtime.tool_invocation_admitted";
        var binding = registration.Binding;
        var rows = new List<RuntimeObservationToolReservation>(
            RuntimeObservationToolReservationCollection.MaximumItems + 1);
        await using var command = new NpgsqlCommand($"""
            SELECT id, payload->>'SessionId', @fence, occurred_at
            FROM {_outboxEvents}
            WHERE event_type = @eventType AND payload->>'projectId' = @project
                AND payload->>'runId' = @run AND payload->>'SessionId' = @session
                AND payload->>'ExecutionFence' = @fence::text
            ORDER BY occurred_at DESC, id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Varchar, eventType);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue(
            "limit", NpgsqlDbType.Integer, RuntimeObservationToolReservationCollection.MaximumItems + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RuntimeObservationToolReservation(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        var truncated = rows.Count > RuntimeObservationToolReservationCollection.MaximumItems;
        return new RuntimeObservationToolReservationCollection(
            rows.Take(RuntimeObservationToolReservationCollection.MaximumItems).ToArray(),
            truncated).Validate();
    }

    /// <summary>
    /// Runs a callback inside a transaction while holding a per-runtime usage advisory lock.
    /// </summary>
    /// <typeparam name="T">The callback result type.</typeparam>
    /// <param name="runtimeInstanceId">The runtime ID used as the lock scope.</param>
    /// <param name="action">The database work performed while the lock is held.</param>
    /// <param name="cancellationToken">Cancels connection, lock, or callback work.</param>
    /// <returns>The callback result.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the runtime ID is empty.</exception>
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

    /// <summary>
    /// Stores the first SDK source receipt for a runtime registration, or returns an exact matching replay.
    /// </summary>
    /// <param name="connection">The caller's database connection.</param>
    /// <param name="transaction">The transaction that owns the source receipt.</param>
    /// <param name="registration">The runtime registration being bound.</param>
    /// <param name="grant">The runtime grant that authorized source registration.</param>
    /// <param name="facts">The SDK session facts to preserve.</param>
    /// <param name="cancellationToken">Cancels database operations.</param>
    /// <returns>The existing or newly persisted source receipt.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when source facts, registration, or grant conflict with the stored receipt.</exception>
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

    /// <summary>
    /// Appends an SDK usage observation once, keyed by its deterministic event ID.
    /// </summary>
    /// <param name="connection">The caller's database connection.</param>
    /// <param name="transaction">The transaction that owns the observation.</param>
    /// <param name="registration">The runtime registration that produced usage.</param>
    /// <param name="grant">The grant that authorized the SDK source.</param>
    /// <param name="observation">The SDK usage event to record.</param>
    /// <param name="cancellationToken">Cancels database operations.</param>
    /// <returns>The stored usage receipt, or the prior receipt for an exact replay.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source is absent or the event ID is reused with different content.</exception>
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

    /// <summary>
    /// Reads one usage receipt scoped to its project, run, and session.
    /// </summary>
    /// <returns>The validated receipt, or <see langword="null"/> when the scoped row is absent.</returns>
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

    /// <summary>
    /// Validates a native-turn begin request, checks its checkpoint and credit limits, and advances dispatch state to sending.
    /// </summary>
    /// <param name="connection">The transaction connection.</param>
    /// <param name="transaction">The transaction that owns the dispatch-state change.</param>
    /// <param name="registration">The active runtime registration.</param>
    /// <param name="grant">The runtime grant for the SDK source.</param>
    /// <param name="request">The source facts and A2A message to admit.</param>
    /// <param name="readCostSnapshot">Reads the current usage-cost snapshot when configured credit limits require it.</param>
    /// <param name="cancellationToken">Cancels owner and cost-snapshot reads.</param>
    /// <returns>The admitted turn and current owner revision, or the denial receipt when a hard credit limit is reached.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the dispatch, message, checkpoint, source, or accounting state is invalid or uncertain.</exception>
    internal async Task<RuntimeNativeTurnBeginResult> BeginNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnBeginRequest request,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> readCostSnapshot,
        CancellationToken cancellationToken,
        ICollection<RuntimeObservationV2Fact>? observationFacts = null)
    {
        observationFacts ??= new List<RuntimeObservationV2Fact>();
        await using (var run = new NpgsqlCommand($"""
            SELECT 1 FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
              AND execution_fence = @fence
              AND execution_state IN ('idle', 'active', 'blocked')
            FOR SHARE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", registration.Binding.ProjectId);
            run.Parameters.AddWithValue("run", registration.Binding.RunId);
            run.Parameters.AddWithValue("fence", registration.Binding.ExecutionFence);
            if (await run.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new RuntimeAuthorizationException("runtime_native_turn_run_terminal");
        }
        if (await CheckpointRecoveryHold.IsHeldAsync(
                connection, transaction, _schema, registration.Binding.ProjectId,
                registration.Binding.RunId, cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("checkpoint_recovery_dispatch_held");

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
        var rebind = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source.Receipt, cancellationToken).ConfigureAwait(false);
        if (dispatch.State != "prepared")
            throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, dispatch, cancellationToken, rebind)
            .ConfigureAwait(false);
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
            var creditDecision = await ReconcileNativeCreditLimitsAsync(
                connection, transaction, registration, source.Receipt.Source, dispatch.RootSessionId,
                snapshot, cancellationToken).ConfigureAwait(false);
            observationFacts.Add(creditDecision.ObservationFact);
            if (creditDecision.HardLimitReached)
            {
                var denial = await RecordNativeCreditDenialAsync(
                    connection, transaction, registration, source.Receipt, message.MessageId,
                    RuntimeCreditLimitDenialStage.NativeTurnBegin, checkpointHash, creditDecision,
                    dispatch.State, dispatch.OwnerRevision, cancellationToken).ConfigureAwait(false);
                observationFacts.Add(RuntimeObservationV2Store.CreditDenialFact(registration, denial));
                return new(null, denial);
            }
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
        return new(admission, null);
    }

    /// <summary>
    /// Confirms a native-turn preparation belongs to the current root checkpoint and pending child dispatch.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the registration, message, or checkpoint intent is not the expected pending dispatch.</exception>
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

    /// <summary>
    /// Compares observed Copilot credits with configured soft and hard limits.
    /// </summary>
    /// <returns>Whether the soft and hard limits have each been reached.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when observed values or configured limits are invalid.</exception>
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

    /// <summary>
    /// Reports whether hosted Copilot usage needs a cost snapshot for any configured credit limit.
    /// </summary>
    internal static bool RequiresCopilotCreditSnapshot(RuntimeBinding binding) =>
        binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
        (binding.CopilotSoftCreditLimit is not null || binding.CopilotHardCreditLimit is not null);

    /// <summary>Computes the cost-rate identity stored with a Copilot credit guard.</summary>
    /// <remarks>Legacy multiplier cards keep their original projection so existing guards remain valid.</remarks>
    internal static string CostRateScopeIdentity(RuntimeUsageCostSnapshotReceipt snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var binding = snapshot.Binding;
        if (binding?.RateCard.ModelTokenRates is { Count: > 0 })
        {
            return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                meterSource = binding.MeterSource,
                providerId = binding.ProviderId,
                adapterVersion = binding.AdapterVersion,
                optionsSchemaVersion = binding.OptionsSchemaVersion,
                optionsRevision = binding.OptionsRevision,
                resourceId = binding.ResourceId,
                resourceGeneration = binding.ResourceGeneration,
                capabilities = binding.NegotiatedCapabilities.Order(StringComparer.Ordinal),
                rateCard = CostRateCardContract.SerializeCanonical(binding.RateCard)
            }));
        }

        return RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding?.MeterSource,
            binding?.ProviderId,
            binding?.AdapterVersion,
            binding?.OptionsSchemaVersion,
            binding?.OptionsRevision,
            binding?.ResourceId,
            binding?.ResourceGeneration,
            capabilities = binding?.NegotiatedCapabilities.Order(StringComparer.Ordinal),
            rateCard = binding is { } legacyBinding
                ? new
                {
                    legacyBinding.RateCard.Id,
                    legacyBinding.RateCard.Version,
                    legacyBinding.RateCard.MeterSource,
                    legacyBinding.RateCard.Unit,
                    legacyBinding.RateCard.NanoUnitsPerUnit,
                    models = legacyBinding.RateCard.ModelMultipliers
                        .OrderBy(model => model.Key, StringComparer.Ordinal)
                }
                : null
        }));
    }

    /// <summary>
    /// Reconciles a cost snapshot against the locked run credit guard and records monotonic limit state.
    /// </summary>
    /// <remarks>The guard update uses its owner revision as a compare-and-swap check; crossing the soft limit emits one outbox event.</remarks>
    private async Task<NativeCreditDecision> ReconcileNativeCreditLimitsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        SdkSessionFacts source, string rootSessionId, RuntimeUsageCostSnapshotReceipt snapshot,
        CancellationToken cancellationToken)
    {
        var observed = RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, new(1, registration, source));
        var binding = registration.Binding;
        // A hard cap must hold new dispatches when the quote or any whole-run usage remains unpriced.
        if (binding.CopilotHardCreditLimit is not null &&
            (snapshot.Quote.Disposition == CostDisposition.Unpriced ||
             !snapshot.CopilotTotals.IsFullyPriced ||
             snapshot.RepresentedReceipts.Any(reference =>
                 reference.Accounting.Disposition == CostDisposition.Unpriced)))
            throw new RuntimeAuthorizationException("runtime_cost_budget_pricing_missing");
        var limits = EvaluateObservedCreditLimits(binding, observed);
        var costIdentity = CostRateScopeIdentity(snapshot);
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
        // The legacy column stores a ledger-event count, not an SDK journal cursor.
        await using (var read = new NpgsqlCommand($"""
            SELECT root_session_id, tenant_id, accepted_selection_hash, execution_fence,
                copilot_soft_credit_limit, copilot_hard_credit_limit, cost_rate_scope_identity,
                copilot_aic_floor, accounted_through_event_position AS accounted_through_ledger_event_count,
                usage_witness_digest,
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
        var nextRevision = checked(revision + 1);
        DateTimeOffset guardUpdatedAt;
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_guards}
            SET copilot_aic_floor = @floor, accounted_through_event_position = @ledgerEventCount,
                usage_witness_digest = @digest, soft_warning_emitted = @warning,
                hard_cap_exhausted = @exhausted, owner_revision = owner_revision + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND owner_revision = @revision
            RETURNING updated_at
            """, connection, transaction))
        {
            AddNativeScope(update, registration, Guid.Empty);
            update.Parameters.AddWithValue("floor", NpgsqlDbType.Numeric, observed);
            update.Parameters.AddWithValue(
                "ledgerEventCount", NpgsqlDbType.Bigint, snapshot.CopilotTotals.Events);
            update.Parameters.AddWithValue("digest", NpgsqlDbType.Char, digest);
            update.Parameters.AddWithValue("warning", NpgsqlDbType.Boolean, warningEmitted || limits.SoftReached);
            update.Parameters.AddWithValue("exhausted", NpgsqlDbType.Boolean, hardExhausted || limits.HardReached);
            update.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, revision);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_usage_cost_snapshot_stale");
            guardUpdatedAt = reader.GetFieldValue<DateTimeOffset>(0);
        }
        if (!warningEmitted && limits.SoftReached)
        {
            var notificationId = Guid.NewGuid();
            await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
                notificationId, $"coordination/{binding.ProjectId}/{binding.RunId}/{rootSessionId}",
                "copilot-soft-credit-limit", "orchestrator.run.copilot_soft_credit_limit_reached", 1,
                JsonSerializer.SerializeToElement(new
                {
                    notificationId,
                    binding.ProjectId,
                    binding.RunId,
                    rootSessionId,
                    binding.ExecutionFence,
                    binding.CopilotSoftCreditLimit,
                    observedCredits = observed,
                    ledgerEventCount = snapshot.CopilotTotals.Events,
                    costIdentity
                }), timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        return new(observed, snapshot.CopilotTotals.Events, digest, costIdentity,
            revision, nextRevision, hardExhausted || limits.HardReached,
            RuntimeObservationV2Store.CreditGuardFact(
                registration, rootSessionId, observed, nextRevision, snapshot.CopilotTotals.Events, digest,
                warningEmitted || limits.SoftReached,
                hardExhausted || limits.HardReached,
                guardUpdatedAt));
    }

    /// <summary>
    /// Seals and appends deterministic hard-credit-denial evidence to the outbox.
    /// </summary>
    private async Task<RuntimeNativeCreditDenialReceipt> RecordNativeCreditDenialAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeSdkSourceReceipt source, Guid dispatchId, RuntimeCreditLimitDenialStage stage,
        string checkpointHash, NativeCreditDecision decision, string dispatchState, long dispatchRevision,
        CancellationToken cancellationToken)
    {
        var hardLimit = registration.Binding.CopilotHardCreditLimit
            ?? throw new RuntimeAuthorizationException("runtime_native_credit_denial_invalid");
        var recordedAt = timeProvider.GetUtcNow();
        var denial = RuntimeNativeDenialContract.SealDenial(new(
            1, registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            source.SourceGrantId,
            dispatchId, stage, RuntimeCreditLimitDenialCode.HardLimitReached,
            RuntimeContractValidation.RegistrationHash(registration),
            RuntimeUsageSourceReceiptContract.HashSource(registration, source.Source),
            RuntimeNativeDenialContract.DispatchHash(
                dispatchId, RuntimeContractValidation.RegistrationHash(registration),
                RuntimeUsageSourceReceiptContract.HashSource(registration, source.Source),
                checkpointHash, dispatchState, dispatchRevision),
            checkpointHash, hardLimit, decision.ObservedCredits, decision.LedgerEventCount,
            decision.LedgerDigest, decision.CostIdentity,
            decision.GuardRevisionBefore, decision.GuardRevisionAfter,
            dispatchState, dispatchRevision, dispatchState, dispatchRevision, recordedAt, string.Empty));
        RuntimeNativeDenialContract.ValidateDenial(denial, registration, source);
        var eventId = NativeCreditDenialEventId(denial);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            eventId,
            $"coordination/{registration.Binding.ProjectId}/{registration.Binding.RunId}/native-credit-denials",
            $"native-credit-denial/{eventId:D}",
            "orchestrator.native_turn.credit_denied", 1,
            JsonSerializer.SerializeToElement(denial, JsonOptions), recordedAt), cancellationToken)
            .ConfigureAwait(false);
        return denial;
    }

    /// <summary>
    /// Reads and validates a prior denial for one registration, dispatch, and denial stage.
    /// </summary>
    internal async Task<RuntimeNativeCreditDenialReceipt?> ReadNativeCreditDenialWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeSdkSourceReceipt source, Guid dispatchId, RuntimeCreditLimitDenialStage stage,
        CancellationToken cancellationToken)
    {
        if (dispatchId == Guid.Empty || !Enum.IsDefined(stage))
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_request_invalid");
        var stageName = stage switch
        {
            RuntimeCreditLimitDenialStage.Preparation => "preparation",
            RuntimeCreditLimitDenialStage.NativeTurnBegin => "nativeTurnBegin",
            _ => throw new RuntimeAuthorizationException("runtime_native_credit_denial_request_invalid")
        };
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.native_turn.credit_denied'
                AND event_version = 1 AND payload->>'dispatchId' = @dispatch
                AND payload->>'runtimeInstanceId' = @runtime AND payload->>'stage' = @stage
            ORDER BY sequence DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text,
            $"coordination/{registration.Binding.ProjectId}/{registration.Binding.RunId}/native-credit-denials");
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Text, dispatchId.ToString("D"));
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Text, registration.RuntimeInstanceId.ToString("D"));
        command.Parameters.AddWithValue("stage", NpgsqlDbType.Text, stageName);
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;
        var denial = Deserialize<RuntimeNativeCreditDenialReceipt>(payload);
        RuntimeNativeDenialContract.ValidateDenial(denial, registration, source);
        return denial;
    }

    /// <summary>
    /// Creates the stable outbox event ID for a denial and the credit-guard revision that observed it.
    /// </summary>
    private static Guid NativeCreditDenialEventId(RuntimeNativeCreditDenialReceipt denial)
    {
        var key = $"native-credit-denial\0{denial.RuntimeInstanceId:D}\0{denial.DispatchId:D}\0" +
            $"{denial.Stage}\0{denial.GuardRevisionAfter}";
        return Guid.ParseExact(RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(key))[..32], "N");
    }

    /// <summary>
    /// Adds the root, tenant, selection, fence, configured limits, and cost identity to a guard query.
    /// </summary>
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

    /// <summary>
    /// Persists the prepared native dispatch and returns a prior denial when the hard limit blocks sending.
    /// </summary>
    /// <remarks>
    /// The root session is locked before the MAF execution lock; an existing dispatch is accepted only as the same first prepared revision.
    /// Spend budgets are admitted after the credit check and before the dispatch row is written, so a denied dispatch holds no reservation.
    /// </remarks>
    internal async Task<RuntimeNativePreparationOutcome> PrepareNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, SessionIdentity root,
        MafExecutionCheckpointSnapshot checkpoint, MafExecutionDispatchIntent intent,
        RuntimeRegistration registration, RuntimeA2ASendRequest message,
        Func<SdkSessionFacts, CancellationToken, Task<RuntimeUsageCostSnapshotReceipt>> readCostSnapshot,
        CancellationToken cancellationToken,
        SpendBudgetAdmissionContext? spendBudgets = null,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>>? readSpendEvidence = null,
        ICollection<RuntimeObservationV2Fact>? observationFacts = null)
    {
        observationFacts ??= new List<RuntimeObservationV2Fact>();
        if (await CheckpointRecoveryHold.IsHeldAsync(
                connection, transaction, _schema, root.ProjectId, root.RunId, cancellationToken)
            .ConfigureAwait(false))
            throw new RuntimeAuthorizationException("checkpoint_recovery_dispatch_held");

        // Every check below runs in the caller's transaction. Any throw rolls back the whole preparation,
        // so a failed check leaves no dispatch row and no spend budget reservation behind.
        // Read the SDK source that is current for this registration and make sure the message was
        // built for that same source grant. A different grant means the host is using a stale source.
        var source = await ReadCurrentSourceAsync(connection, transaction, registration, cancellationToken)
            .ConfigureAwait(false);
        ValidateNativePreparation(root, checkpoint, intent, registration, source.Source, message);
        if (source.SourceGrantId != message.Message.Metadata.Runtime.SourceGrantId)
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
        // Lock the root session row. The session must still be active and must still carry the
        // execution fence from the registration; otherwise a newer owner has taken over the run.
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
        // Take the MAF execution lock after the session row lock. This fixed order prevents deadlocks
        // with other writers that use the same two locks.
        await AcquireNativeExecutionLockAsync(connection, transaction, root, cancellationToken).ConfigureAwait(false);
        // Build the dispatch row that this call wants to write, so it can be compared with any row
        // that already exists for the same message.
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Source, intent.MessageId, intent.PromptHash,
            RuntimeNativeTurnContract.RequestHash(message), 2);
        var prepared = new NativeDispatch(root.SessionId, intent.ChildSessionId, registration.RuntimeInstanceId,
            registration.Revision, registration.Binding.ExecutionFence,
            RuntimeContractValidation.RegistrationHash(registration), source.CanonicalPayloadHash,
            source.Source.ModelSelectionReference, source.Source.ModelId, intent.PromptHash, admission.RequestHash,
            1, "prepared", checkpoint.State.WorkPlanId, intent.AssociationId, checkpoint.Info.CheckpointId,
            checkpoint.State.Revision, checkpoint.State.DecisionStateVersion, null, null);
        var marker = await ReadNativeDispatchMarkerAsync(
            connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
        NativeChildRebindReceipt? rebind = null;
        var checkpointDispatch = prepared;
        if (marker is not null)
        {
            // A dispatch row already exists for this message, for example from a retried request.
            // Accept it only when it describes the same work and is still the first prepared revision.
            // A different row is a conflict; a row that already moved past "prepared" means the
            // earlier attempt may have been sent, so the result is indeterminate.
            var existingDispatch = await ReadNativeDispatchAsync(
                connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
            rebind = await RequireNativeDispatchBindingAsync(
                connection, transaction, existingDispatch, admission, source, cancellationToken)
                .ConfigureAwait(false);
            if (existingDispatch.RootSessionId != prepared.RootSessionId ||
                existingDispatch.WorkPlanId != prepared.WorkPlanId ||
                existingDispatch.AssociationId != prepared.AssociationId ||
                rebind is null &&
                (existingDispatch.CheckpointId != prepared.CheckpointId ||
                 existingDispatch.CheckpointRevision != prepared.CheckpointRevision ||
                 existingDispatch.DecisionStateVersion != prepared.DecisionStateVersion))
                throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
            if (existingDispatch.State != "prepared" || existingDispatch.OwnerRevision != 1)
                throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
            marker = new(existingDispatch.State, existingDispatch.OwnerRevision);
            checkpointDispatch = existingDispatch;
        }
        // The owner rechecks a rebound dispatch against its recovery anchor and current descendant.
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, checkpointDispatch,
            cancellationToken, rebind).ConfigureAwait(false);
        if (registration.Binding.MaxModelTurns is { } maxModelTurns)
        {
            var observedCount = await RuntimeModelTurnReservationStore.CountRunWideAsync(
                    connection, transaction, _schema, registration.Binding.ProjectId,
                    registration.Binding.RunId, root.SessionId, intent.MessageId, cancellationToken)
                    .ConfigureAwait(false);
            if (observedCount >= maxModelTurns)
            {
                if (marker is not null)
                    throw new RuntimeAuthorizationException("runtime_model_turn_limit_exhausted");
                var sendState = await RuntimeNumericLimitSendState.ReadAsync(
                    connection, transaction, _schema, registration.Binding.ProjectId,
                    registration.Binding.RunId, intent.MessageId, cancellationToken).ConfigureAwait(false);
                throw new RuntimeNumericLimitDeniedException(
                    "runtime_model_turn_limit_exhausted", RuntimeNumericLimitKind.ModelTurns,
                    maxModelTurns, observedCount, checkpoint.State.DecisionStateVersion,
                    sendState.Hash, sendState.IsEmpty);
            }
        }
        if (RequiresCopilotCreditSnapshot(registration.Binding))
        {
            // Credit check: read the current Copilot cost snapshot from Events and compare it with the
            // run's credit limits.
            var snapshot = await readCostSnapshot(source.Source, cancellationToken).ConfigureAwait(false);
            var creditDecision = await ReconcileNativeCreditLimitsAsync(
                connection, transaction, registration, source.Source, root.SessionId, snapshot, cancellationToken)
                .ConfigureAwait(false);
            observationFacts.Add(creditDecision.ObservationFact);
            // A reached hard credit limit returns a denial instead of throwing, so the denial receipt
            // is committed. The method returns before spend budget admission and before the dispatch
            // insert, so the denied dispatch holds no reservation and is never sent.
            if (creditDecision.HardLimitReached)
            {
                var denial = await RecordNativeCreditDenialAsync(
                    connection, transaction, registration, source, intent.MessageId,
                    RuntimeCreditLimitDenialStage.Preparation, checkpointHash, creditDecision,
                    marker?.State ?? "absent", marker?.OwnerRevision ?? 0, cancellationToken)
                    .ConfigureAwait(false);
                observationFacts.Add(RuntimeObservationV2Store.CreditDenialFact(registration, denial));
                return new(denial, null);
            }
        }
        // Spend budget admission runs after the session, fence, native limit, and credit checks and
        // before the dispatch insert. It compares committed Events spend plus all outstanding
        // reservations plus this dispatch's reserve with each hard limit. A soft limit only sends a
        // notification. When the dispatch is admitted, its reservations are written in this
        // transaction, so they commit or roll back together with the dispatch row.
        if (spendBudgets is { IsEmpty: false })
        {
            // Budgets without an evidence reader cannot be checked. Fail closed instead of sending
            // the dispatch without a budget check.
            var budgetDenial = await _spendBudgets.AdmitWithinTransactionAsync(
                connection, transaction, registration, source.Source.MeterSource, intent.MessageId, spendBudgets,
                readSpendEvidence ?? throw new RuntimeAuthorizationException(
                    SpendBudgetAdmissionMath.EvidenceUnavailable),
                cancellationToken).ConfigureAwait(false);
            // A denied budget returns before the dispatch insert. The denial notification commits,
            // but no reservation and no dispatch row are written, so the caller does not send.
            if (budgetDenial is not null)
                return new(null, budgetDenial);
        }
        // Write the prepared dispatch. ON CONFLICT DO NOTHING keeps an existing row for the same
        // message; the row is read back and verified below in both cases.
        var dispatchInserted = false;
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
            dispatchInserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        // Read the stored row back. It must match the dispatch built above and still be the first
        // prepared revision; anything else means another writer changed it.
        var retained = await ReadNativeDispatchAsync(
            connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
        rebind = await RequireNativeDispatchBindingAsync(
            connection, transaction, retained, admission, source, cancellationToken).ConfigureAwait(false);
        if (retained.RootSessionId != prepared.RootSessionId || retained.WorkPlanId != prepared.WorkPlanId ||
            retained.AssociationId != prepared.AssociationId ||
            rebind is null &&
            (retained.CheckpointId != prepared.CheckpointId ||
             retained.CheckpointRevision != prepared.CheckpointRevision ||
             retained.DecisionStateVersion != prepared.DecisionStateVersion))
            throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
        if (retained.State != "prepared" || retained.OwnerRevision != 1)
            throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
        if (dispatchInserted)
        {
            var prior = await RuntimeModelTurnReservationStore.CountRunWideAsync(
                connection, transaction, options.Schema,
                registration.Binding.ProjectId, registration.Binding.RunId,
                prepared.RootSessionId, intent.MessageId, cancellationToken).ConfigureAwait(false);
            var ownerPayloadHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                $"{prepared.RequestHash}\0{prepared.CheckpointId}\0{prepared.CheckpointRevision}\0" +
                $"{prepared.DecisionStateVersion}"));
            observationFacts.Add(RuntimeObservationV2Store.ModelTurnReservedFact(
                registration, intent.MessageId, intent.MessageId, retained.OwnerRevision,
                checked(prior + 1), "nativeDispatchPreparation", ownerPayloadHash,
                timeProvider.GetUtcNow()));
        }
        // No credit denial and no spend budget denial: the dispatch is admitted.
        return new(null, null);
    }

    internal async Task<RuntimeNumericLimitDenialReceipt> RecordNativeNumericLimitDenialWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity root,
        MafExecutionCheckpointSnapshot checkpoint,
        MafExecutionDispatchIntent intent,
        RuntimeRegistration registration,
        RuntimeA2ASendRequest message,
        RuntimeNumericLimitDeniedException denial,
        CancellationToken cancellationToken)
    {
        if (denial.Kind != RuntimeNumericLimitKind.ModelTurns ||
            registration.Binding.MaxModelTurns != denial.Limit)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        var source = await ReadCurrentSourceAsync(connection, transaction, registration, cancellationToken)
            .ConfigureAwait(false);
        ValidateNativePreparation(root, checkpoint, intent, registration, source.Source, message);
        if (source.SourceGrantId != message.Message.Metadata.Runtime.SourceGrantId ||
            checkpoint.State.DecisionStateVersion != denial.OwnerRevision)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");

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
                throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        }
        await AcquireNativeExecutionLockAsync(connection, transaction, root, cancellationToken).ConfigureAwait(false);
        if (await ReadNativeDispatchMarkerAsync(
                connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false)
            is not null)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        var count = await RuntimeModelTurnReservationStore.CountRunWideAsync(
            connection, transaction, _schema, registration.Binding.ProjectId,
            registration.Binding.RunId, root.SessionId, intent.MessageId, cancellationToken).ConfigureAwait(false);
        if (count != denial.ObservedCount || count < denial.Limit)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        var sendStateAfter = await RuntimeNumericLimitSendState.ReadAsync(
            connection, transaction, _schema, registration.Binding.ProjectId,
            registration.Binding.RunId, intent.MessageId, cancellationToken).ConfigureAwait(false);
        if (!denial.SendStateEmpty || !sendStateAfter.IsEmpty ||
            sendStateAfter.Hash != denial.SendStateHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_send_state_changed");

        var admission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Source, intent.MessageId, intent.PromptHash,
            RuntimeNativeTurnContract.RequestHash(message), 1);
        var candidate = new NativeDispatch(root.SessionId, intent.ChildSessionId,
            registration.RuntimeInstanceId, registration.Revision, registration.Binding.ExecutionFence,
            RuntimeContractValidation.RegistrationHash(registration), source.CanonicalPayloadHash,
            source.Source.ModelSelectionReference, source.Source.ModelId, intent.PromptHash,
            admission.RequestHash, 1, "prepared", checkpoint.State.WorkPlanId,
            intent.AssociationId, checkpoint.Info.CheckpointId, checkpoint.State.Revision,
            checkpoint.State.DecisionStateVersion, null, null);
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, candidate, cancellationToken)
            .ConfigureAwait(false);

        var receipt = RuntimeNumericLimitDenialReceipt.Seal(new(
            1, RuntimeNumericLimitDenialIdentity.ReceiptId(
                registration.Binding.ProjectId, registration.Binding.RunId, intent.MessageId),
            registration.Binding.ProjectId, registration.Binding.RunId, root.SessionId,
            registration.Binding.SessionId, registration.RuntimeInstanceId, registration.Revision,
            source.RegistrationRevision, source.SourceGrantId, source.CanonicalPayloadHash,
            source.SourceGrantId, message.Message.Metadata.Runtime.SourceGrantRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            registration.Binding.ExecutionFence, registration.Binding.AcceptedSelectionHash,
            denial.OwnerRevision, denial.Kind, denial.Limit, count, intent.MessageId,
            admission.RequestHash, intent.MessageId, checkpoint.Info.CheckpointId,
            checkpoint.State.Revision, checkpointHash, checkpoint.State.DecisionStateVersion,
            checkpoint.State.WorkPlanId, intent.AssociationId, denial.SendStateHash,
            sendStateAfter.Hash, timeProvider.GetUtcNow(), string.Empty));
        var stored = await _executionEvidence.AppendNumericLimitDenialAsync(
            connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
        await AppendObservationV2Async(
            connection, transaction, stored.ProjectId, stored.RunId,
            RuntimeObservationV2Store.DenialFact(stored), cancellationToken).ConfigureAwait(false);
        return stored;
    }

    internal async Task<RuntimeNumericLimitNativeNoSendReceipt> RecordNumericLimitNativeNoSendWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        RuntimeGrantReceipt grant,
        RuntimeNumericLimitNativeNoSendRecordRequest request,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var action = request.Action;
        var evidence = request.Evidence;
        RuntimeActionContract.Validate(action);
        evidence.Validate();
        if (binding.AgentId != RuntimeActionContract.CoordinatorRootStepId ||
            binding.WorkflowStepId is not null || binding.MaxModelTurns is null ||
            action.ActionId != "model.turn" || action.EventId != evidence.AttemptId ||
            action.DispatchId != evidence.DispatchId || action.DispatchId != action.EventId ||
            evidence.RuntimeInstanceId != registration.RuntimeInstanceId ||
            evidence.RegistrationRevision != registration.Revision ||
            evidence.ExecutionFence != binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");

        var source = await ReadCurrentSourceForGrantAsync(
            connection, transaction, registration, grant, cancellationToken).ConfigureAwait(false);
        var sourceHash = source.CanonicalPayloadHash;
        var original = await _executionEvidence.ReadNumericLimitDenialAsync(
            connection, transaction, binding.ProjectId, binding.RunId, evidence.AttemptId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_not_found");
        RuntimeNumericLimitNativeNoSendContract.ValidatePair(evidence, original, registration, action);
        if (evidence.SourceGrantId != grant.GrantId || evidence.SourceGrantRevision != grant.Revision ||
            evidence.SourceHash != sourceHash ||
            evidence.SdkSessionIdHash != RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(source.Source.SdkSessionId)))
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");

        await using (var run = new NpgsqlCommand($"""
            SELECT tenant_id, accepted_selection_hash, execution_fence, execution_state
            FROM {_schema}.accepted_runs
            WHERE project_id = @project AND run_id = @run
            FOR SHARE
            """, connection, transaction))
        {
            run.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            run.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            await using var reader = await run.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(0) != binding.TenantId ||
                !string.Equals(reader.GetString(1).Trim(), binding.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(2) != binding.ExecutionFence ||
                reader.GetString(3) is "completed" or "failed" or "indeterminate" or "cancelled")
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        }

        Guid decisionId;
        string rootSessionId;
        long ownerRevision;
        await using (var owner = new NpgsqlCommand($"""
            SELECT s.root_session_id, d.decision_id, d.state_version
            FROM {_sessions} s
            JOIN {_schema}.accepted_runs r ON r.project_id = s.project_id AND r.run_id = s.run_id
            JOIN {_decisions} d
              ON d.project_id = s.project_id AND d.run_id = s.run_id AND d.session_id = s.root_session_id
            WHERE s.project_id = @project AND s.run_id = @run AND s.session_id = @session
              AND s.lifecycle_state = 'active' AND s.execution_fence = @fence
              AND r.execution_fence = @fence AND r.execution_state <> 'completed'
              AND r.tenant_id = @tenant AND r.accepted_selection_hash = @selection
              AND d.decision_state = 'accepted'
            ORDER BY d.state_version DESC LIMIT 1
            FOR SHARE OF s, r, d
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            owner.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            owner.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
            owner.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
            owner.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
            owner.Parameters.AddWithValue(
                "selection", NpgsqlDbType.Char, binding.AcceptedSelectionHash.ToUpperInvariant());
            await using var reader = await owner.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
            rootSessionId = reader.GetString(0);
            decisionId = reader.GetGuid(1);
            ownerRevision = reader.GetInt64(2);
        }
        if (rootSessionId != binding.SessionId || ownerRevision != original.OwnerRevision ||
            original.RootSessionId != rootSessionId || original.SessionId != binding.SessionId ||
            original.LimitKind != RuntimeNumericLimitKind.ModelTurns ||
            original.AcceptedLimit != binding.MaxModelTurns ||
            original.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            original.SourceGrantId != grant.GrantId || original.SourceHash != sourceHash ||
            original.ReceiptId != RuntimeNumericLimitDenialIdentity.ReceiptId(
                binding.ProjectId, binding.RunId, evidence.AttemptId) ||
            original.SourceRegistrationRevision != registration.Revision ||
            original.GrantId != action.EventId || original.GrantRevision != "1" ||
            original.RequestHash != RuntimeActionContract.Hash(action) ||
            original.AttemptId != action.EventId || original.DispatchId != action.DispatchId)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");

        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.maf.execution'), hashtext(@scope))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("scope", JsonSerializer.Serialize(
                new[] { binding.ProjectId, binding.RunId, binding.SessionId }));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var available = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection, transaction))
        {
            available.Parameters.AddWithValue(
                "table", NpgsqlDbType.Text, $"{options.Schema}.maf_root_model_turn_admissions");
            if (!(bool)(await available.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
        }
        await using (var admission = new NpgsqlCommand($"""
            SELECT 1 FROM {_schema}.maf_root_model_turn_admissions
            WHERE project_id = @project AND run_id = @run AND root_session_id = @session
              AND request_id = @request AND actor_issuer = @issuer AND actor_id = @actor
              AND tenant_id = @tenant AND accepted_selection_hash = @selection
              AND prompt_hash = @prompt AND execution_fence = @fence
              AND decision_id = @decision AND decision_state_version = @decisionVersion
            """, connection, transaction))
        {
            admission.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
            admission.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
            admission.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
            admission.Parameters.AddWithValue("request", NpgsqlDbType.Uuid, action.EventId);
            admission.Parameters.AddWithValue("issuer", NpgsqlDbType.Varchar, binding.ActorIssuer);
            admission.Parameters.AddWithValue("actor", NpgsqlDbType.Varchar, binding.ActorId);
            admission.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
            admission.Parameters.AddWithValue("selection", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
            admission.Parameters.AddWithValue("prompt", NpgsqlDbType.Char, action.InputHash);
            admission.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
            admission.Parameters.AddWithValue("decision", NpgsqlDbType.Uuid, decisionId);
            admission.Parameters.AddWithValue("decisionVersion", NpgsqlDbType.Bigint, ownerRevision);
            if (await admission.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        }

        var count = await RuntimeModelTurnReservationStore.CountRunWideAsync(
            connection, transaction, options.Schema, binding.ProjectId, binding.RunId,
            rootSessionId, action.EventId, cancellationToken).ConfigureAwait(false);
        var sendState = await RuntimeNumericLimitSendState.ReadAsync(
            connection, transaction, options.Schema, binding.ProjectId, binding.RunId,
            action.EventId, cancellationToken).ConfigureAwait(false);
        if (count != original.ObservedCount || count < binding.MaxModelTurns.Value ||
            !sendState.IsEmpty || sendState.Hash != original.SendStateBeforeHash ||
            original.SendStateBeforeHash != original.SendStateAfterHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");

        var stored = await _executionEvidence.AppendNumericLimitNativeNoSendAsync(
            connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
        await AppendObservationV2Async(
            connection, transaction, stored.ProjectId, stored.RunId,
            RuntimeObservationV2Store.NativeNoSendFact(stored, original), cancellationToken).ConfigureAwait(false);
        return stored;
    }

    /// <summary>
    /// Records the runtime host's native-turn observation and advances the dispatch to accounting-pending.
    /// </summary>
    /// <param name="connection">The caller's database connection.</param>
    /// <param name="transaction">The transaction that owns the observation and dispatch update.</param>
    /// <param name="registration">The runtime registration.</param>
    /// <param name="grant">The grant used for the SDK source.</param>
    /// <param name="request">The admitted turn and observed usage/output.</param>
    /// <param name="cancellationToken">Cancels database operations.</param>
    /// <returns>The recorded receipt, or the previously recorded exact observation.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when observation contents conflict, usage is reused, or dispatch state cannot accept the report.</exception>
    /// <remarks>Matching repeated observations are idempotent; a first report advances the owner revision and is retained for later accounting.</remarks>
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
        _ = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, request.Admission, source.Receipt, cancellationToken)
            .ConfigureAwait(false);
        await RequireUniqueNativeUsageAsync(
            connection, transaction, request.Admission, request.Observation, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Reads the complete native-dispatch snapshot and locks the active root and dispatch when
    /// <paramref name="lockRows"/> is true.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">
    /// Thrown when the dispatch is unavailable. When <paramref name="lockRows"/> is true, the active root must also
    /// be available under the current fence.
    /// </exception>
    private async Task<NativeDispatch> ReadNativeDispatchAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        Guid messageId, CancellationToken cancellationToken, bool lockRows = true)
    {
        if (lockRows)
        {
            await using var root = new NpgsqlCommand($"""
                SELECT owner.session_id
                FROM {_sessions} owner
                JOIN {_dispatches} dispatch
                    ON owner.project_id = dispatch.project_id AND owner.run_id = dispatch.run_id
                        AND owner.session_id = dispatch.root_session_id
                WHERE dispatch.project_id = @project AND dispatch.run_id = @run AND dispatch.dispatch_id = @message
                    AND owner.lifecycle_state = 'active' AND owner.execution_fence = @fence
                FOR UPDATE OF owner
                """, connection, transaction);
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
                source_report_hash, source_report_json::text, accepted_selection_hash,
                jsonb_build_object('byok', accepted_byok_descriptor, 'cost', cost_binding_identity)::text,
                is_cost_producing, hard_cap_serialized, created_at
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
            {(lockRows ? "FOR UPDATE" : "")}
            """, connection, transaction);
        AddNativeScope(command, registration, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_unavailable");
        using var cost = JsonDocument.Parse(reader.GetString(21));
        return new(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetInt64(11),
            reader.GetString(12), reader.GetString(13), reader.GetString(14), reader.GetString(15),
            reader.GetInt64(16), reader.GetInt64(17), reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19), reader.GetString(20).Trim(),
            ChildCheckpointRecoveryManifestCodec.Hash(cost.RootElement), reader.GetBoolean(22),
            reader.GetBoolean(23), reader.GetFieldValue<DateTimeOffset>(24));
    }

    internal async Task<bool> IsNativeDispatchAdmittedForActionGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        Guid dispatchId,
        string promptHash,
        CancellationToken cancellationToken)
    {
        var evidence = await ReadNativeActionDispatchEvidenceAsync(
            connection, transaction, registration, dispatchId, promptHash, cancellationToken)
            .ConfigureAwait(false);
        return evidence is { State: "prepared" or "sending" };
    }

    internal async Task<RuntimeNativeActionDispatchEvidence?> ReadNativeActionDispatchEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        Guid dispatchId,
        string? expectedPromptHash,
        CancellationToken cancellationToken)
    {
        NativeDispatch dispatch;
        try
        {
            dispatch = await ReadNativeDispatchAsync(
                connection, transaction, registration, dispatchId, cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException exception)
            when (exception.Code == "runtime_native_turn_admission_unavailable")
        {
            return null;
        }
        var source = await ReadCurrentSourceAsync(
            connection, transaction, registration, cancellationToken).ConfigureAwait(false);
        var rebinds = await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, registration.Binding.ProjectId, registration.Binding.RunId,
            dispatchId, cancellationToken).ConfigureAwait(false);
        var requestHash = rebinds.IsEmpty ? dispatch.RequestHash : rebinds[^1].EffectiveRequestHash;
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Source, dispatchId, dispatch.PromptHash, requestHash, dispatch.OwnerRevision);
        NativeChildRebindReceipt? rebind;
        try
        {
            rebind = await RequireNativeDispatchBindingAsync(
                connection, transaction, dispatch, admission, source, cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException exception)
            when (exception.Code == "runtime_native_turn_admission_invalid")
        {
            return null;
        }
        if (expectedPromptHash is not null && dispatch.PromptHash != expectedPromptHash)
            return null;
        string? checkpointHash = null;
        if (dispatch.State is "prepared" or "sending")
            checkpointHash = await RequireNativeCheckpointAsync(
                connection, transaction, registration, admission, dispatch, cancellationToken, rebind)
                .ConfigureAwait(false);
        return new RuntimeNativeActionDispatchEvidence(
            dispatch.RootSessionId,
            dispatch.ChildSessionId,
            dispatchId,
            dispatch.State,
            rebind?.RuntimeInstanceId ?? dispatch.RuntimeInstanceId,
            rebind?.RegistrationRevision ?? dispatch.RegistrationRevision,
            rebind?.DestinationFence ?? dispatch.ExecutionFence,
            rebind?.SdkSourceReceiptHash ?? dispatch.SourceHash,
            rebind?.EffectiveRequestHash ?? dispatch.RequestHash,
            rebind?.AnchorCheckpointId ?? dispatch.CheckpointId,
            rebind?.AnchorCheckpointRevision ?? dispatch.CheckpointRevision,
            rebind?.AnchorDecisionStateVersion ?? dispatch.DecisionStateVersion,
            rebind?.AnchorCheckpointHash ?? checkpointHash,
            dispatch.WorkPlanId,
            dispatch.AssociationId);
    }

    /// <summary>
    /// Reads the dispatch state and owner revision under a row lock, if the dispatch exists.
    /// </summary>
    private async Task<NativeDispatchMarker?> ReadNativeDispatchMarkerAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_state, owner_revision FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @message
            FOR UPDATE
            """, connection, transaction);
        AddNativeScope(command, registration, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    /// <summary>
    /// Records evidence that a hard-credit denial prevented the native turn from being sent.
    /// </summary>
    /// <remarks>The evidence is accepted only for a matching stored denial and unchanged prepared dispatch/checkpoint hashes.</remarks>
    internal async Task<RuntimeNativeTurnNoSendReceipt> RecordNativeTurnNoSendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnNoSendEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        RuntimeNativeDenialContract.ValidateRequest(request, registration, source.Receipt);
        var denial = await ReadNativeCreditDenialWithinTransactionAsync(
            connection, transaction, registration, source.Receipt, request.Denial.DispatchId,
            RuntimeCreditLimitDenialStage.NativeTurnBegin, cancellationToken).ConfigureAwait(false);
        var storedDenial = denial
            ?? throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");
        if (storedDenial.CanonicalPayloadHash != request.Denial.CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");

        var existing = await ReadNativeTurnNoSendWithinTransactionAsync(
            connection, transaction, registration, request.Denial.CanonicalPayloadHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RuntimeNativeDenialContract.ValidateNoSend(existing, storedDenial, registration, source.Receipt);
            return existing;
        }

        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, request.Denial.DispatchId, cancellationToken).ConfigureAwait(false);
        var rebound = (await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, registration.Binding.ProjectId, registration.Binding.RunId,
            request.Denial.DispatchId, cancellationToken).ConfigureAwait(false)).LastOrDefault();
        var expectedAdmission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Receipt.Source, request.Denial.DispatchId, dispatch.PromptHash,
            rebound?.EffectiveRequestHash ?? dispatch.RequestHash, checked(dispatch.OwnerRevision + 1));
        var rebind = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, expectedAdmission, source.Receipt, cancellationToken)
            .ConfigureAwait(false);
        if (dispatch.State != "prepared" ||
            dispatch.OwnerRevision != request.Denial.DispatchRevisionAfter)
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, expectedAdmission, dispatch, cancellationToken, rebind)
            .ConfigureAwait(false);
        if (checkpointHash != storedDenial.CheckpointHash ||
            RuntimeNativeDenialContract.DispatchHash(
                request.Denial.DispatchId, storedDenial.RegistrationHash, storedDenial.SourceHash,
                checkpointHash, dispatch.State, dispatch.OwnerRevision) !=
            storedDenial.DispatchHash)
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");

        var eventId = NativeTurnNoSendEventId(denial.CanonicalPayloadHash);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            eventId,
            $"coordination/{registration.Binding.ProjectId}/{registration.Binding.RunId}/native-no-sends",
            $"native-turn-no-send/{eventId:D}",
            "orchestrator.native_turn.no_send", 1,
            JsonSerializer.SerializeToElement(request.Evidence, JsonOptions),
            request.Evidence.VerifiedAt), cancellationToken).ConfigureAwait(false);
        await AppendObservationV2Async(
            connection, transaction, registration.Binding.ProjectId, registration.Binding.RunId,
            RuntimeObservationV2Store.CreditNoSendFact(registration, storedDenial, request.Evidence),
            cancellationToken).ConfigureAwait(false);
        return request.Evidence;
    }

    /// <summary>
    /// Stores fixed-credit guard evidence as a deterministic outbox event, accepting only exact replays.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when registration, source tuple, grant, or replay evidence conflicts.</exception>
    internal async Task<RuntimeFixedCreditGuardReceipt> RecordFixedCreditGuardWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeFixedCreditGuardReceipt receipt, RuntimeGrantReceipt? grant,
        RuntimeSdkSourceReceipt? sourceReceipt, CancellationToken cancellationToken)
    {
        RuntimeFixedCreditGuardContract.ValidateReceipt(receipt);
        if (receipt.Registration != registration || (grant is null) != (sourceReceipt is null))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        if (receipt.SourceGrantId is not null)
        {
            if (grant is null || sourceReceipt is null)
                throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
            RuntimeFixedCreditGuardContract.ValidateSourceTuple(receipt, registration, grant, sourceReceipt);
        }
        else if (grant is not null || receipt.Stage == RuntimeFixedCreditGuardStage.HostPreQueue)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");

        var existing = await ReadFixedCreditGuardWithinTransactionAsync(
            connection, transaction, registration, receipt.Stage, receipt.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireFixedCreditGuardReplay(existing, receipt);
            return existing;
        }

        var eventId = FixedCreditGuardEventId(receipt);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            eventId,
            FixedCreditGuardStream(registration),
            $"fixed-credit-denial/{eventId:D}",
            "orchestrator.runtime.fixed_credit_denial", 1,
            JsonSerializer.SerializeToElement(receipt, JsonOptions),
            receipt.RecordedAt), cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// Records evidence that a fixed-credit guard prevented a runtime action from being sent.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the stored guard receipt, grant, source, or no-send evidence does not match.</exception>
    internal async Task<RuntimeFixedCreditNoSendReceipt> RecordFixedCreditNoSendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeSdkSourceReceipt sourceReceipt,
        RuntimeFixedCreditNoSendEvidenceRequest request, CancellationToken cancellationToken)
    {
        var stored = await ReadFixedCreditGuardWithinTransactionAsync(
            connection, transaction, registration, request.Receipt.Stage,
            request.Receipt.RequestId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        if (stored.CanonicalPayloadHash != request.Receipt.CanonicalPayloadHash)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        RuntimeFixedCreditGuardContract.ValidateSourceTuple(stored, registration, grant, sourceReceipt);
        RuntimeFixedCreditGuardContract.ValidateEvidence(
            request.Evidence, stored, registration, grant, sourceReceipt);

        var existing = await ReadFixedCreditNoSendWithinTransactionAsync(
            connection, transaction, registration, stored.CanonicalPayloadHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RuntimeFixedCreditGuardContract.ValidateEvidence(
                existing, stored, registration, grant, sourceReceipt);
            return existing;
        }

        var eventId = FixedCreditNoSendEventId(stored.CanonicalPayloadHash);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            eventId,
            FixedCreditGuardStream(registration),
            $"fixed-credit-no-send/{eventId:D}",
            "orchestrator.runtime.fixed_credit_no_send", 1,
            JsonSerializer.SerializeToElement(request.Evidence, JsonOptions),
            request.Evidence.VerifiedAt), cancellationToken).ConfigureAwait(false);
        return request.Evidence;
    }

    /// <summary>
    /// Reads a fixed-credit guard receipt and, when requested, its matching no-send evidence.
    /// </summary>
    /// <param name="includeEvidence">Whether to load and validate the corresponding no-send record.</param>
    /// <returns>The guard and optional evidence, or <see langword="null"/> when no guard exists for the request.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when request, registration, receipt hash, or source evidence conflicts.</exception>
    internal async Task<RuntimeFixedCreditGuardReadResult?> ReadFixedCreditGuardResultWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeFixedCreditGuardReadRequest request, bool includeEvidence,
        RuntimeGrantReceipt? grant, RuntimeSdkSourceReceipt? sourceReceipt,
        CancellationToken cancellationToken)
    {
        RuntimeFixedCreditGuardContract.ValidateReadRequest(request);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != registration.Binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        var receipt = await ReadFixedCreditGuardWithinTransactionAsync(
            connection, transaction, registration, request.Stage, request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
            return null;
        if (receipt.RequestHash != request.RequestHash)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");

        RuntimeFixedCreditNoSendReceipt? evidence = null;
        if (includeEvidence)
        {
            evidence = await ReadFixedCreditNoSendWithinTransactionAsync(
                connection, transaction, registration, receipt.CanonicalPayloadHash, cancellationToken)
                .ConfigureAwait(false);
            if (evidence is not null)
            {
                if (grant is null || sourceReceipt is null)
                    throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
                RuntimeFixedCreditGuardContract.ValidateEvidence(
                    evidence, receipt, registration, grant, sourceReceipt);
            }
            else if (receipt.SourceGrantId is not null &&
                (grant is null || sourceReceipt is null ||
                 receipt.SourceGrantId != grant.GrantId ||
                 receipt.SourceGrantRevision != grant.Revision ||
                 receipt.SourceHash != RuntimeUsageSourceReceiptContract.HashSource(
                     registration, sourceReceipt.Source)))
                throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        }
        return new(receipt, evidence);
    }

    /// <summary>
    /// Selects the one complete fixed-credit denial pair for the exact current registration and source grant.
    /// </summary>
    internal async Task<RuntimeFixedCreditGuardSelectionResult> SelectFixedCreditGuardWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeSdkSourceReceipt sourceReceipt,
        RuntimeFixedCreditGuardSelectionRequest request, CancellationToken cancellationToken)
    {
        RuntimeFixedCreditGuardContract.ValidateSelectionRequest(request);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.RegistrationRevision != registration.Revision ||
            request.ExecutionFence != registration.Binding.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");

        var sourceHash = RuntimeUsageSourceReceiptContract.HashSource(registration, sourceReceipt.Source);
        var candidates = new List<RuntimeFixedCreditGuardReceipt>();
        await using (var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.runtime.fixed_credit_denial'
                AND event_version = 1
                AND payload->'registration'->>'runtimeInstanceId' = @runtime
                AND payload->'registration'->>'revision' = @revision
                AND payload->'registration'->'binding'->>'executionFence' = @fence
                AND payload->>'stage' = @stage
                AND payload->>'sourceHash' = @sourceHash
                AND payload->>'sourceGrantId' = @grantId
                AND payload->>'sourceGrantRevision' = @grantRevision
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, FixedCreditGuardStream(registration));
            command.Parameters.AddWithValue("runtime", NpgsqlDbType.Text, registration.RuntimeInstanceId.ToString("D"));
            command.Parameters.AddWithValue("revision", NpgsqlDbType.Text, registration.Revision.ToString());
            command.Parameters.AddWithValue("fence", NpgsqlDbType.Text,
                registration.Binding.ExecutionFence.ToString());
            command.Parameters.AddWithValue("stage", NpgsqlDbType.Text, "hostPreQueue");
            command.Parameters.AddWithValue("sourceHash", NpgsqlDbType.Text, sourceHash);
            command.Parameters.AddWithValue("grantId", NpgsqlDbType.Text, grant.GrantId.ToString("D"));
            command.Parameters.AddWithValue("grantRevision", NpgsqlDbType.Text, grant.Revision.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var receipt = Deserialize<RuntimeFixedCreditGuardReceipt>(reader.GetString(0));
                RuntimeFixedCreditGuardContract.ValidateReceipt(receipt);
                RuntimeFixedCreditGuardContract.ValidateSourceTuple(
                    receipt, registration, grant, sourceReceipt);
                if (receipt.Stage != RuntimeFixedCreditGuardStage.HostPreQueue)
                    throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
                candidates.Add(receipt);
            }
        }

        RuntimeFixedCreditGuardReceipt? selectedReceipt = null;
        RuntimeFixedCreditNoSendReceipt? selectedEvidence = null;
        foreach (var receipt in candidates)
        {
            var evidence = await ReadFixedCreditNoSendUniqueWithinTransactionAsync(
                connection, transaction, registration, receipt.CanonicalPayloadHash, cancellationToken)
                .ConfigureAwait(false);
            if (evidence is null)
                continue;
            RuntimeFixedCreditGuardContract.ValidateEvidence(
                evidence, receipt, registration, grant, sourceReceipt);
            if (selectedReceipt is not null)
                return new(null, null);
            selectedReceipt = receipt;
            selectedEvidence = evidence;
        }
        return selectedReceipt is null
            ? new(null, null)
            : new(selectedReceipt, selectedEvidence);
    }

    /// <summary>
    /// Reads and validates one fixed-credit guard event by runtime, stage, and request ID.
    /// </summary>
    internal async Task<RuntimeFixedCreditGuardReceipt?> ReadFixedCreditGuardWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeFixedCreditGuardStage stage, Guid requestId, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(stage) || requestId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_request_invalid");
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.runtime.fixed_credit_denial'
                AND event_version = 1
                AND payload->'registration'->>'runtimeInstanceId' = @runtime
                AND payload->'registration'->>'revision' = @revision
                AND payload->'registration'->'binding'->>'executionFence' = @fence
                AND payload->>'stage' = @stage
                AND payload->>'requestId' = @request
            ORDER BY sequence DESC LIMIT 1
            """, connection, transaction);
        AddFixedCreditGuardScope(command, registration, stage, requestId);
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;
        var receipt = Deserialize<RuntimeFixedCreditGuardReceipt>(payload);
        RuntimeFixedCreditGuardContract.ValidateReceipt(receipt);
        if (receipt.Registration != registration)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        return receipt;
    }

    /// <summary>
    /// Reads a fixed-credit no-send event by the digest of its guard receipt.
    /// </summary>
    private async Task<RuntimeFixedCreditNoSendReceipt?> ReadFixedCreditNoSendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string guardReceiptHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.runtime.fixed_credit_no_send'
                AND event_version = 1 AND payload->>'guardReceiptHash' = @guard
            ORDER BY sequence DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, FixedCreditGuardStream(registration));
        command.Parameters.AddWithValue("guard", NpgsqlDbType.Text, guardReceiptHash);
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? null : Deserialize<RuntimeFixedCreditNoSendReceipt>(payload);
    }

    private async Task<RuntimeFixedCreditNoSendReceipt?> ReadFixedCreditNoSendUniqueWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string guardReceiptHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.runtime.fixed_credit_no_send'
                AND event_version = 1 AND payload->>'guardReceiptHash' = @guard
            LIMIT 2
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, FixedCreditGuardStream(registration));
        command.Parameters.AddWithValue("guard", NpgsqlDbType.Text, guardReceiptHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var evidence = Deserialize<RuntimeFixedCreditNoSendReceipt>(reader.GetString(0));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        return evidence;
    }

    /// <summary>
    /// Reads previously recorded no-send evidence for a native-turn credit denial.
    /// </summary>
    internal async Task<RuntimeNativeTurnNoSendReceipt?> ReadNativeTurnNoSendWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string denialHash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text FROM {_outboxEvents}
            WHERE stream_id = @stream AND event_type = 'orchestrator.native_turn.no_send'
                AND event_version = 1 AND payload->>'denialReceiptHash' = @denial
            ORDER BY sequence DESC LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text,
            $"coordination/{registration.Binding.ProjectId}/{registration.Binding.RunId}/native-no-sends");
        command.Parameters.AddWithValue("denial", NpgsqlDbType.Text, denialHash);
        var payload = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? null : Deserialize<RuntimeNativeTurnNoSendReceipt>(payload);
    }

    /// <summary>
    /// Derives the stable event ID for no-send evidence from its denial receipt hash.
    /// </summary>
    private static Guid NativeTurnNoSendEventId(string denialHash) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes($"native-turn-no-send\0{denialHash}"))[..32], "N");

    /// <summary>
    /// Builds the run-scoped outbox stream for fixed-credit guard records.
    /// </summary>
    private static string FixedCreditGuardStream(RuntimeRegistration registration) =>
        $"coordination/{registration.Binding.ProjectId}/{registration.Binding.RunId}/fixed-credit-denials";

    /// <summary>
    /// Derives the stable fixed-credit guard event ID from registration, fence, stage, and request.
    /// </summary>
    private static Guid FixedCreditGuardEventId(RuntimeFixedCreditGuardReceipt receipt) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"fixed-credit-denial\0{receipt.Registration.RuntimeInstanceId:D}\0" +
            $"{receipt.Registration.Revision}\0{receipt.Registration.Binding.ExecutionFence}\0" +
            $"{receipt.Stage}\0{receipt.RequestId:D}"))[..32], "N");

    /// <summary>
    /// Derives the stable event ID for fixed-credit no-send evidence from its guard receipt hash.
    /// </summary>
    private static Guid FixedCreditNoSendEventId(string guardReceiptHash) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes($"fixed-credit-no-send\0{guardReceiptHash}"))[..32], "N");

    /// <summary>
    /// Rejects an existing fixed-credit guard event when any stable request or source binding changed.
    /// </summary>
    private static void RequireFixedCreditGuardReplay(
        RuntimeFixedCreditGuardReceipt existing, RuntimeFixedCreditGuardReceipt requested)
    {
        if (existing.Registration != requested.Registration ||
            existing.RegistrationHash != requested.RegistrationHash ||
            existing.RequestHash != requested.RequestHash ||
            existing.SourceHash != requested.SourceHash ||
            existing.SourceGrantId != requested.SourceGrantId ||
            existing.SourceGrantRevision != requested.SourceGrantRevision)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
    }

    /// <summary>
    /// Reads the recorded native-turn receipt and report inside the caller's transaction.
    /// </summary>
    internal async Task<RuntimeNativeTurnRecordedReceipt> ReadNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeNativeTurnRecordedReceipt expected, CancellationToken cancellationToken)
    {
        var (_, recorded) = await ReadNativeTurnStateAsync(
            connection, transaction, registration, expected, cancellationToken).ConfigureAwait(false);
        return recorded;
    }

    private async Task<(NativeDispatch Dispatch, RuntimeNativeTurnRecordedReceipt Recorded)> ReadNativeTurnStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeNativeTurnRecordedReceipt expected, CancellationToken cancellationToken)
    {
        RuntimeNativeTurnContract.ValidateRecorded(expected);
        var admission = expected.Admission;
        var source = await ReadCurrentSourceAsync(connection, transaction, registration, cancellationToken)
            .ConfigureAwait(false);
        if (admission.Registration != registration || admission.Source != source.Source)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, registration, admission.MessageId, cancellationToken).ConfigureAwait(false);
        var rebind = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source, cancellationToken).ConfigureAwait(false);
        if (dispatch.State is not ("terminal_pending_accounting" or "completed") ||
            dispatch.ReportHash != expected.CanonicalPayloadHash || dispatch.ReportJson is null ||
            expected.OwnerRevision !=
                (dispatch.State == "completed" ? dispatch.OwnerRevision - 1 : dispatch.OwnerRevision))
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var recorded = new RuntimeNativeTurnRecordedReceipt(
            admission, Deserialize<RuntimeNativeTurnObservation>(dispatch.ReportJson),
            dispatch.ReportHash, expected.OwnerRevision);
        RuntimeNativeTurnContract.ValidateRecorded(recorded);
        if (recorded.Observation.CompletedAt > timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_native_turn_observation_invalid");
        await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, dispatch, cancellationToken, rebind)
            .ConfigureAwait(false);
        await RequireUniqueNativeUsageAsync(
            connection, transaction, admission, recorded.Observation, cancellationToken).ConfigureAwait(false);
        return (dispatch, recorded);
    }

    /// <summary>
    /// Ensures that none of the SDK usage event IDs in this observation were already reported by another dispatch.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when a usage event has already been associated with another native turn.</exception>
    private async Task RequireUniqueNativeUsageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeNativeTurnAdmissionReceipt admission,
        RuntimeNativeTurnObservation observation, CancellationToken cancellationToken)
    {
        // One SDK usage event cannot account for two dispatches in the same runtime session.
        if (observation.UsageEventIds.IsEmpty)
            return;
        await using var command = new NpgsqlCommand($"""
            SELECT EXISTS (
                SELECT 1
                FROM {_dispatches} dispatch
                LEFT JOIN LATERAL (
                    SELECT receipt_json
                    FROM {_schema}.maf_runtime_evidence_receipts receipt
                    WHERE receipt.project_id = dispatch.project_id AND receipt.run_id = dispatch.run_id
                      AND receipt.receipt_kind = 'native-child-rebind'
                      AND receipt.receipt_json->>'dispatchId' = dispatch.dispatch_id::text
                    ORDER BY (receipt.receipt_json->>'destinationFence')::bigint DESC
                    LIMIT 1
                ) rebind ON true
                WHERE dispatch.project_id = @project AND dispatch.run_id = @run
                    AND dispatch.dispatch_id <> @message
                    AND dispatch.source_report_json->'source'->>'sdkSessionId' = @sdk_session
                    AND (dispatch.source_report_json->'usageEventIds') ?| @events
                    AND (
                        dispatch.runtime_instance_id = @runtime
                        OR rebind.receipt_json->>'runtimeInstanceId' = @runtimeText
                    ))
            """, connection, transaction);
        AddNativeScope(command, admission.Registration, admission.MessageId);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, admission.Source.RuntimeInstanceId);
        command.Parameters.AddWithValue("runtimeText", NpgsqlDbType.Text,
            admission.Source.RuntimeInstanceId.ToString("D"));
        command.Parameters.AddWithValue("sdk_session", NpgsqlDbType.Text, admission.Source.SdkSessionId);
        command.Parameters.AddWithValue("events", NpgsqlDbType.Array | NpgsqlDbType.Text,
            observation.UsageEventIds.Select(id => id.ToString("D")).ToArray());
        if ((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            throw new RuntimeAuthorizationException("runtime_native_turn_usage_reused");
    }

    /// <summary>
    /// Validates usage-cost receipts and marks a terminal native dispatch accounted.
    /// </summary>
    /// <param name="snapshot">The owner-confirmed cost snapshot that covers the recorded turn and required receipts.</param>
    /// <returns>The accounting receipt, or the prior accounted receipt when references match exactly.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when cost evidence, stored acknowledgment references, or dispatch revision conflicts.</exception>
    /// <remarks>The update is a compare-and-swap from terminal-pending to completed, and records priced versus unpriced status.</remarks>
    internal async Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeTurnWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, RuntimeNativeTurnAccountingRequest request,
        RuntimeUsageCostSnapshotReceipt snapshot,
        CancellationToken cancellationToken)
    {
        var snapshotRequest = RuntimeNativeTurnContract.AccountingSnapshotRequest(
            request.Recorded, request.RequiredReceipts);
        var admission = request.Recorded.Admission;
        var source = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(source, registration, grant);
        var (dispatch, _) = await ReadNativeTurnStateAsync(
            connection, transaction, registration, request.Recorded, cancellationToken).ConfigureAwait(false);
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
        // Persist accounting only after Events proves coverage of the exact recorded turn and receipt set.
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

    /// <summary>
    /// Requires that the dispatch's recorded output and usage have a completed accounting record.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when accounting is pending, output hashes differ, or stored references are invalid.</exception>
    internal async Task<RuntimeChildTurnAccountingEvidence> RequireNativeTurnAccountedWithinTransactionAsync(
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
        _ = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source, cancellationToken).ConfigureAwait(false);
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
        var recorded = new RuntimeNativeTurnRecordedReceipt(
            admission, observation, dispatch.ReportHash, checked(dispatch.OwnerRevision - 1));
        RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        return new RuntimeChildTurnAccountingEvidence(
            recorded, source.CanonicalPayloadHash, dispatch.OwnerRevision, reader.GetString(1));
    }

    internal async Task<RuntimeChildTurnAccountingEvidence> RequireNativeTurnAccountedForRecoveryWithinTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SessionIdentity root,
        ChildRecoveryMember member,
        string acceptedSelectionHash,
        long sourceFence,
        string answer,
        CancellationToken cancellationToken)
    {
        var native = member.NativeOrigin
            ?? throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var savedRegistration = member.SavedRegistration
            ?? throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var binding = member.Startup.Binding;
        if (native.State != "completed" ||
            native.TerminalOutcome != "native-completed" ||
            native.SourceReportStatus != "partial" ||
            native.SourceReportHash is null ||
            native.AccountingStatus is not ("priced" or "unpriced") ||
            binding.ParentRootSessionId != root.SessionId ||
            binding.ChildSessionId != member.Session.SessionId ||
            binding.AssociationId != member.AssociationId ||
            binding.DispatchMessageId != native.DispatchId ||
            binding.PromptHash != native.PromptHash ||
            !string.Equals(native.AcceptedSelectionHash, acceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase) ||
            savedRegistration.Binding.SessionId != member.Session.SessionId ||
            savedRegistration.Binding.ExecutionFence != sourceFence ||
            !string.Equals(savedRegistration.Binding.AcceptedSelectionHash, acceptedSelectionHash,
                StringComparison.OrdinalIgnoreCase))
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");

        var currentOrigin = await ChildCheckpointRecoveryCensus.ReadNativeOriginAsync(
            connection, transaction, _schema, root, member.Session, binding,
            acceptedSelectionHash, cancellationToken).ConfigureAwait(false);
        if (currentOrigin != native)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");

        var rebinds = await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, root.ProjectId, root.RunId, native.DispatchId, cancellationToken)
            .ConfigureAwait(false);
        RuntimeRegistration effectiveRegistration;
        NativeChildRebindReceipt? effectiveRebind;
        if (rebinds.IsEmpty)
        {
            if (member.PriorNativeRebindReceiptId is not null ||
                member.PriorNativeRebindPayloadHash is not null ||
                savedRegistration.RuntimeInstanceId != native.RuntimeInstanceId ||
                savedRegistration.Revision != native.RegistrationRevision ||
                savedRegistration.Binding.ExecutionFence != native.ExecutionFence ||
                RuntimeContractValidation.RegistrationHash(savedRegistration) != native.RuntimeBindingHash)
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
            effectiveRegistration = savedRegistration;
            effectiveRebind = null;
        }
        else
        {
            effectiveRebind = rebinds[^1];
            if (effectiveRebind.ReceiptId != member.PriorNativeRebindReceiptId ||
                effectiveRebind.CanonicalPayloadHash != member.PriorNativeRebindPayloadHash ||
                effectiveRebind.OriginFingerprint !=
                    NativeChildRebindOriginFingerprint.Create(effectiveRebind.TenantId, root, member).Hash)
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
            effectiveRegistration = await _registrations.ReadRevisionAsync(
                connection, transaction, effectiveRebind.RuntimeInstanceId,
                effectiveRebind.RegistrationRevision, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
            if (RuntimeContractValidation.RegistrationHash(effectiveRegistration) !=
                effectiveRebind.RegistrationHash)
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        }

        var source = await ReadCurrentSourceAsync(
            connection, transaction, effectiveRegistration, cancellationToken).ConfigureAwait(false);
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, effectiveRegistration, native.DispatchId, cancellationToken,
            lockRows: false).ConfigureAwait(false);
        if (!MatchesNativeOrigin(dispatch, native) ||
            source.Source.ModelSelectionReference != native.ModelSelectionReference ||
            source.Source.ModelId != native.SelectedModelId ||
            (effectiveRebind is null
                ? source.CanonicalPayloadHash != native.SdkSourceReceiptHash
                : source.CanonicalPayloadHash != effectiveRebind.SdkSourceReceiptHash))
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");

        var admission = new RuntimeNativeTurnAdmissionReceipt(
            effectiveRegistration,
            source.Source,
            native.DispatchId,
            native.PromptHash,
            effectiveRebind?.EffectiveRequestHash ?? native.RequestHash,
            checked(native.OwnerRevision - 2));
        var validatedRebind = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source, cancellationToken,
            requireReleasedHold: false).ConfigureAwait(false);
        if ((effectiveRebind is null) != (validatedRebind is null) ||
            effectiveRebind is not null &&
            (validatedRebind!.ReceiptId != effectiveRebind.ReceiptId ||
             validatedRebind.CanonicalPayloadHash != effectiveRebind.CanonicalPayloadHash))
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");

        string reportJson;
        string reportHash;
        string acknowledgmentsJson;
        long ownerRevision;
        await using (var command = new NpgsqlCommand($"""
            SELECT source_report_json::text, source_report_hash,
                   acknowledgment_references::text, owner_revision
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
                AND child_session_id = @child AND dispatch_state = 'completed'
                AND source_report_status = 'partial' AND source_report_hash IS NOT NULL
                AND accounting_status IN ('priced', 'unpriced')
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, root.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, root.RunId);
            command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, native.DispatchId);
            command.Parameters.AddWithValue("child", NpgsqlDbType.Varchar, member.Session.SessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
            reportJson = reader.GetString(0);
            reportHash = reader.GetString(1).Trim();
            acknowledgmentsJson = reader.GetString(2);
            ownerRevision = reader.GetInt64(3);
        }

        RuntimeNativeTurnObservation observation;
        ImmutableArray<RuntimeUsageCostReceiptReference> references;
        try
        {
            observation = Deserialize<RuntimeNativeTurnObservation>(reportJson);
            references = Deserialize<ImmutableArray<RuntimeUsageCostReceiptReference>>(acknowledgmentsJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or
                                           InvalidOperationException)
        {
            throw new RuntimeAuthorizationException(
                "runtime_native_turn_accounting_invalid", null, exception);
        }

        RuntimeNativeTurnContract.ValidateObservation(admission, observation);
        if (ownerRevision != native.OwnerRevision ||
            reportHash != native.SourceReportHash ||
            native.SourceReportHash != RuntimeNativeTurnContract.ObservationHash(observation) ||
            observation.OutputHash != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(answer)) ||
            observation.CompletedAt > timeProvider.GetUtcNow() ||
            source.CanonicalPayloadHash != native.SdkSourceReceiptHash)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");

        using (var acknowledgments = JsonDocument.Parse(acknowledgmentsJson))
        {
            if (ChildCheckpointRecoveryManifestCodec.Hash(acknowledgments.RootElement) !=
                native.AcknowledgmentReferencesHash)
                throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        }

        var recorded = new RuntimeNativeTurnRecordedReceipt(
            admission,
            observation,
            native.SourceReportHash
                ?? throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid"),
            checked(ownerRevision - 1));
        RuntimeNativeTurnContract.ValidateRecorded(recorded);
        _ = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, references);
        return new RuntimeChildTurnAccountingEvidence(
            recorded, source.CanonicalPayloadHash, ownerRevision, native.AccountingStatus);
    }

    /// <summary>
    /// Verifies that the dispatch row matches the runtime, source, model, prompt, and request in its admission receipt.
    /// </summary>
    private static void RequireOriginNativeDispatch(
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

    private async Task<NativeChildRebindReceipt?> RequireNativeDispatchBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeDispatch dispatch,
        RuntimeNativeTurnAdmissionReceipt admission,
        RuntimeSdkSourceReceipt source,
        CancellationToken cancellationToken,
        bool requireReleasedHold = true)
    {
        var binding = admission.Registration;
        var receipts = await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, binding.Binding.ProjectId, binding.Binding.RunId,
            admission.MessageId, cancellationToken).ConfigureAwait(false);
        if (receipts.IsEmpty)
        {
            RequireOriginNativeDispatch(dispatch, admission, source.CanonicalPayloadHash);
            return null;
        }

        var receiptIds = new HashSet<Guid>();
        var operationIds = new HashSet<Guid>();
        var priorKeys = new HashSet<string>(StringComparer.Ordinal);
        var destinationFences = new HashSet<long>();
        NativeChildRebindReceipt? previous = null;
        NativeChildRebindReceipt? current = null;
        foreach (var receipt in receipts)
        {
            receipt.Validate();
            if (!receiptIds.Add(receipt.ReceiptId) ||
                !operationIds.Add(receipt.RecoveryOperationId) ||
                !priorKeys.Add(receipt.PriorAuthorityKey) ||
                !destinationFences.Add(receipt.DestinationFence) ||
                receipt.ProjectId != binding.Binding.ProjectId ||
                receipt.RunId != binding.Binding.RunId ||
                receipt.RootSessionId != dispatch.RootSessionId ||
                receipt.ChildSessionId != dispatch.ChildSessionId ||
                receipt.DispatchId != admission.MessageId ||
                receipt.MessageId != admission.MessageId ||
                receipt.PromptHash != dispatch.PromptHash ||
                receipt.OriginalRequestHash != dispatch.RequestHash ||
                receipt.WorkPlanId != dispatch.WorkPlanId ||
                receipt.AssociationId != dispatch.AssociationId ||
                receipt.SelectionHash != dispatch.AcceptedSelectionHash ||
                receipt.DestinationFence <= receipt.SourceFence ||
                receipt.DestinationFence <= 0 ||
                !string.Equals(receipt.SelectionHash, binding.Binding.AcceptedSelectionHash,
                    StringComparison.OrdinalIgnoreCase))
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            var manifest = await ReadNativeRebindManifestAsync(
                connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
            var members = manifest.Members.Where(member =>
                member.AssociationId == receipt.AssociationId &&
                member.Session.SessionId == receipt.ChildSessionId).ToArray();
            if (members.Length != 1)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            var manifestMember = members[0];
            if (manifestMember.NativeOrigin is not { } nativeOrigin ||
                manifestMember.Disposition != "pendingUnsent" ||
                ChildCheckpointRecoveryManifestCodec.Hash(manifest) != receipt.ManifestSha256 ||
                manifest.OperationId != receipt.RecoveryOperationId ||
                manifest.WorkPlanId != receipt.WorkPlanId ||
                manifest.WorkPlanHash != receipt.WorkPlanHash ||
                manifest.WorkflowHash != receipt.WorkflowHash ||
                manifest.TenantId != receipt.TenantId ||
                manifest.Root.ProjectId != receipt.ProjectId ||
                manifest.Root.RunId != receipt.RunId ||
                manifest.Root.SessionId != receipt.RootSessionId ||
                !string.Equals(manifest.SelectionHash, receipt.SelectionHash, StringComparison.OrdinalIgnoreCase) ||
                manifest.SourceCheckpointId != receipt.SourceCheckpointId ||
                manifest.SourceCheckpointRevision != receipt.SourceCheckpointRevision ||
                manifest.SourceCheckpointHash != receipt.SourceCheckpointHash ||
                manifest.SourceExecutionFence != receipt.SourceFence ||
                manifest.SourceDecisionStateVersion != receipt.SourceDecisionStateVersion ||
                manifest.HoldFence != receipt.DestinationFence ||
                manifestMember.MessageId != receipt.MessageId ||
                manifestMember.PromptHash != receipt.PromptHash ||
                manifestMember.AssociationHash != receipt.AssociationHash ||
                manifestMember.WorkItemBindingSha256 != receipt.WorkItemBindingSha256 ||
                (manifestMember.MessageRequestHash ?? nativeOrigin.RequestHash) != receipt.MessageRequestHash ||
                manifestMember.EgressReceiptHash != receipt.EgressReceiptHash ||
                NativeChildRebindOriginFingerprint.Create(manifest, manifestMember).Hash !=
                    receipt.OriginFingerprint ||
                !MatchesNativeOrigin(dispatch, nativeOrigin))
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            var originRegistration = await _registrations.ReadRevisionAsync(
                connection, transaction, nativeOrigin.RuntimeInstanceId, nativeOrigin.RegistrationRevision,
                cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            var originSource = await ReadSourceAsync(
                connection, transaction, nativeOrigin.RuntimeInstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            var receiptRegistration = receipt.RuntimeInstanceId == binding.RuntimeInstanceId &&
                receipt.RegistrationRevision == binding.Revision
                ? binding
                : await _registrations.ReadRevisionAsync(
                    connection, transaction, receipt.RuntimeInstanceId, receipt.RegistrationRevision,
                    cancellationToken).ConfigureAwait(false);
            var receiptSource = receipt.RuntimeInstanceId == source.RuntimeInstanceId &&
                receipt.SdkSourceReceiptHash == source.CanonicalPayloadHash
                ? source
                : (await ReadSourceAsync(
                    connection, transaction, receipt.RuntimeInstanceId, cancellationToken).ConfigureAwait(false))
                    ?.Receipt;
            if (receiptRegistration is null || receiptSource is null ||
                RuntimeContractValidation.RegistrationHash(receiptRegistration) != receipt.RegistrationHash ||
                receiptSource.RuntimeInstanceId != receipt.RuntimeInstanceId ||
                receiptSource.RegistrationRevision != receipt.RegistrationRevision ||
                receiptSource.SourceGrantId != receipt.SourceGrantId ||
                receiptSource.CanonicalPayloadHash != receipt.SdkSourceReceiptHash ||
                originSource.Receipt.CanonicalPayloadHash != nativeOrigin.SdkSourceReceiptHash ||
                !NativeChildRebindOriginFingerprint.HasSameModelAndCostBinding(
                    originRegistration, receiptRegistration, nativeOrigin,
                    originSource.Receipt.Source, receiptSource.Source))
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            if (previous is null)
            {
                if (receipt.PriorAuthorityKey != "origin" ||
                    receipt.PriorReceiptId is not null || receipt.PriorReceiptHash is not null)
                    throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            }
            else if (receipt.PriorAuthorityKey != $"receipt:{previous.ReceiptId:D}" ||
                     receipt.PriorReceiptId != previous.ReceiptId ||
                     receipt.PriorReceiptHash != previous.CanonicalPayloadHash ||
                     receipt.OriginFingerprint != previous.OriginFingerprint ||
                     receipt.OriginalRequestHash != previous.OriginalRequestHash ||
                     receipt.DestinationFence <= previous.DestinationFence)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            if (receipt.Lineage.Length == 0 ||
                receipt.Lineage[^1].OperationId != receipt.RecoveryOperationId ||
                receipt.Lineage[^1].SourceCheckpointId != receipt.SourceCheckpointId ||
                receipt.Lineage[^1].SourceCheckpointRevision != receipt.SourceCheckpointRevision ||
                receipt.Lineage[^1].SourceCheckpointHash != receipt.SourceCheckpointHash ||
                receipt.Lineage[^1].SourceFence != receipt.SourceFence ||
                receipt.Lineage[^1].DestinationCheckpointId != receipt.AnchorCheckpointId ||
                receipt.Lineage[^1].DestinationCheckpointRevision != receipt.AnchorCheckpointRevision ||
                receipt.Lineage[^1].DestinationCheckpointHash != receipt.AnchorCheckpointHash ||
                receipt.Lineage[^1].DestinationFence != receipt.DestinationFence)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            if (receipt.SupersessionReceiptId is { } supersessionId)
            {
                var supersession = await _executionEvidence.ReadCheckpointRecoverySupersessionAsync(
                    connection, transaction, receipt.ProjectId, receipt.RunId,
                    receipt.Lineage[^1].SupersedesOperationId
                        ?? throw new RuntimeAuthorizationException(
                            "checkpoint_recovery_native_rebind_invalid"),
                    cancellationToken).ConfigureAwait(false);
                if (supersession is null || supersession.ReceiptId != supersessionId ||
                    supersession.CanonicalPayloadHash != receipt.SupersessionReceiptHash ||
                    supersession.SuccessorOperationId != receipt.RecoveryOperationId)
                    throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            }
            else if (receipt.SupersessionReceiptHash is not null ||
                     receipt.Lineage[^1].SupersedesOperationId is not null ||
                     receipt.Lineage[^1].SupersessionReceiptId is not null ||
                     receipt.Lineage[^1].SupersessionPayloadHash is not null)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            if (previous is not null && receipt.SourceFence > previous.DestinationFence)
                await RequireNativeChildRebindRecoveryGapAsync(
                    connection, transaction, previous, receipt, manifest, cancellationToken)
                    .ConfigureAwait(false);

            previous = receipt;
            if (receipt.DestinationFence == binding.Binding.ExecutionFence)
                current = receipt;
        }

        var holdIsHeld = requireReleasedHold && await CheckpointRecoveryHold.IsHeldAsync(
            connection, transaction, _schema, binding.Binding.ProjectId, binding.Binding.RunId,
            cancellationToken).ConfigureAwait(false);
        if (current is null || current.RecoveryOperationId != receipts[^1].RecoveryOperationId ||
            current.RuntimeInstanceId != binding.RuntimeInstanceId ||
            current.RegistrationRevision != binding.Revision ||
            current.RegistrationHash != RuntimeContractValidation.RegistrationHash(binding) ||
            current.SdkSourceReceiptHash != source.CanonicalPayloadHash ||
            current.SourceGrantId != source.SourceGrantId ||
            current.EffectiveRequestHash != admission.RequestHash ||
            current.PromptHash != admission.PromptHash ||
            current.RegistrationRevision < 1 ||
            holdIsHeld)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        return current;
    }

    internal async Task<NativeChildRebindReceipt?> ValidateNativeChildRebindChainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration effectiveRegistration,
        ChildRecoveryNativeOrigin lockedOrigin,
        Guid dispatchId,
        bool requireReleasedHold,
        CancellationToken cancellationToken)
    {
        var source = await ReadSourceAsync(
            connection, transaction, effectiveRegistration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, effectiveRegistration, dispatchId, cancellationToken, lockRows: false)
            .ConfigureAwait(false);
        if (!MatchesNativeOrigin(dispatch, lockedOrigin))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_conflict");
        var receipts = await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, effectiveRegistration.Binding.ProjectId,
            effectiveRegistration.Binding.RunId, dispatchId, cancellationToken).ConfigureAwait(false);
        if (receipts.IsEmpty)
            return null;
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            effectiveRegistration, source.Receipt.Source, dispatchId, dispatch.PromptHash,
            receipts[^1].EffectiveRequestHash, dispatch.OwnerRevision);
        return await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source.Receipt, cancellationToken,
            requireReleasedHold).ConfigureAwait(false);
    }

    private async Task RequireNativeChildRebindRecoveryGapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeChildRebindReceipt previousReceipt,
        NativeChildRebindReceipt currentReceipt,
        ChildCheckpointRecoveryManifest currentManifest,
        CancellationToken cancellationToken)
    {
        var recoveryGap = currentReceipt.SourceFence - previousReceipt.DestinationFence;
        var lineage = currentReceipt.Lineage[^1];
        if (recoveryGap < 1 || lineage.SupersedesOperationId is not { } supersededOperationId ||
            lineage.SupersessionReceiptId is not { } supersessionReceiptId ||
            lineage.SupersessionPayloadHash is not { } supersessionPayloadHash ||
            currentReceipt.SupersessionReceiptId != supersessionReceiptId ||
            currentReceipt.SupersessionReceiptHash != supersessionPayloadHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

        var successor = await ReadNativeRecoveryOperationAsync(
            connection, transaction, currentReceipt.ProjectId, currentReceipt.RunId,
            currentReceipt.RecoveryOperationId, cancellationToken).ConfigureAwait(false);
        if (successor.Result.CheckpointRecovery is not { Phase: "ready", DispatchHeld: false } ||
            successor.Manifest.OperationId != currentManifest.OperationId ||
            ChildCheckpointRecoveryManifestCodec.Hash(successor.Manifest) != currentReceipt.ManifestSha256)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

        var blockedCount = 0;
        while (true)
        {
            if (blockedCount >= NativeChildRebindReceipt.MaximumLineageEdges)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            var supersession = await _executionEvidence.ReadCheckpointRecoverySupersessionAsync(
                connection, transaction, currentReceipt.ProjectId, currentReceipt.RunId,
                supersededOperationId, cancellationToken).ConfigureAwait(false);
            if (supersession is null ||
                supersession.ReceiptId != supersessionReceiptId ||
                supersession.CanonicalPayloadHash != supersessionPayloadHash ||
                supersession.SupersedesOperationId != supersededOperationId ||
                supersession.SuccessorOperationId != successor.Result.OperationId ||
                supersession.SuccessorRequestHash != successor.RequestHash ||
                supersession.SuccessorManifestSha256 !=
                    ChildCheckpointRecoveryManifestCodec.Hash(successor.Manifest) ||
                supersession.SuccessorSourceCheckpointId != successor.Manifest.SourceCheckpointId ||
                supersession.SuccessorSourceFence != successor.Manifest.SourceExecutionFence ||
                supersession.SuccessorSourceCheckpointHash != successor.Manifest.SourceCheckpointHash ||
                supersession.TenantId != currentReceipt.TenantId ||
                supersession.ProjectId != currentReceipt.ProjectId ||
                supersession.RunId != currentReceipt.RunId ||
                supersession.RootSessionId != currentReceipt.RootSessionId ||
                supersession.SelectionHash != currentReceipt.SelectionHash ||
                supersession.WorkPlanId != currentReceipt.WorkPlanId ||
                supersession.WorkPlanHash != currentReceipt.WorkPlanHash ||
                supersession.ChildIdentitySha256 !=
                    CheckpointRecoverySupersessionSource.ChildIdentityHash(successor.Manifest))
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            var blocked = await ReadNativeRecoveryOperationAsync(
                connection, transaction, currentReceipt.ProjectId, currentReceipt.RunId,
                supersededOperationId, cancellationToken).ConfigureAwait(false);
            if (blocked.Result.CheckpointRecovery is not
                {
                    Phase: "blocked",
                    CurrentStep: "activation",
                    BoundaryKind: "childJoin",
                    DispatchHeld: true,
                    ReasonCode: "checkpoint_recovery_host_bootstrap_spent" or
                        "runtime_activation_owner_unavailable" or
                        "runtime_activation_owner_timeout" or
                        "runtime_owner_unavailable" or
                        "runtime_owner_timeout"
                } status ||
                blocked.Result.ExecutionFence != status.ExecutionFence ||
                blocked.Result.PreviousExecutionFence + 1 != status.ExecutionFence ||
                status.ChildManifestSha256 != ChildCheckpointRecoveryManifestCodec.Hash(blocked.Manifest) ||
                status.SupersededByOperationId != successor.Result.OperationId ||
                status.SupersededByReceiptId != supersession.ReceiptId ||
                status.SupersededByPayloadHash != supersession.CanonicalPayloadHash ||
                supersession.SupersededRequestHash != blocked.RequestHash ||
                supersession.SupersededManifestSha256 != status.ChildManifestSha256 ||
                supersession.SupersededAdmissionSha256 != ComputeRecoveryAdmissionHash(
                    blocked.Result, status) ||
                supersession.TerminalStatusSha256 != RuntimeContractValidation.Hash(
                    Encoding.UTF8.GetBytes(blocked.TerminalStatusJson)) ||
                supersession.TerminalPhaseVersion != blocked.PhaseVersion ||
                supersession.SupersededCheckpointId != status.RecoveryCheckpointId ||
                supersession.SupersededFence != status.ExecutionFence ||
                supersession.SupersededCheckpointHash != status.RecoveryCheckpointHash ||
                blocked.Manifest.Root.ProjectId != currentReceipt.ProjectId ||
                blocked.Manifest.Root.RunId != currentReceipt.RunId ||
                blocked.Manifest.Root.SessionId != currentReceipt.RootSessionId ||
                blocked.Manifest.TenantId != currentReceipt.TenantId ||
                blocked.Manifest.OperationId != blocked.Result.OperationId ||
                blocked.Manifest.HoldFence != status.ExecutionFence ||
                blocked.Manifest.SourceExecutionFence != blocked.Result.PreviousExecutionFence ||
                blocked.Manifest.WorkPlanId != currentReceipt.WorkPlanId ||
                blocked.Manifest.WorkPlanHash != currentReceipt.WorkPlanHash ||
                blocked.Manifest.OtherNativeWork.Length != 0)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            var members = blocked.Manifest.Members.Where(member =>
                member.AssociationId == currentReceipt.AssociationId &&
                member.Session.SessionId == currentReceipt.ChildSessionId).ToArray();
            if (members.Length != 1 ||
                members[0].Disposition != "pendingUnsent" ||
                members[0].NativeOrigin is not { State: "prepared", OwnerRevision: 1 } ||
                members[0].PriorNativeRebindReceiptId != previousReceipt.ReceiptId ||
                members[0].PriorNativeRebindPayloadHash != previousReceipt.CanonicalPayloadHash ||
                NativeChildRebindOriginFingerprint.Create(blocked.Manifest, members[0]).Hash !=
                    previousReceipt.OriginFingerprint)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

            blockedCount++;
            if (status.SupersedesOperationId is not { } nextSupersededOperationId)
            {
                if (status.SupersessionReceiptId is not null ||
                    status.SupersessionPayloadHash is not null ||
                    blocked.Manifest.SourceExecutionFence != previousReceipt.DestinationFence)
                    throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
                await RequireRecoveryCheckpointDescendantAsync(
                    connection, transaction, currentReceipt, blocked.Manifest, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            if (status.SupersessionReceiptId is not { } nextReceiptId ||
                status.SupersessionPayloadHash is not { } nextPayloadHash)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
            successor = blocked;
            supersededOperationId = nextSupersededOperationId;
            supersessionReceiptId = nextReceiptId;
            supersessionPayloadHash = nextPayloadHash;
        }

        if (blockedCount != recoveryGap)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
    }

    private async Task<NativeRecoveryOperation> ReadNativeRecoveryOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT request_hash, result::text,
                   (result #- ARRAY['checkpointRecovery', 'supersededByOperationId']
                           #- ARRAY['checkpointRecovery', 'supersededByReceiptId']
                           #- ARRAY['checkpointRecovery', 'supersededByPayloadHash'])::text,
                   progress::text, phase_version
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
              AND operation_id = @operation AND operation_kind = 'recovery'
            FOR SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var requestHash = reader.GetString(0).Trim();
        var resultJson = reader.GetString(1);
        var terminalStatusJson = reader.GetString(2);
        var progress = Deserialize<JsonElement>(reader.GetString(3));
        var phaseVersion = reader.GetInt64(4);
        await reader.DisposeAsync().ConfigureAwait(false);
        var result = Deserialize<RunExecutionTransitionResult>(resultJson);
        if (result.OperationId != operationId || result.Session.ProjectId != projectId ||
            result.Session.RunId != runId || result.CheckpointRecovery is null ||
            phaseVersion < 1)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var manifest = CoordinationOwnerStore.ReadCheckpointRecoveryManifest(result, progress);
        return new(requestHash, resultJson, terminalStatusJson, phaseVersion, result, manifest);
    }

    private async Task RequireRecoveryCheckpointDescendantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeChildRebindReceipt previousReceipt,
        ChildCheckpointRecoveryManifest descendant,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            WITH RECURSIVE chain AS (
                SELECT checkpoint_id, parent_checkpoint_id, execution_fence, payload::text,
                       0 AS depth, ARRAY[checkpoint_id]::text[] AS path
                FROM {_checkpoints}
                WHERE project_id = @project AND run_id = @run AND session_id = @root
                  AND store_name = 'coordinator-execution'
                  AND checkpoint_id = @descendant AND execution_fence = @fence
                UNION ALL
                SELECT parent.checkpoint_id, parent.parent_checkpoint_id,
                       parent.execution_fence, parent.payload::text,
                       child.depth + 1, child.path || parent.checkpoint_id
                FROM chain child
                JOIN {_checkpoints} parent
                  ON parent.project_id = @project AND parent.run_id = @run
                 AND parent.session_id = @root
                 AND parent.store_name = 'coordinator-execution'
                 AND parent.execution_fence = @fence
                 AND parent.checkpoint_id = child.parent_checkpoint_id
                WHERE child.checkpoint_id <> @anchor
                  AND child.depth < @maximumDepth
                  AND NOT parent.checkpoint_id = ANY(child.path)
            )
            SELECT checkpoint_id, parent_checkpoint_id, execution_fence, payload, depth
            FROM chain
            ORDER BY depth
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, previousReceipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, previousReceipt.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, previousReceipt.RootSessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, previousReceipt.DestinationFence);
        command.Parameters.AddWithValue("descendant", NpgsqlDbType.Varchar, descendant.SourceCheckpointId);
        command.Parameters.AddWithValue("anchor", NpgsqlDbType.Varchar, previousReceipt.AnchorCheckpointId);
        command.Parameters.AddWithValue("maximumDepth", NpgsqlDbType.Integer,
            NativeChildRebindReceipt.MaximumLineageEdges);
        var checkpoints = new List<(string Id, string? Parent, long Fence, string Payload, int Depth)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                checkpoints.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4)));
        }
        if (checkpoints.Count == 0 || checkpoints.Count > NativeChildRebindReceipt.MaximumLineageEdges + 1 ||
            checkpoints[0].Id != descendant.SourceCheckpointId ||
            checkpoints[0].Fence != previousReceipt.DestinationFence ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(checkpoints[0].Payload)) !=
                descendant.SourceCheckpointHash ||
            checkpoints[^1].Id != previousReceipt.AnchorCheckpointId ||
            checkpoints[^1].Fence != previousReceipt.DestinationFence ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(checkpoints[^1].Payload)) !=
                previousReceipt.AnchorCheckpointHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        for (var index = 1; index < checkpoints.Count; index++)
            if (checkpoints[index].Depth != index ||
                checkpoints[index - 1].Parent != checkpoints[index].Id ||
                checkpoints[index].Fence != previousReceipt.DestinationFence)
                throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
    }

    private static string ComputeRecoveryAdmissionHash(
        RunExecutionTransitionResult result,
        CheckpointRecoveryStatus status) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            operationId = result.OperationId,
            result.PreviousExecutionFence,
            result.ExecutionFence,
            result.PreviousRunStateVersion,
            result.RunStateVersion,
            status.SourceCheckpointId,
            status.SourceCheckpointRevision,
            status.SourceCheckpointHash,
            status.RecoveryCheckpointId,
            status.RecoveryCheckpointRevision,
            status.RecoveryCheckpointHash,
            status.ChildManifestSha256,
            status.BoundaryKind,
            status.GateRequestId,
            status.GateAuthorizedActorId
        }, JsonOptions));

    private async Task<ChildCheckpointRecoveryManifest> ReadNativeRebindManifestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeChildRebindReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT result::text, progress::text
            FROM {_executionOperations}
            WHERE project_id = @project AND run_id = @run
              AND operation_id = @operation AND operation_kind = 'recovery'
            FOR SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, receipt.RecoveryOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        var operation = JsonSerializer.Deserialize<RunExecutionTransitionResult>(
            reader.GetString(0), JsonOptions);
        var progress = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1));
        await reader.DisposeAsync().ConfigureAwait(false);
        if (operation?.CheckpointRecovery is not { Phase: "ready", DispatchHeld: false } status ||
            operation.OperationId != receipt.RecoveryOperationId ||
            operation.Session.ProjectId != receipt.ProjectId ||
            operation.Session.RunId != receipt.RunId ||
            operation.Session.SessionId != receipt.RootSessionId ||
            operation.ExecutionFence != receipt.DestinationFence ||
            status.RecoveryCheckpointId != receipt.AnchorCheckpointId ||
            status.RecoveryCheckpointRevision != receipt.AnchorCheckpointRevision ||
            status.RecoveryCheckpointHash != receipt.AnchorCheckpointHash)
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        return CoordinationOwnerStore.ReadCheckpointRecoveryManifest(operation, progress);
    }

    private static bool MatchesNativeOrigin(NativeDispatch dispatch, ChildRecoveryNativeOrigin native)
    {
        return dispatch.RuntimeInstanceId == native.RuntimeInstanceId &&
            dispatch.RegistrationRevision == native.RegistrationRevision &&
            dispatch.ExecutionFence == native.ExecutionFence &&
            dispatch.AcceptedSelectionHash == native.AcceptedSelectionHash &&
            dispatch.PromptHash == native.PromptHash &&
            dispatch.RequestHash == native.RequestHash &&
            dispatch.BindingHash == native.RuntimeBindingHash &&
            dispatch.SourceHash == native.SdkSourceReceiptHash &&
            dispatch.ModelSelectionReference == native.ModelSelectionReference &&
            dispatch.ModelId == native.SelectedModelId &&
            dispatch.CostBindingHash == native.CostBindingHash &&
            dispatch.IsCostProducing == native.IsCostProducing &&
            dispatch.HardCapSerialized == native.HardCapSerialized &&
            dispatch.CreatedAt == native.CreatedAt;
    }

    /// <summary>
    /// Reads the bound root checkpoint and requires that it still contains this running dispatch intent.
    /// </summary>
    /// <returns>The hash of the exact stored checkpoint payload after all dispatch bindings match.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the checkpoint is no longer latest or its intent, revision, fence, or progress changed.</exception>
    private async Task<string> RequireNativeCheckpointAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeNativeTurnAdmissionReceipt admission, NativeDispatch dispatch, CancellationToken cancellationToken,
        NativeChildRebindReceipt? rebind = null)
    {
        if (rebind is not null)
            return await RequireReboundNativeCheckpointAsync(
                connection, transaction, registration, admission, dispatch, rebind, cancellationToken)
                .ConfigureAwait(false);

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
        return RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(payload));
    }

    private async Task<string> RequireReboundNativeCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeRegistration registration,
        RuntimeNativeTurnAdmissionReceipt admission,
        NativeDispatch dispatch,
        NativeChildRebindReceipt rebind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            WITH RECURSIVE leaves AS (
                SELECT checkpoint.checkpoint_id, checkpoint.parent_checkpoint_id,
                       checkpoint.execution_fence, checkpoint.payload, 0 AS depth
                FROM {_checkpoints} checkpoint
                WHERE checkpoint.project_id = @project AND checkpoint.run_id = @run
                  AND checkpoint.session_id = @root
                  AND checkpoint.store_name = 'coordinator-execution'
                  AND checkpoint.execution_fence = @fence
                  AND NOT EXISTS (
                      SELECT 1 FROM {_checkpoints} child
                      WHERE child.project_id = checkpoint.project_id
                        AND child.run_id = checkpoint.run_id
                        AND child.session_id = checkpoint.session_id
                        AND child.store_name = checkpoint.store_name
                        AND child.parent_checkpoint_id = checkpoint.checkpoint_id)
                ORDER BY checkpoint.checkpoint_id
                LIMIT 2
            ),
            chain AS (
                SELECT checkpoint_id, parent_checkpoint_id, execution_fence, payload, depth,
                       ARRAY[checkpoint_id]::text[] AS path
                FROM leaves
                UNION ALL
                SELECT parent.checkpoint_id, parent.parent_checkpoint_id,
                       parent.execution_fence, parent.payload, child.depth + 1,
                       child.path || parent.checkpoint_id
                FROM chain child
                JOIN {_checkpoints} parent
                  ON parent.project_id = @project AND parent.run_id = @run
                 AND parent.session_id = @root
                 AND parent.store_name = 'coordinator-execution'
                 AND parent.execution_fence = @fence
                 AND parent.checkpoint_id = child.parent_checkpoint_id
                WHERE child.checkpoint_id <> @anchor
                  AND NOT parent.checkpoint_id = ANY(child.path)
            )
            SELECT depth, checkpoint_id, parent_checkpoint_id, execution_fence,
                   CASE WHEN depth = 0 OR checkpoint_id = @anchor THEN payload::text END
            FROM chain
            ORDER BY depth, checkpoint_id
            """, connection, transaction);
        AddNativeScope(command, registration, admission.MessageId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, dispatch.RootSessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
        command.Parameters.AddWithValue("anchor", NpgsqlDbType.Varchar, rebind.AnchorCheckpointId);
        var chain = new List<(int Depth, string Id, string? Parent, long Fence, string? Payload)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                chain.Add((reader.GetInt32(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        if (chain.Count == 0 || chain[0].Depth != 0 ||
            chain.Count(row => row.Depth == 0) != 1 ||
            chain.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != chain.Count)
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
        for (var index = 1; index < chain.Count; index++)
            if (chain[index].Depth != index ||
                chain[index - 1].Parent != chain[index].Id ||
                chain[index].Fence != registration.Binding.ExecutionFence)
                throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
        var anchorIndex = chain.FindIndex(row => row.Id == rebind.AnchorCheckpointId);
        if (anchorIndex < 0 ||
            chain[anchorIndex].Fence != rebind.DestinationFence ||
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(chain[anchorIndex].Payload!))) !=
                rebind.AnchorCheckpointHash)
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
        using var anchorDocument = JsonDocument.Parse(chain[anchorIndex].Payload!);
        var anchorState = MafExecutionCheckpointContract.Deserialize(anchorDocument.RootElement);
        if (anchorState.Revision != rebind.AnchorCheckpointRevision ||
            anchorState.DecisionStateVersion != rebind.AnchorDecisionStateVersion ||
            anchorState.WorkPlanId != rebind.WorkPlanId ||
            anchorState.RecoveryLineage is not { } anchorLineage ||
            anchorLineage.RecoveryOperationId != rebind.RecoveryOperationId ||
            anchorLineage.RecoveryExecutionFence != rebind.DestinationFence)
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");

        using var latestDocument = JsonDocument.Parse(chain[0].Payload!);
        var latest = MafExecutionCheckpointContract.Deserialize(latestDocument.RootElement);
        if (latest.WorkPlanId != rebind.WorkPlanId ||
            latest.DecisionStateVersion != rebind.AnchorDecisionStateVersion ||
            latest.RecoveryLineage != anchorLineage ||
            !latest.PendingDispatches.TryGetValue(rebind.AssociationId, out var pending) ||
            pending.ChildSessionId != dispatch.ChildSessionId ||
            pending.MessageId != admission.MessageId ||
            pending.PromptHash != admission.PromptHash ||
            pending.EgressReceipt is null ||
            ChildCheckpointRecoveryManifestCodec.Hash(pending.EgressReceipt) != rebind.EgressReceiptHash ||
            latest.Results.ContainsKey(rebind.AssociationId) ||
            !IsNativeDispatchRunning(latest, rebind.AssociationId))
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");

        await using var decision = new NpgsqlCommand($"""
            SELECT 1 FROM {_decisions} current
            WHERE current.project_id = @project AND current.run_id = @run
              AND current.session_id = @root
              AND current.execution_fence = @fence
              AND current.state_version = @decision
              AND NOT EXISTS (
                  SELECT 1 FROM {_decisions} next
                  WHERE next.project_id = current.project_id AND next.run_id = current.run_id
                    AND next.session_id = current.session_id
                    AND next.state_version > current.state_version)
            """, connection, transaction);
        AddNativeScope(decision, registration, admission.MessageId);
        decision.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, dispatch.RootSessionId);
        decision.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, registration.Binding.ExecutionFence);
        decision.Parameters.AddWithValue("decision", NpgsqlDbType.Bigint, rebind.AnchorDecisionStateVersion);
        if (await decision.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new RuntimeAuthorizationException("runtime_native_turn_checkpoint_stale");
        return RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(chain[0].Payload!));
    }

    /// <summary>
    /// Reports whether the association is still running in either model-work or fixed-work progress.
    /// </summary>
    private static bool IsNativeDispatchRunning(MafExecutionCheckpoint checkpoint, string associationId) =>
        checkpoint.Progress.WorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Running ||
        checkpoint.Progress.FixedWorkItems.GetValueOrDefault(associationId) == MafExecutionTaskStatus.Running;

    /// <summary>
    /// Adds project, run, and message parameters to native dispatch queries.
    /// </summary>
    private static void AddNativeScope(NpgsqlCommand command, RuntimeRegistration registration, Guid messageId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, registration.Binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, registration.Binding.RunId);
        command.Parameters.AddWithValue("message", NpgsqlDbType.Uuid, messageId);
    }

    /// <summary>
    /// Adds the run stream and exact registration, fence, stage, and request identifiers to a guard query.
    /// </summary>
    private static void AddFixedCreditGuardScope(
        NpgsqlCommand command, RuntimeRegistration registration,
        RuntimeFixedCreditGuardStage stage, Guid requestId)
    {
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, FixedCreditGuardStream(registration));
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Text, registration.RuntimeInstanceId.ToString("D"));
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Text,
            registration.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Text,
            registration.Binding.ExecutionFence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("stage", NpgsqlDbType.Text,
            JsonNamingPolicy.CamelCase.ConvertName(stage.ToString()));
        command.Parameters.AddWithValue("request", NpgsqlDbType.Text, requestId.ToString("D"));
    }

    /// <summary>
    /// Acquires the transaction-scoped execution lock for the root session.
    /// </summary>
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

    /// <summary>
    /// Locked native dispatch row, including the checkpoint and report values needed for admission and reconciliation.
    /// </summary>
    private sealed record NativeDispatch(
        string RootSessionId, string ChildSessionId, Guid RuntimeInstanceId, long RegistrationRevision,
        long ExecutionFence, string BindingHash, string SourceHash, string ModelSelectionReference,
        string ModelId, string PromptHash, string RequestHash, long OwnerRevision, string State,
        string WorkPlanId, string AssociationId, string CheckpointId, long CheckpointRevision,
        long DecisionStateVersion, string? ReportHash, string? ReportJson,
        string AcceptedSelectionHash = "", string CostBindingHash = "",
        bool IsCostProducing = false, bool HardCapSerialized = false, DateTimeOffset CreatedAt = default);

    /// <summary>
    /// Minimal dispatch state and owner revision used to bind a credit-denial decision.
    /// </summary>
    private sealed record NativeDispatchMarker(string State, long OwnerRevision);

    private sealed record NativeRecoveryOperation(
        string RequestHash,
        string ResultJson,
        string TerminalStatusJson,
        long PhaseVersion,
        RunExecutionTransitionResult Result,
        ChildCheckpointRecoveryManifest Manifest);

    /// <summary>
    /// Reconciled cost observation and guard revisions used to decide whether a turn can proceed.
    /// </summary>
    private sealed record NativeCreditDecision(
        decimal ObservedCredits, long LedgerEventCount, string LedgerDigest, string CostIdentity,
        long GuardRevisionBefore, long GuardRevisionAfter, bool HardLimitReached,
        RuntimeObservationV2Fact ObservationFact);

    /// <summary>
    /// Reads and revalidates the SDK source receipt for an active runtime registration.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source is missing or its registration revision or hash is inconsistent.</exception>
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

    /// <summary>
    /// Reads the SDK source receipt and verifies it against the runtime grant and configuration hash.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source receipt does not belong to the supplied grant.</exception>
    internal async Task<RuntimeSdkSourceReceipt> ReadCurrentSourceForGrantAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeGrantReceipt grant, CancellationToken cancellationToken)
    {
        var stored = await ReadSourceAsync(connection, transaction, registration.RuntimeInstanceId, cancellationToken)
            .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        RequireSource(stored, registration, grant);
        return stored.Receipt;
    }

    /// <summary>
    /// Reads a run's credit guard under a shared lock and validates it against the observation intent.
    /// </summary>
    /// <param name="intent">The root, tenant, selection, fence, and configured limits expected by the observer.</param>
    /// <returns>The sealed current snapshot, or <see langword="null"/> when no guard has been recorded.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when guard identity, limits, or required ledger fields conflict.</exception>
    internal async Task<RuntimeCreditGuardSnapshot?> ReadCreditGuardWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        RuntimeCreditGuardIntent intent, CancellationToken cancellationToken)
    {
        RuntimeCreditGuardObservationContract.RequireIntent(intent, registration);
        await using var command = new NpgsqlCommand($"""
            SELECT root_session_id, tenant_id, accepted_selection_hash, execution_fence,
                copilot_soft_credit_limit, copilot_hard_credit_limit, cost_rate_scope_identity,
                cost_unit, cost_scope, copilot_aic_floor,
                accounted_through_event_position, usage_witness_digest, owner_revision,
                soft_warning_emitted, hard_cap_exhausted, updated_at
            FROM {_guards}
            WHERE project_id = @project AND run_id = @run
            FOR SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, intent.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, intent.RunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var softLimit = reader.IsDBNull(4) ? (decimal?)null : reader.GetDecimal(4);
        var hardLimit = reader.IsDBNull(5) ? (decimal?)null : reader.GetDecimal(5);
        var costIdentity = reader.IsDBNull(6) ? null : reader.GetString(6);
        var floor = reader.IsDBNull(9) ? (decimal?)null : reader.GetDecimal(9);
        var ledgerEventCount = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10);
        var ledgerDigest = reader.IsDBNull(11) ? null : reader.GetString(11);
        if (reader.GetString(0) != intent.RootSessionId ||
            reader.GetString(1) != intent.TenantId ||
            reader.GetString(2) != intent.AcceptedSelectionHash ||
            reader.GetInt64(3) != intent.ExecutionFence ||
            softLimit != intent.SoftCreditLimit ||
            hardLimit != intent.HardCreditLimit ||
            costIdentity is null || floor is null || ledgerEventCount is null || ledgerDigest is null)
            throw new RuntimeAuthorizationException("runtime_credit_guard_intent_conflict");

        var snapshot = RuntimeCreditGuardObservationContract.Seal(new(
            1, intent, RuntimeContractValidation.RegistrationHash(registration), costIdentity,
            reader.GetString(7), reader.GetString(8), floor.Value, ledgerEventCount.Value,
            ledgerDigest, reader.GetInt64(12), reader.GetBoolean(13), reader.GetBoolean(14),
            reader.GetFieldValue<DateTimeOffset>(15), string.Empty));
        RuntimeCreditGuardObservationContract.ValidateSnapshot(snapshot, registration, intent);
        return snapshot;
    }

    /// <summary>
    /// Reads a completed source report and its selection hash after checking its manifest and source receipt bindings.
    /// </summary>
    /// <returns>The validated source-completion manifest and selection hash, or <see langword="null"/> when no eligible report exists.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when persisted completion metadata is inconsistent.</exception>
    internal async Task<(UsageDispatchSourceCompletionManifest Manifest, string SelectionHash)?>
        ReadSourceCompletionAsync(
            string projectId, string runId, string sessionId, Guid dispatchId,
            CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var rebinds = await _executionEvidence.ReadNativeChildRebindsAsync(
            connection, transaction, projectId, runId, dispatchId, cancellationToken).ConfigureAwait(false);
        RuntimeSdkSourceReceipt source;

        if (rebinds.IsEmpty)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT completion.source_completion_manifest_json::text,
                       completion.source_completion_manifest_hash,
                       completion.runtime_instance_id, completion.registration_revision,
                       completion.execution_fence, completion.accepted_selection_hash,
                       source.receipt_json::text, source.canonical_payload_hash
                FROM {_dispatches} completion
                JOIN {_sources} source
                  ON source.runtime_instance_id = completion.runtime_instance_id
                 AND source.registration_revision = completion.registration_revision
                 AND source.canonical_payload_hash = completion.sdk_source_receipt_hash
                WHERE completion.project_id = @project AND completion.run_id = @run
                  AND completion.child_session_id = @session AND completion.dispatch_id = @dispatch
                  AND completion.source_report_status = 'source-complete'
                  AND completion.source_completion_manifest_json IS NOT NULL
                """, connection, transaction);
            AddSourceCompletionScope(command, projectId, runId, sessionId, dispatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var manifest = Deserialize<UsageDispatchSourceCompletionManifest>(reader.GetString(0));
            source = Deserialize<RuntimeSdkSourceReceipt>(reader.GetString(6));
            UsageDispatchSourceCompletionManifestContract.Validate(manifest);
            if (manifest.DispatchId != dispatchId.ToString("D") || manifest.ProjectId != projectId ||
                manifest.RunId != runId || manifest.SessionId != sessionId ||
                manifest.ReceiptDigest != reader.GetString(1) ||
                manifest.RuntimeInstanceId != reader.GetGuid(2) ||
                manifest.RegistrationRevision != reader.GetInt64(3) ||
                manifest.ExecutionFence != reader.GetInt64(4) ||
                source.RuntimeInstanceId != manifest.RuntimeInstanceId ||
                source.RegistrationRevision != manifest.RegistrationRevision ||
                source.CanonicalPayloadHash != reader.GetString(7))
                throw new RuntimeAuthorizationException("runtime_usage_source_completion_invalid");
            var selectionHash = reader.GetString(5).Trim();
            if (source.Source.SourceMode != "hosted-copilot" ||
                source.Source.MeterSource != SdkMeterSources.CopilotNanoAiu)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (manifest, selectionHash);
        }

        var latestReceipt = rebinds[^1];
        var effectiveRegistration = await ReadNativeRebindRegistrationAsync(
            connection, transaction, latestReceipt, cancellationToken).ConfigureAwait(false);
        var storedSource = await ReadSourceAsync(
            connection, transaction, effectiveRegistration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_sdk_source_unknown");
        source = storedSource.Receipt;
        if (source.RegistrationRevision != effectiveRegistration.Revision ||
            source.CanonicalPayloadHash != latestReceipt.SdkSourceReceiptHash ||
            source.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(
                effectiveRegistration, source.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");

        var dispatch = await ReadNativeDispatchAsync(
            connection, transaction, effectiveRegistration, dispatchId, cancellationToken, lockRows: false)
            .ConfigureAwait(false);
        if (dispatch.ChildSessionId != sessionId)
            throw new RuntimeAuthorizationException("runtime_usage_source_completion_invalid");
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            effectiveRegistration, source.Source, dispatchId, dispatch.PromptHash,
            latestReceipt.EffectiveRequestHash, dispatch.OwnerRevision);
        var effective = await RequireNativeDispatchBindingAsync(
            connection, transaction, dispatch, admission, source, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");

        await using (var command = new NpgsqlCommand($"""
            SELECT source_completion_manifest_json::text, source_completion_manifest_hash,
                   accepted_selection_hash
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND child_session_id = @session
              AND dispatch_id = @dispatch AND source_report_status = 'source-complete'
              AND source_completion_manifest_json IS NOT NULL
            """, connection, transaction))
        {
            AddSourceCompletionScope(command, projectId, runId, sessionId, dispatchId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            var manifest = Deserialize<UsageDispatchSourceCompletionManifest>(reader.GetString(0));
            UsageDispatchSourceCompletionManifestContract.Validate(manifest);
            var selectionHash = reader.GetString(2).Trim();
            if (manifest.DispatchId != dispatchId.ToString("D") ||
                manifest.ProjectId != projectId || manifest.RunId != runId ||
                manifest.SessionId != sessionId || manifest.ReceiptDigest != reader.GetString(1) ||
                manifest.RuntimeInstanceId != effective.RuntimeInstanceId ||
                manifest.RegistrationRevision != effective.RegistrationRevision ||
                manifest.ExecutionFence != effective.DestinationFence ||
                source.RuntimeInstanceId != manifest.RuntimeInstanceId ||
                source.RegistrationRevision != manifest.RegistrationRevision ||
                source.CanonicalPayloadHash != effective.SdkSourceReceiptHash ||
                !string.Equals(selectionHash, effective.SelectionHash, StringComparison.OrdinalIgnoreCase))
                throw new RuntimeAuthorizationException("runtime_usage_source_completion_invalid");
            if (source.Source.SourceMode != "hosted-copilot" ||
                source.Source.MeterSource != SdkMeterSources.CopilotNanoAiu)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (manifest, selectionHash);
        }
    }

    private async Task<RuntimeRegistration> ReadNativeRebindRegistrationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        NativeChildRebindReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_links::text
            FROM {_schema}.runtime_run_start_intents
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND root_session_id = @session AND accepted_selection_hash = @selection
              AND execution_fence = @fence AND state = 'ready' AND current_step = 'ready'
              AND NOT cleanup_obligation
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, receipt.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, receipt.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, receipt.RunId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, receipt.ChildSessionId);
        command.Parameters.AddWithValue("selection", NpgsqlDbType.Char, receipt.SelectionHash);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, receipt.DestinationFence);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        using var links = JsonDocument.Parse(json);
        var registration = links.RootElement.GetProperty("registration").Deserialize<RuntimeRegistration>(JsonOptions)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() ||
            registration.RuntimeInstanceId != receipt.RuntimeInstanceId ||
            registration.Revision != receipt.RegistrationRevision ||
            RuntimeContractValidation.RegistrationHash(registration) != receipt.RegistrationHash ||
            registration.Binding.ProjectId != receipt.ProjectId ||
            registration.Binding.RunId != receipt.RunId ||
            registration.Binding.SessionId != receipt.ChildSessionId ||
            registration.Binding.ExecutionFence != receipt.DestinationFence ||
            !string.Equals(registration.Binding.AcceptedSelectionHash, receipt.SelectionHash,
                StringComparison.OrdinalIgnoreCase))
            throw new RuntimeAuthorizationException("checkpoint_recovery_native_rebind_invalid");
        return registration;
    }

    private static void AddSourceCompletionScope(
        NpgsqlCommand command, string projectId, string runId, string sessionId, Guid dispatchId)
    {
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, sessionId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
    }

    /// <summary>
    /// Loads the source receipt and database metadata for a runtime ID.
    /// </summary>
    internal async Task<StoredSource?> ReadSourceAsync(
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

    /// <summary>
    /// Requires that source facts, grant ID, configuration hash, and registration revision still match.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source was registered for different runtime facts or grant.</exception>
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

    /// <summary>
    /// Returns the current UTC timestamp rounded down to the persisted 10-tick precision.
    /// </summary>
    private DateTimeOffset RecordedAt()
    {
        var now = timeProvider.GetUtcNow();
        return new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
    }

    /// <summary>
    /// Deserializes a stored runtime receipt using strict JSON settings.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when stored JSON does not contain the expected receipt.</exception>
    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native runtime source receipt is invalid.");

    /// <summary>
    /// Adds the canonical hash, serialized receipt, and timestamp parameters to a receipt insert.
    /// </summary>
    private static void AddReceipt<T>(NpgsqlCommand command, string hash, T receipt, DateTimeOffset recordedAt)
    {
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, hash);
        command.Parameters.AddWithValue("receipt", NpgsqlDbType.Varchar, JsonSerializer.Serialize(receipt, JsonOptions));
        command.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, recordedAt);
    }

    /// <summary>
    /// The SDK source receipt paired with the configuration hash used to authorize it.
    /// </summary>
    internal sealed record StoredSource(RuntimeSdkSourceReceipt Receipt, string ConfigurationHash);
}
