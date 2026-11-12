using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

internal sealed record RuntimeObservationV2ReadScope(
    string Issuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string RootSessionId,
    string SessionId,
    string SelectionHash,
    string SelectionRevision,
    long CurrentFence,
    long SessionFence,
    string AuthorizationHash);

internal sealed record RuntimeObservationV2StoreResult(
    RuntimeObservationV2History History,
    RuntimeObservationV2Totals Totals,
    RuntimeObservationSection<RuntimeObservationCreditGuard> CreditGuard,
    RuntimeObservationV2Fact? RecordedBindingFact);

internal sealed class RuntimeObservationV2Store(NpgsqlDataSource dataSource, string schema)
{
    private const string EventType = "orchestrator.runtime_observation.fact";
    private const int EventVersion = 1;
    private const int CursorLifetimeHours = 24;
    private readonly string _cursors = $"\"{schema}\".runtime_observation_v2_cursors";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly string _schema = $"\"{schema}\"";
    private readonly PostgresOutbox _outbox = new(dataSource, schema);

    internal static string StreamId(string projectId, string runId) =>
        $"coordination/{projectId}/{runId}/runtime-limit-observations";

    internal static RuntimeObservationV2Fact DenialFact(RuntimeNumericLimitDenialReceipt denial)
    {
        denial.Validate();
        return new RuntimeObservationV2Fact(
            denial.ReceiptId, null, RuntimeObservationFactKind.Denied,
            denial.ProjectId, denial.RunId, denial.AttemptId, denial.DispatchId,
            denial.SessionId, denial.ExecutionFence, denial.RuntimeInstanceId,
            denial.RegistrationRevision, denial.AcceptedSelectionHash, denial.OwnerRevision,
            denial.LimitKind == RuntimeNumericLimitKind.ModelTurns
                ? RuntimeObservationLimitKind.ModelTurns
                : RuntimeObservationLimitKind.ToolCalls,
            denial.AcceptedLimit, denial.ObservedCount, "count",
            denial.LimitKind == RuntimeNumericLimitKind.ModelTurns ? "modelTurns" : "toolCalls",
            denial.WorkPlanId is null ? "rootAuthorization" : "childPreparation",
            "limit_reached", null, null, denial.RecordedAt,
            new(RuntimeObservationSectionState.Available, null, denial.ReceiptId,
                denial.CanonicalPayloadHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable(
                denial.SessionId == denial.RootSessionId ? "native_witness_missing" : "no_native_boundary_witness"),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_rejection_not_observed"),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_execution_not_observed"))
        {
            CheckpointId = denial.CheckpointId,
            CheckpointRevision = denial.CheckpointRevision,
            CheckpointHash = denial.CheckpointHash,
            CheckpointDecisionStateVersion = denial.CheckpointDecisionStateVersion,
            WorkPlanId = denial.WorkPlanId,
            AssociationId = denial.AssociationId
        }.Validate();
    }

    internal static RuntimeObservationV2Fact NativeNoSendFact(
        RuntimeNumericLimitNativeNoSendReceipt receipt,
        RuntimeNumericLimitDenialReceipt denial)
    {
        receipt.Validate();
        denial.Validate();
        if (receipt.ProjectId != denial.ProjectId || receipt.RunId != denial.RunId ||
            receipt.AttemptId != denial.AttemptId || receipt.DenialReceiptId != denial.ReceiptId ||
            receipt.DenialPayloadHash != denial.CanonicalPayloadHash ||
            receipt.SessionId != denial.SessionId || receipt.ExecutionFence != denial.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_native_no_send_conflict");
        return (DenialFact(denial) with
        {
            FactId = receipt.ReceiptId,
            Sequence = null,
            Kind = RuntimeObservationFactKind.NativeNoSend,
            RecordedAt = receipt.RecordedAt,
            OwnerDecision = new(RuntimeObservationSectionState.Available, null,
                denial.ReceiptId, denial.CanonicalPayloadHash, null, null),
            NativeNoSend = new RuntimeObservationEvidenceLink(RuntimeObservationSectionState.Available, null,
                receipt.ReceiptId, receipt.CanonicalPayloadHash, receipt.RangeComplete,
                receipt.ObservedEventCount).Validate()
        }).Validate();
    }

    internal static RuntimeObservationV2Fact CreditDenialFact(
        RuntimeRegistration registration,
        RuntimeNativeCreditDenialReceipt denial)
    {
        var binding = registration.Binding;
        var factId = CreditDenialFactId(denial);
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.Denied,
            binding.ProjectId, binding.RunId, denial.DispatchId, denial.DispatchId,
            binding.SessionId, denial.ExecutionFence, denial.RuntimeInstanceId,
            denial.RegistrationRevision, binding.AcceptedSelectionHash,
            denial.DispatchRevisionBefore == 0 ? null : denial.DispatchRevisionBefore,
            RuntimeObservationLimitKind.CopilotHardCredits,
            denial.HardCreditLimit, denial.ObservedCredits, "nanoAiu", "copilotRun",
            denial.Stage == RuntimeCreditLimitDenialStage.Preparation
                ? "childPreparation" : "nativeTurnBegin",
            "hard_credit_limit_reached", null, null, denial.RecordedAt,
            new(RuntimeObservationSectionState.Available, null, factId,
                denial.CanonicalPayloadHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable(
                denial.Stage == RuntimeCreditLimitDenialStage.Preparation
                    ? "no_native_boundary_witness" : "native_witness_missing"),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_rejection_not_observed"),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_execution_not_observed")).Validate();
    }

    internal static RuntimeObservationV2Fact CreditNoSendFact(
        RuntimeRegistration registration,
        RuntimeNativeCreditDenialReceipt denial,
        RuntimeNativeTurnNoSendReceipt receipt)
    {
        RuntimeContractValidation.ValidateHash(receipt.CanonicalPayloadHash);
        if (receipt.DispatchId != denial.DispatchId ||
            receipt.DenialReceiptHash != denial.CanonicalPayloadHash ||
            receipt.RuntimeInstanceId != denial.RuntimeInstanceId ||
            receipt.RegistrationRevision != denial.RegistrationRevision ||
            !receipt.RangeComplete || receipt.ObservedEventCount != 0 ||
            receipt.BeforeCursorHash != receipt.AfterCursorHash)
            throw new RuntimeAuthorizationException("runtime_native_turn_no_send_invalid");
        var fact = CreditDenialFact(registration, denial);
        return (fact with
        {
            FactId = CreditNoSendFactId(denial),
            Kind = RuntimeObservationFactKind.NativeNoSend,
            RecordedAt = receipt.VerifiedAt,
            NativeNoSend = new RuntimeObservationEvidenceLink(RuntimeObservationSectionState.Available, null,
                CreditNoSendFactId(denial), receipt.CanonicalPayloadHash,
                receipt.RangeComplete, receipt.ObservedEventCount).Validate()
        }).Validate();
    }

    internal static Guid CreditDenialFactId(RuntimeNativeCreditDenialReceipt denial) =>
        StableGuid($"native-credit-denial\0{denial.RuntimeInstanceId:D}\0{denial.DispatchId:D}\0" +
            $"{denial.Stage}\0{denial.GuardRevisionAfter}");

    internal static Guid CreditNoSendFactId(RuntimeNativeCreditDenialReceipt denial) =>
        StableGuid($"native-turn-no-send\0{denial.CanonicalPayloadHash}");

    internal static RuntimeObservationV2Fact CoverageStartedFact(
        string projectId,
        string runId,
        string rootSessionId,
        long executionFence,
        string selectionHash,
        DateTimeOffset recordedAt)
    {
        var factId = StableGuid($"runtime-observation-coverage\0{projectId}\0{runId}");
        var normalizedSelectionHash = selectionHash.ToLowerInvariant();
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.CoverageStarted,
            projectId, runId, null, null, rootSessionId, executionFence, null, null,
            normalizedSelectionHash, null, null, null, null, null, null, "runAcceptance",
            "observation_coverage_started", null, null, recordedAt,
            new(RuntimeObservationSectionState.Available, null, factId, normalizedSelectionHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable("not_a_native_attempt"),
            RuntimeObservationEvidenceLink.Unavailable("not_applicable"),
            RuntimeObservationEvidenceLink.Unavailable("not_observed")).Validate();
    }

    internal static RuntimeObservationV2Fact ModelTurnReservedFact(
        RuntimeRegistration registration,
        Guid attemptId,
        Guid dispatchId,
        long ownerRevision,
        long reservedCount,
        string stage,
        string ownerPayloadHash,
        DateTimeOffset recordedAt)
    {
        var binding = registration.Binding;
        var factId = StableGuid($"model-turn-reservation\0{binding.ProjectId}\0{binding.RunId}\0{dispatchId:D}");
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.ModelTurnReserved,
            binding.ProjectId, binding.RunId, attemptId, dispatchId, binding.SessionId,
            binding.ExecutionFence, registration.RuntimeInstanceId, registration.Revision,
            binding.AcceptedSelectionHash, ownerRevision, RuntimeObservationLimitKind.ModelTurns,
            binding.MaxModelTurns, reservedCount, "count", "copilotRun", stage, null,
            null, reservedCount, recordedAt,
            new(RuntimeObservationSectionState.Available, null, dispatchId, ownerPayloadHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable("native_send_not_proven"),
            RuntimeObservationEvidenceLink.Unavailable("not_applicable"),
            RuntimeObservationEvidenceLink.Unavailable("not_observed")).Validate();
    }

    internal static RuntimeObservationV2Fact ToolInvocationReservedFact(
        RuntimeRegistration registration,
        Guid attemptId,
        Guid dispatchId,
        long ownerRevision,
        long reservedCount,
        string ownerPayloadHash,
        string permissionPayloadHash,
        DateTimeOffset recordedAt)
    {
        var binding = registration.Binding;
        var factId = StableGuid($"tool-invocation-reservation\0{binding.ProjectId}\0{binding.RunId}\0{dispatchId:D}");
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.ToolInvocationReserved,
            binding.ProjectId, binding.RunId, attemptId, dispatchId, binding.SessionId,
            binding.ExecutionFence, registration.RuntimeInstanceId, registration.Revision,
            binding.AcceptedSelectionHash, ownerRevision, RuntimeObservationLimitKind.ToolCalls,
            binding.MaxToolCalls, reservedCount, "count", "copilotRun", "toolInvocationAdmission",
            null, null, reservedCount, recordedAt,
            new(RuntimeObservationSectionState.Available, null, dispatchId, ownerPayloadHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable("not_a_native_model_attempt"),
            new(RuntimeObservationSectionState.Available, null, dispatchId, permissionPayloadHash, null, null),
            RuntimeObservationEvidenceLink.Unavailable("native_tool_execution_not_observed")).Validate();
    }

    internal static RuntimeObservationV2Fact CreditGuardFact(
        RuntimeRegistration registration,
        string rootSessionId,
        decimal observedFloor,
        long revision,
        long ledgerEventCount,
        string ledgerDigest,
        bool softWarning,
        bool hardExhausted,
        DateTimeOffset recordedAt)
    {
        var binding = registration.Binding;
        var factId = StableGuid(
            $"runtime-credit-guard\0{binding.ProjectId}\0{binding.RunId}\0{revision}");
        return new RuntimeObservationV2Fact(
            factId, null, RuntimeObservationFactKind.CreditGuardChanged,
            binding.ProjectId, binding.RunId, null, null, rootSessionId,
            binding.ExecutionFence, registration.RuntimeInstanceId, registration.Revision,
            binding.AcceptedSelectionHash, revision, null, null, observedFloor,
            "nanoAiu", "copilotRun", "guardUpdate",
            softWarning ? "soft_warning_or_floor_update" :
                hardExhausted ? "hard_limit_reached" : "floor_update",
            null, null, recordedAt,
            new(RuntimeObservationSectionState.Available, null, factId, ledgerDigest, null, null),
            RuntimeObservationEvidenceLink.Unavailable("not_a_native_attempt"),
            RuntimeObservationEvidenceLink.Unavailable("not_applicable"),
            RuntimeObservationEvidenceLink.Unavailable("not_observed"))
        {
            AccountedThroughLedgerEventCount = ledgerEventCount,
            SoftWarningEmitted = softWarning,
            HardCapExhausted = hardExhausted
        }.Validate();
    }

    internal async Task AppendAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string runId,
        RuntimeObservationV2Fact fact,
        CancellationToken cancellationToken)
    {
        fact.Validate();
        if (fact.Sequence is not null || fact.ProjectId != projectId || fact.RunId != runId)
            throw new RuntimeAuthorizationException("runtime_observation_v2_fact_invalid");
        var payload = JsonSerializer.SerializeToElement(fact, JsonOptions);
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            fact.FactId,
            StreamId(projectId, runId),
            $"runtime-observation-fact/{fact.FactId:D}",
            EventType,
            EventVersion,
            payload,
            fact.RecordedAt), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeObservationV2StoreResult> ReadAsync(
        RuntimeObservationV2ReadScope scope,
        int pageSize,
        string? cursor,
        Guid? attemptId,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > RuntimeObservationV2History.MaximumItems ||
            attemptId == Guid.Empty || cursor is not null && attemptId is not null)
            throw new RuntimeAuthorizationException("runtime_observation_v2_query_invalid");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var streamId = StreamId(scope.ProjectId, scope.RunId);
        var head = await ReadHeadAsync(connection, transaction, streamId, cancellationToken).ConfigureAwait(false);
        var state = cursor is null
            ? new CursorState(1, scope, pageSize, head, 0, Guid.NewGuid())
            : await DecodeCursorAsync(
                connection, transaction, cursor, scope, pageSize, head, cancellationToken)
                .ConfigureAwait(false);

        List<RuntimeObservationV2Fact> items;
        string? nextCursor = null;
        string? exactUnavailableReason = null;
        if (attemptId is { } exactAttempt)
        {
            items = await ReadAttemptAsync(
                connection, transaction, streamId, exactAttempt, scope, state.HighWatermark,
                RuntimeObservationV2History.MaximumItems, cancellationToken).ConfigureAwait(false);
            if (items.Count > RuntimeObservationV2History.MaximumItems)
            {
                items = items.Take(RuntimeObservationV2History.MaximumItems).ToList();
                nextCursor = await StoreCursorAsync(
                    connection, transaction, state with
                    {
                        AfterSequence = items[0].Sequence!.Value - 1
                    }, cancellationToken).ConfigureAwait(false);
                exactUnavailableReason = "attempt_history_exceeds_bounded_aggregate";
            }
            else if (items.Count == 0)
            {
                exactUnavailableReason = "attempt_evidence_not_retained";
            }
        }
        else
        {
            items = await ReadPageAsync(
                connection, transaction, streamId, scope, state, pageSize, cancellationToken)
                .ConfigureAwait(false);
            if (items.Count > pageSize)
            {
                items = items.Take(pageSize).ToList();
                nextCursor = await StoreCursorAsync(
                    connection, transaction, state with { AfterSequence = items[^1].Sequence!.Value },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var creditGuard = await ReadCreditGuardAsync(
            connection, transaction, scope, streamId, state.HighWatermark, cancellationToken)
            .ConfigureAwait(false);
        var bindingFact = await ReadLatestSessionBindingFactAsync(
            connection, transaction, scope, streamId, state.HighWatermark, cancellationToken)
            .ConfigureAwait(false);
        var (coverageStart, totals) = await ReadCoverageAndTotalsAsync(
            connection, transaction, streamId, state.HighWatermark, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        const string incompleteReason = "legacy_or_uninstrumented_history";
        var complete = coverageStart is not null;
        var history = new RuntimeObservationV2History(
            state.SnapshotId,
            state.HighWatermark,
            complete,
            coverageStart,
            complete ? null : incompleteReason,
            attemptId is not null,
            items,
            nextCursor,
            exactUnavailableReason).Validate();
        return new RuntimeObservationV2StoreResult(history, totals, creditGuard, bindingFact);
    }

    private async Task<List<RuntimeObservationV2Fact>> ReadPageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string streamId,
        RuntimeObservationV2ReadScope scope,
        CursorState state,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text, sequence
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND event_type = @eventType AND event_version = @eventVersion
              AND sequence > @after AND sequence <= @high
              AND (@rootScope OR payload->>'sessionId' = @session)
            ORDER BY sequence
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType);
        command.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, EventVersion);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Bigint, state.AfterSequence);
        command.Parameters.AddWithValue("high", NpgsqlDbType.Bigint, state.HighWatermark);
        command.Parameters.AddWithValue("rootScope", NpgsqlDbType.Boolean, scope.SessionId == scope.RootSessionId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Text, scope.SessionId);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);
        return await ReadFactsAsync(command, scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<RuntimeObservationV2Fact>> ReadAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string streamId,
        Guid attemptId,
        RuntimeObservationV2ReadScope scope,
        long highWatermark,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text, sequence
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND event_type = @eventType AND event_version = @eventVersion
              AND sequence <= @high AND payload->>'attemptId' = @attempt
              AND (@rootScope OR payload->>'sessionId' = @session)
            ORDER BY sequence
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType);
        command.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, EventVersion);
        command.Parameters.AddWithValue("high", NpgsqlDbType.Bigint, highWatermark);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Text, attemptId.ToString("D"));
        command.Parameters.AddWithValue("rootScope", NpgsqlDbType.Boolean, scope.SessionId == scope.RootSessionId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Text, scope.SessionId);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, pageSize + 1);
        var items = await ReadFactsAsync(command, scope, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
            items = await ReadLegacyNumericDenialAsync(
                connection, transaction, attemptId, scope, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task<(DateTimeOffset? CoverageStart, RuntimeObservationV2Totals Totals)>
        ReadCoverageAndTotalsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string streamId,
            long highWatermark,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT
                min((payload->>'recordedAt')::timestamptz)
                    FILTER (WHERE payload->>'kind' = 'coverageStarted'),
                count(*) FILTER (WHERE payload->>'kind' = 'modelTurnReserved'),
                count(*) FILTER (WHERE payload->>'kind' = 'toolInvocationReserved'),
                count(*) FILTER (WHERE payload->>'kind' = 'coverageStarted')
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND event_type = @eventType
              AND event_version = @eventVersion AND sequence <= @high
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType);
        command.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, EventVersion);
        command.Parameters.AddWithValue("high", NpgsqlDbType.Bigint, highWatermark);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_observation_v2_coverage_unavailable");
        if (reader.GetInt64(3) == 0)
        {
            var unknown = new RuntimeObservationCount(
                RuntimeObservationSectionState.Unknown, null, "legacy_or_uninstrumented_history");
            return (null, new RuntimeObservationV2Totals(
                unknown, unknown, unknown, unknown, unknown, unknown).Validate());
        }

        var coverageStart = reader.GetFieldValue<DateTimeOffset>(0);
        var reservedModels = new RuntimeObservationCount(
            RuntimeObservationSectionState.Available, reader.GetInt64(1), null);
        var reservedTools = new RuntimeObservationCount(
            RuntimeObservationSectionState.Available, reader.GetInt64(2), null);
        var unavailableNative = new RuntimeObservationCount(
            RuntimeObservationSectionState.Unavailable, null, "native_send_proof_not_complete");
        var unavailableConsumed = new RuntimeObservationCount(
            RuntimeObservationSectionState.Unavailable, null, "consumption_proof_not_complete");
        var unavailableTools = new RuntimeObservationCount(
            RuntimeObservationSectionState.Unavailable, null, "native_tool_execution_not_observed");
        return (coverageStart, new RuntimeObservationV2Totals(
            reservedModels, unavailableConsumed, unavailableNative, unavailableNative,
            reservedTools, unavailableTools).Validate());
    }

    private static async Task<List<RuntimeObservationV2Fact>> ReadFactsAsync(
        NpgsqlCommand command,
        RuntimeObservationV2ReadScope scope,
        CancellationToken cancellationToken)
    {
        var items = new List<RuntimeObservationV2Fact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var fact = JsonSerializer.Deserialize<RuntimeObservationV2Fact>(
                reader.GetString(0), JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_observation_v2_fact_corrupt");
            fact = fact with { Sequence = reader.GetInt64(1) };
            if (fact.SessionId != scope.SessionId && scope.SessionId != scope.RootSessionId)
                continue;
            items.Add(fact.Validate());
        }
        return items;
    }

    private async Task<List<RuntimeObservationV2Fact>> ReadLegacyNumericDenialAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid attemptId,
        RuntimeObservationV2ReadScope scope,
        CancellationToken cancellationToken)
    {
        var items = new List<RuntimeObservationV2Fact>();
        await using var command = new NpgsqlCommand($"""
            SELECT receipt_id, receipt_json::text
            FROM {_schema}.maf_runtime_evidence_receipts
            WHERE project_id = @project AND run_id = @run AND attempt_id = @attempt
              AND receipt_kind IN ('numeric-limit-denial', 'numeric-limit-native-no-send')
            ORDER BY created_at, receipt_kind
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, scope.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, scope.RunId);
        command.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, attemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        RuntimeNumericLimitDenialReceipt? denial = null;
        RuntimeNumericLimitNativeNoSendReceipt? noSend = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(1);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("limitKind", out _))
                denial = JsonSerializer.Deserialize<RuntimeNumericLimitDenialReceipt>(json, JsonOptions)?.Validate();
            else
                noSend = JsonSerializer.Deserialize<RuntimeNumericLimitNativeNoSendReceipt>(json, JsonOptions)?.Validate();
        }
        if (denial is null || denial.SessionId != scope.SessionId && scope.SessionId != scope.RootSessionId)
            return items;

        var linkedNative = noSend is not null && noSend.DenialReceiptId == denial.ReceiptId &&
            noSend.DenialPayloadHash == denial.CanonicalPayloadHash &&
            noSend.AttemptId == denial.AttemptId &&
            noSend.SessionId == denial.SessionId &&
            noSend.ExecutionFence == denial.ExecutionFence
            ? new RuntimeObservationEvidenceLink(
                RuntimeObservationSectionState.Available,
                null,
                noSend.ReceiptId,
                noSend.CanonicalPayloadHash,
                noSend.RangeComplete,
                noSend.ObservedEventCount).Validate()
            : RuntimeObservationEvidenceLink.Unavailable(
                denial.SessionId == denial.RootSessionId ? "native_witness_missing" : "no_native_boundary_witness");
        var fact = DenialFact(denial);
        items.Add(fact with { NativeNoSend = linkedNative });
        _ = items[^1].Validate();
        return items;
    }

    private async Task<RuntimeObservationSection<RuntimeObservationCreditGuard>> ReadCreditGuardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeObservationV2ReadScope scope,
        string streamId,
        long highWatermark,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND event_type = @eventType AND event_version = @eventVersion
              AND sequence <= @high AND payload->>'kind' = 'creditGuardChanged'
            ORDER BY sequence DESC
            LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType);
        command.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, EventVersion);
        command.Parameters.AddWithValue("high", NpgsqlDbType.Bigint, highWatermark);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new RuntimeObservationSection<RuntimeObservationCreditGuard>(RuntimeObservationSectionState.Unknown, null);
        var fact = JsonSerializer.Deserialize<RuntimeObservationV2Fact>(reader.GetString(0), JsonOptions)
            ?.Validate()
            ?? throw new RuntimeAuthorizationException("runtime_observation_v2_fact_corrupt");
        if (fact.Kind != RuntimeObservationFactKind.CreditGuardChanged ||
            fact.SessionId != scope.RootSessionId ||
            !fact.AcceptedSelectionHash.Equals(scope.SelectionHash, StringComparison.OrdinalIgnoreCase) ||
            fact.ExecutionFence != scope.CurrentFence)
            return new RuntimeObservationSection<RuntimeObservationCreditGuard>(RuntimeObservationSectionState.Unavailable, null);
        var guard = new RuntimeObservationCreditGuard(
            fact.ObservedValue!.Value,
            fact.AccountedThroughLedgerEventCount!.Value,
            fact.OwnerDecision.PayloadHash!,
            fact.OwnerRevision!.Value,
            fact.SoftWarningEmitted!.Value,
            fact.HardCapExhausted!.Value,
            fact.RecordedAt).Validate();
        return new RuntimeObservationSection<RuntimeObservationCreditGuard>(RuntimeObservationSectionState.Available, guard).Validate();
    }

    private async Task<RuntimeObservationV2Fact?> ReadLatestSessionBindingFactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RuntimeObservationV2ReadScope scope,
        string streamId,
        long highWatermark,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT payload::text, sequence
            FROM {_schema}.outbox_events
            WHERE stream_id = @stream AND event_type = @eventType AND event_version = @eventVersion
              AND sequence <= @high
              AND payload->>'sessionId' = @session
              AND payload->>'runtimeInstanceId' IS NOT NULL
              AND payload->>'registrationRevision' IS NOT NULL
              AND payload->>'executionFence' = @fence
              AND lower(payload->>'acceptedSelectionHash') = lower(@selectionHash)
            ORDER BY sequence DESC
            LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        command.Parameters.AddWithValue("eventType", NpgsqlDbType.Text, EventType);
        command.Parameters.AddWithValue("eventVersion", NpgsqlDbType.Integer, EventVersion);
        command.Parameters.AddWithValue("high", NpgsqlDbType.Bigint, highWatermark);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Text, scope.SessionId);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Text, scope.SessionFence.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("selectionHash", NpgsqlDbType.Text, scope.SelectionHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var fact = JsonSerializer.Deserialize<RuntimeObservationV2Fact>(reader.GetString(0), JsonOptions)
            ?? throw new RuntimeAuthorizationException("runtime_observation_v2_fact_corrupt");
        return (fact with { Sequence = reader.GetInt64(1) }).Validate();
    }

    private async Task<long> ReadHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string streamId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT last_sequence FROM {_schema}.outbox_streams WHERE stream_id = @stream
            """, connection, transaction);
        command.Parameters.AddWithValue("stream", NpgsqlDbType.Text, streamId);
        return (long?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0;
    }

    private async Task<CursorState> DecodeCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cursor,
        RuntimeObservationV2ReadScope scope,
        int pageSize,
        long committedHead,
        CancellationToken cancellationToken)
    {
        if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out var cursorId))
            throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_invalid");
        string? storedJson;
        DateTimeOffset expiresAt;
        try
        {
            await using var command = new NpgsqlCommand($"""
                SELECT cursor_state::text, expires_at
                FROM {_cursors}
                WHERE cursor_id = @cursor AND project_id = @project AND run_id = @run
                """, connection, transaction);
            command.Parameters.AddWithValue("cursor", NpgsqlDbType.Uuid, cursorId);
            command.Parameters.AddWithValue("project", NpgsqlDbType.Text, scope.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Text, scope.RunId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_stale");
            storedJson = reader.GetString(0);
            expiresAt = reader.GetFieldValue<DateTimeOffset>(1);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            _ = exception;
            throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_invalid");
        }
        CursorState state;
        try
        {
            state = JsonSerializer.Deserialize<CursorState>(storedJson, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_invalid");
        }
        if (expiresAt <= DateTimeOffset.UtcNow)
            throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_stale");
        if (state.Version != 1 || state.PageSize != pageSize ||
            state.HighWatermark < 0 || state.HighWatermark > committedHead ||
            state.AfterSequence < 0 || state.AfterSequence > state.HighWatermark ||
            state.SnapshotId == Guid.Empty || state.Scope != scope)
            throw new RuntimeAuthorizationException("runtime_observation_v2_cursor_stale");
        return state;
    }

    private async Task<string> StoreCursorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CursorState state,
        CancellationToken cancellationToken)
    {
        var cursorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var cleanup = new NpgsqlCommand($"""
            DELETE FROM {_cursors}
            WHERE project_id = @project AND run_id = @run AND expires_at <= @now
            """, connection, transaction))
        {
            cleanup.Parameters.AddWithValue("project", NpgsqlDbType.Text, state.Scope.ProjectId);
            cleanup.Parameters.AddWithValue("run", NpgsqlDbType.Text, state.Scope.RunId);
            cleanup.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_cursors} (cursor_id, project_id, run_id, cursor_state, expires_at)
            VALUES (@cursor, @project, @run, @state, @expires)
            """, connection, transaction);
        insert.Parameters.AddWithValue("cursor", NpgsqlDbType.Uuid, cursorId);
        insert.Parameters.AddWithValue("project", NpgsqlDbType.Text, state.Scope.ProjectId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Text, state.Scope.RunId);
        insert.Parameters.AddWithValue("state", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(state, JsonOptions));
        insert.Parameters.AddWithValue(
            "expires", NpgsqlDbType.TimestampTz, now.AddHours(CursorLifetimeHours));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return cursorId.ToString("N");
    }

    private static Guid StableGuid(string value) =>
        Guid.ParseExact(RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(value))[..32], "N");

    private sealed record CursorState(
        int Version,
        RuntimeObservationV2ReadScope Scope,
        int PageSize,
        long HighWatermark,
        long AfterSequence,
        Guid SnapshotId);
}
