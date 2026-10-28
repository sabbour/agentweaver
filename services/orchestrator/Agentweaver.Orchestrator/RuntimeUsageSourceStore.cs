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
    private readonly string _guards = $"\"{options.Schema}\".maf_execution_run_guards";
    private readonly string _outboxEvents = $"\"{options.Schema}\".outbox_events";
    private readonly PostgresOutbox _outbox = new(dataSource, options.Schema);
    private readonly SpendBudgetExposureStore _spendBudgets = new(dataSource, options, timeProvider);
    private readonly RuntimeExecutionEvidenceLedgerStore _executionEvidence = new(options.Schema);

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
        var rows = new List<RuntimeObservationDispatch>(RuntimeObservationDispatchCollection.MaximumItems + 1);
        await using var command = new NpgsqlCommand($"""
            SELECT dispatch_id, root_session_id, child_session_id, work_plan_id, association_id,
                checkpoint_id, checkpoint_revision, execution_fence, owner_revision, dispatch_state,
                source_report_status, accounting_status, usage_witness_watermark, usage_witness_digest,
                created_at, updated_at
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND root_session_id = @root
                AND child_session_id = @session AND runtime_instance_id = @runtime
                AND registration_revision = @registrationRevision AND execution_fence = @fence
                AND accepted_selection_hash = @selectionHash
            ORDER BY created_at DESC, dispatch_id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        command.Parameters.AddWithValue("root", NpgsqlDbType.Varchar, rootSessionId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, binding.SessionId);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, registration.RuntimeInstanceId);
        command.Parameters.AddWithValue("registrationRevision", NpgsqlDbType.Bigint, registration.Revision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, binding.ExecutionFence);
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        command.Parameters.AddWithValue(
            "limit", NpgsqlDbType.Integer, RuntimeObservationDispatchCollection.MaximumItems + 1);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RuntimeObservationDispatch(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetFieldValue<DateTimeOffset>(15)));
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
        var checkpointHash = await RequireNativeCheckpointAsync(
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
            var creditDecision = await ReconcileNativeCreditLimitsAsync(
                connection, transaction, registration, source.Receipt.Source, dispatch.RootSessionId,
                snapshot, cancellationToken).ConfigureAwait(false);
            if (creditDecision.HardLimitReached)
            {
                var denial = await RecordNativeCreditDenialAsync(
                    connection, transaction, registration, source.Receipt, message.MessageId,
                    RuntimeCreditLimitDenialStage.NativeTurnBegin, checkpointHash, creditDecision,
                    dispatch.State, dispatch.OwnerRevision, cancellationToken).ConfigureAwait(false);
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
        await using (var update = new NpgsqlCommand($"""
            UPDATE {_guards}
            SET copilot_aic_floor = @floor, accounted_through_event_position = @ledgerEventCount,
                usage_witness_digest = @digest, soft_warning_emitted = @warning,
                hard_cap_exhausted = @exhausted, owner_revision = owner_revision + 1,
                updated_at = clock_timestamp()
            WHERE project_id = @project AND run_id = @run AND owner_revision = @revision
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
                    ledgerEventCount = snapshot.CopilotTotals.Events, costIdentity
                }), timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        return new(observed, snapshot.CopilotTotals.Events, digest, costIdentity,
            revision, nextRevision, hardExhausted || limits.HardReached);
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
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>>? readSpendEvidence = null)
    {
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
        // The dispatch must belong to the checkpoint that is stored for this run.
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, admission, prepared, cancellationToken).ConfigureAwait(false);
        var marker = await ReadNativeDispatchMarkerAsync(
            connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
        if (marker is not null)
        {
            // A dispatch row already exists for this message, for example from a retried request.
            // Accept it only when it describes the same work and is still the first prepared revision.
            // A different row is a conflict; a row that already moved past "prepared" means the
            // earlier attempt may have been sent, so the result is indeterminate.
            var existingDispatch = await ReadNativeDispatchAsync(
                connection, transaction, registration, intent.MessageId, cancellationToken).ConfigureAwait(false);
            RequireNativeDispatch(existingDispatch, admission, source.CanonicalPayloadHash);
            if (existingDispatch.RootSessionId != prepared.RootSessionId ||
                existingDispatch.WorkPlanId != prepared.WorkPlanId ||
                existingDispatch.AssociationId != prepared.AssociationId ||
                existingDispatch.CheckpointId != prepared.CheckpointId ||
                existingDispatch.CheckpointRevision != prepared.CheckpointRevision ||
                existingDispatch.DecisionStateVersion != prepared.DecisionStateVersion)
                throw new RuntimeAuthorizationException("runtime_native_turn_conflict");
            if (existingDispatch.State != "prepared" || existingDispatch.OwnerRevision != 1)
                throw new RuntimeAuthorizationException("runtime_native_turn_indeterminate");
            marker = new(existingDispatch.State, existingDispatch.OwnerRevision);
        }
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
            // A reached hard credit limit returns a denial instead of throwing, so the denial receipt
            // is committed. The method returns before spend budget admission and before the dispatch
            // insert, so the denied dispatch holds no reservation and is never sent.
            if (creditDecision.HardLimitReached)
                return new(await RecordNativeCreditDenialAsync(
                    connection, transaction, registration, source, intent.MessageId,
                    RuntimeCreditLimitDenialStage.Preparation, checkpointHash, creditDecision,
                    marker?.State ?? "absent", marker?.OwnerRevision ?? 0, cancellationToken)
                    .ConfigureAwait(false), null);
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
        // Read the stored row back. It must match the dispatch built above and still be the first
        // prepared revision; anything else means another writer changed it.
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
        return await _executionEvidence.AppendNumericLimitDenialAsync(
            connection, transaction, receipt, cancellationToken).ConfigureAwait(false);
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
        RequireNativeDispatch(dispatch, request.Admission, source.Receipt.CanonicalPayloadHash);
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
    /// Locks the active root and dispatch rows and returns the complete native-dispatch snapshot.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the root or dispatch is unavailable under the current fence.</exception>
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
        var expectedAdmission = new RuntimeNativeTurnAdmissionReceipt(
            registration, source.Receipt.Source, request.Denial.DispatchId, dispatch.PromptHash,
            dispatch.RequestHash, checked(dispatch.OwnerRevision + 1));
        RequireNativeDispatch(dispatch, expectedAdmission, source.Receipt.CanonicalPayloadHash);
        if (dispatch.State != "prepared" ||
            dispatch.OwnerRevision != request.Denial.DispatchRevisionAfter)
            throw new RuntimeAuthorizationException("runtime_native_credit_denial_conflict");
        var checkpointHash = await RequireNativeCheckpointAsync(
            connection, transaction, registration, expectedAdmission, dispatch, cancellationToken)
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
        RequireNativeDispatch(dispatch, admission, source.CanonicalPayloadHash);
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
            connection, transaction, registration, admission, dispatch, cancellationToken).ConfigureAwait(false);
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
                SELECT 1 FROM {_dispatches}
                WHERE project_id = @project AND run_id = @run AND dispatch_id <> @message
                    AND runtime_instance_id = @runtime
                    AND source_report_json->'source'->>'sdkSessionId' = @sdk_session
                    AND (source_report_json->'usageEventIds') ?| @events)
            """, connection, transaction);
        AddNativeScope(command, admission.Registration, admission.MessageId);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, admission.Source.RuntimeInstanceId);
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

    /// <summary>
    /// Verifies that the dispatch row matches the runtime, source, model, prompt, and request in its admission receipt.
    /// </summary>
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

    /// <summary>
    /// Reads the bound root checkpoint and requires that it still contains this running dispatch intent.
    /// </summary>
    /// <returns>The hash of the exact stored checkpoint payload after all dispatch bindings match.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the checkpoint is no longer latest or its intent, revision, fence, or progress changed.</exception>
    private async Task<string> RequireNativeCheckpointAsync(
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
        return RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(payload));
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
        long DecisionStateVersion, string? ReportHash, string? ReportJson);

    /// <summary>
    /// Minimal dispatch state and owner revision used to bind a credit-denial decision.
    /// </summary>
    private sealed record NativeDispatchMarker(string State, long OwnerRevision);

    /// <summary>
    /// Reconciled cost observation and guard revisions used to decide whether a turn can proceed.
    /// </summary>
    private sealed record NativeCreditDecision(
        decimal ObservedCredits, long LedgerEventCount, string LedgerDigest, string CostIdentity,
        long GuardRevisionBefore, long GuardRevisionAfter, bool HardLimitReached);

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

    /// <summary>
    /// Loads the source receipt and database metadata for a runtime ID.
    /// </summary>
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
    private sealed record StoredSource(RuntimeSdkSourceReceipt Receipt, string ConfigurationHash);
}
