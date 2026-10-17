using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

/// <summary>Reports an expected reconciliation mismatch and the affected source receipts.</summary>
internal sealed class NativeUsageReconciliationException(
    string code, ImmutableArray<Guid> sourceReceiptIds = default) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
    internal ImmutableArray<Guid> SourceReceiptIds { get; } = sourceReceiptIds;
}

/// <summary>The pinned binding and priceability result for a native source preflight.</summary>
internal sealed record NativeUsageCostPreflightResult(
    CostBinding? Binding,
    bool IsPriced,
    string? UnpricedReason);

/// <summary>Admits owner-issued receipts and maintains run-scoped cost bindings transactionally.</summary>
internal sealed class NativeUsageReceiptConsumer(
    NpgsqlDataSource dataSource, PostgresSessionsProviderOptions options, PostgresUsageLedger ledger,
    CopilotCostProvider? provider = null, ICostProviderBinder? bindings = null)
{
    private const string InboxConsumer = "events.native-sdk-usage.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly PostgresOutbox _inbox = new(dataSource, options.Schema);
    private readonly string _usageLedger = $"\"{options.Schema}\".usage_ledger";
    private readonly string _receipts = $"\"{options.Schema}\".usage_source_receipts";
    private readonly string _bindings = $"\"{options.Schema}\".usage_run_cost_bindings";
    private readonly string _ledger = $"\"{options.Schema}\".usage_ledger";

    /// <summary>Pins Copilot cost identity and returns the current quote and run totals for admission.</summary>
    internal async Task<RuntimeRunAdmissionReceipt> ReadRunAdmissionAsync(
        string tenantId, string projectId, string runId, RuntimeRunAdmissionRequest request,
        JsonElement selection, RuntimeAcceptedModelSelection model, RuntimeModelBindingPin modelPin,
        Func<CancellationToken, Task> revalidateCurrentAuthority, CancellationToken cancellationToken)
    {
        RuntimeRunAdmissionContract.ValidateRequest(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireCostLockAsync(connection, transaction, tenantId, projectId, runId,
            SdkMeterSources.CopilotNanoAiu, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(connection, transaction,
            tenantId, projectId, runId, SdkMeterSources.CopilotNanoAiu, cancellationToken).ConfigureAwait(false);
        var quote = Quote(modelPin.ModelId, runId, pinned);
        var totals = await ledger.GetCopilotRunTotalsWithinTransactionAsync(
            connection, transaction, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var receipt = new RuntimeRunAdmissionReceipt(
            1, tenantId, projectId, runId, request.AcceptedSelectionHash, model, modelPin, pinned.Binding, quote, totals);
        RuntimeRunAdmissionContract.ValidateReceipt(receipt, request, tenantId, projectId, runId, selection);
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>Verifies a complete priced receipt set and returns totals at its accounting revision.</summary>
    internal async Task<RuntimeUsageCostReconciliationReceipt> ReconcileAsync(
        string tenantId,
        string projectId,
        string runId,
        RuntimeUsageCostReconciliationRequest request,
        Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        RuntimeUsageCostReconciliationContract.ValidateRequest(request);
        UsageLedgerValidation.ValidateRunScope(tenantId, projectId, runId);
        ArgumentNullException.ThrowIfNull(revalidateCurrentAuthority);

        var scope = new CostRunScope(tenantId, projectId, runId, request.MeterSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var pinned = await ReadPinnedBindingAsync(connection, transaction, scope, cancellationToken)
            .ConfigureAwait(false);
        if (pinned.Binding is not { } costBinding || pinned.UnpricedReason is not null ||
            costBinding.MeterSource != request.MeterSource ||
            costBinding.RateCard.Id != request.RateCardId ||
            costBinding.RateCard.Version != request.RateCardVersion ||
            costBinding.RateCard.Unit != request.RateCardUnit)
            throw new NativeUsageReconciliationException("runtime_usage_reconciliation_binding_mismatch");

        var references = await ReadCostReceiptReferencesAsync(
            connection, transaction, tenantId, projectId, runId, request, costBinding, cancellationToken)
            .ConfigureAwait(false);
        var totals = await ledger.GetCopilotRunTotalsWithinTransactionAsync(
            connection, transaction, tenantId, projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        if (totals.Events < request.MinimumAccountingRevision)
            throw new NativeUsageReconciliationException("runtime_usage_reconciliation_stale");
        var receipt = new RuntimeUsageCostReconciliationReceipt(
            1,
            request.CostTurnId,
            request.RuntimeInstanceId,
            request.RegistrationRevision,
            request.ExecutionFence,
            tenantId,
            projectId,
            runId,
            request.SessionId,
            request.RuntimeTurnId,
            request.AcceptedSelectionHash,
            request.ModelSelectionReference,
            request.ModelId,
            request.MeterSource,
            request.RateCardId,
            request.RateCardVersion,
            request.RateCardUnit,
            totals.Events,
            references,
            totals);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>Pins a meter's binding and returns a quote or stable reason when pricing is unavailable.</summary>
    internal async Task<NativeUsageCostPreflightResult> PreflightAsync(
        string tenantId,
        string projectId,
        string runId,
        string meterSource,
        string verifiedModelId,
        Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(tenantId);
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        RuntimeContractValidation.ValidateIdentifier(meterSource);
        RuntimeContractValidation.ValidateIdentifier(verifiedModelId);
        ArgumentNullException.ThrowIfNull(revalidateCurrentAuthority);

        var scope = new CostRunScope(tenantId, projectId, runId, meterSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireCostLockAsync(connection, transaction,
            scope.TenantId, scope.ProjectId, scope.RunId, scope.MeterSource, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(
            connection, transaction, scope.TenantId, scope.ProjectId, scope.RunId,
            scope.MeterSource, cancellationToken).ConfigureAwait(false);
        var result = QuotePreflight(meterSource, verifiedModelId, runId, pinned);
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Idempotently consumes a source receipt and writes its ledger entry and acknowledgment together.</summary>
    internal async Task<RuntimeUsageAccountingAcknowledgment> AppendAsync(
        RuntimeUsageSourceReceipt receipt, Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        var sourceJson = JsonSerializer.Serialize(receipt, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var usage = receipt.Usage;
        await AcquireCostLockAsync(connection, transaction, usage.Attribution.TenantId,
            usage.Attribution.ProjectId, usage.Attribution.RunId, usage.ModelBinding.MeterSource,
            cancellationToken).ConfigureAwait(false);
        var admission = await _inbox.AdmitAsync(
            connection, transaction, InboxConsumer, receipt.ReceiptId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        RuntimeUsageAccountingAcknowledgment acknowledgment;
        if (admission == InboxAdmission.Duplicate)
        {
            await using var read = new NpgsqlCommand($"""
                SELECT source_receipt_json, source_payload_hash, accounting_receipt_json
                FROM {_receipts} WHERE source_receipt_id = @receipt
                """, connection, transaction);
            read.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("An admitted native usage inbox has no accounting receipt.");
            var stored = Deserialize<RuntimeUsageSourceReceipt>(reader.GetString(0));
            if (stored != receipt || reader.GetString(1) != receipt.CanonicalPayloadHash)
                throw new UsageLedgerConflictException("The source receipt identity has a different immutable payload.");
            acknowledgment = new(receipt.ReceiptId, Deserialize<UsageAccountingReceipt>(reader.GetString(2)), true);
        }
        else
        {
            var pinned = await ReadOrPinBindingAsync(connection, transaction, usage.Attribution.TenantId,
                usage.Attribution.ProjectId, usage.Attribution.RunId, usage.ModelBinding.MeterSource, cancellationToken)
                .ConfigureAwait(false);
            var price = Price(usage, pinned);
            var appended = await ledger.AppendWithinTransactionAsync(
                connection, transaction, usage, pinned.Binding, price, cancellationToken).ConfigureAwait(false);
            acknowledgment = new(receipt.ReceiptId, appended.Receipt, false);
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO {_receipts}
                    (source_receipt_id, event_id, source_payload_hash, source_receipt_json, accounting_receipt_json)
                VALUES (@receipt, @event, @hash, @source, @accounting)
                """, connection, transaction);
            insert.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, receipt.ReceiptId);
            insert.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
            insert.Parameters.AddWithValue("hash", NpgsqlDbType.Char, receipt.CanonicalPayloadHash);
            insert.Parameters.AddWithValue("source", NpgsqlDbType.Varchar, sourceJson);
            insert.Parameters.AddWithValue("accounting", NpgsqlDbType.Varchar,
                JsonSerializer.Serialize(acknowledgment.Accounting, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return acknowledgment;
    }

    /// <summary>Validates required accounting receipts and snapshots ledger totals in one transaction.</summary>
    internal async Task<RuntimeUsageCostSnapshotReceipt> ReadCostSnapshotAsync(
        RuntimeUsageCostSnapshotRequest request, Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken, RuntimeNativeTurnRecordedReceipt? verifiedNativeTurn = null)
    {
        RuntimeUsageCostSnapshotContract.ValidateRequest(request);
        if (request.NativeTurn is not null)
        {
            if (verifiedNativeTurn is null)
                throw new RuntimeAuthorizationException("runtime_native_turn_evidence_required");
            RuntimeNativeTurnContract.ValidateAccountingSnapshot(request, verifiedNativeTurn);
        }
        else if (verifiedNativeTurn is not null)
            throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
        var scope = request.Registration.Binding;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireCostLockAsync(connection, transaction, scope.TenantId, scope.ProjectId, scope.RunId,
            request.Source.MeterSource, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(connection, transaction,
            scope.TenantId, scope.ProjectId, scope.RunId, request.Source.MeterSource, cancellationToken)
            .ConfigureAwait(false);
        var quote = Quote(request.Source.ModelId, scope.RunId, pinned);
        var represented = await ReadRequiredReceiptsAsync(
            connection, transaction, request, verifiedNativeTurn, cancellationToken)
            .ConfigureAwait(false);
        var totals = await ledger.GetCopilotRunTotalsWithinTransactionAsync(
            connection, transaction, scope.TenantId, scope.ProjectId, scope.RunId, cancellationToken)
            .ConfigureAwait(false);
        var receipt = new RuntimeUsageCostSnapshotReceipt(1,
            RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source),
            pinned.Binding, quote, totals)
        {
            DispatchId = request.DispatchId, RepresentedReceipts = represented
        };
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(receipt, request);
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    private async Task<ImmutableArray<RuntimeUsageCostReceiptReference>> ReadRequiredReceiptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeUsageCostSnapshotRequest request,
        RuntimeNativeTurnRecordedReceipt? verifiedNativeTurn, CancellationToken cancellationToken)
    {
        var represented = ImmutableArray.CreateBuilder<RuntimeUsageCostReceiptReference>();
        foreach (var reference in request.RequiredReceipts)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT source.source_receipt_json, source.source_payload_hash, source.accounting_receipt_json,
                    ledger.canonical_input_hash, (ledger.payload->'usage')::text,
                    ledger.price_disposition, ledger.price_amount, ledger.price_unit, ledger.unpriced_reason,
                    ledger.rate_card_id, ledger.rate_card_version, ledger.recorded_at
                FROM {_receipts} source
                JOIN {_ledger} ledger ON ledger.event_id = source.event_id
                WHERE source.source_receipt_id = @receipt AND source.event_id = @event
                """, connection, transaction);
            command.Parameters.AddWithValue("receipt", NpgsqlDbType.Uuid, reference.SourceReceiptId);
            command.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, reference.Accounting.EventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new RuntimeAuthorizationException("runtime_usage_accounting_pending");
            var source = Deserialize<RuntimeUsageSourceReceipt>(reader.GetString(0));
            RuntimeUsageSourceReceiptContract.Validate(source);
            var accounting = Deserialize<UsageAccountingReceipt>(reader.GetString(2));
            var usage = Deserialize<UsageSubmission>(reader.GetString(4));
            if (source.ReceiptId != reference.SourceReceiptId || source.Registration != request.Registration ||
                source.Usage.SdkSource != request.Source ||
                (source.Usage.A2AMessageId != request.DispatchId &&
                    (verifiedNativeTurn is null || source.Usage.A2AMessageId is not null)) ||
                verifiedNativeTurn is not null &&
                    source.Usage.OccurredAt > verifiedNativeTurn.Observation.CompletedAt ||
                source.CanonicalPayloadHash != reader.GetString(1) ||
                usage != source.Usage || accounting != reference.Accounting ||
                accounting.EventId != usage.EventId || accounting.Attribution != usage.Attribution ||
                accounting.CanonicalPayloadHash != reader.GetString(3) ||
                accounting.Disposition.ToString() != reader.GetString(5) ||
                accounting.Amount != (reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6)) ||
                accounting.Unit != (reader.IsDBNull(7) ? null : reader.GetString(7)) ||
                accounting.UnpricedReason != (reader.IsDBNull(8) ? null : reader.GetString(8)) ||
                accounting.RateCardId != (reader.IsDBNull(9) ? null : reader.GetString(9)) ||
                accounting.RateCardVersion != (reader.IsDBNull(10) ? null : reader.GetString(10)) ||
                accounting.RecordedAt != reader.GetFieldValue<DateTimeOffset>(11))
                throw new RuntimeAuthorizationException("runtime_usage_accounting_mismatch");
            represented.Add(new(source.ReceiptId, accounting));
        }
        return represented.ToImmutable();
    }

    /// <summary>Matches every requested receipt to its source proof, ledger row, and pinned binding.</summary>
    private async Task<ImmutableArray<RuntimeUsageCostReceiptReference>> ReadCostReceiptReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        string projectId,
        string runId,
        RuntimeUsageCostReconciliationRequest request,
        CostBinding pinnedBinding,
        CancellationToken cancellationToken)
    {
        var required = request.RequiredReceipts.ToDictionary(reference => reference.SourceReceiptId);
        var represented = ImmutableArray.CreateBuilder<RuntimeUsageCostReceiptReference>(required.Count);
        var found = new HashSet<Guid>();
        var mismatched = ImmutableArray.CreateBuilder<Guid>();
        await using var read = new NpgsqlCommand($"""
            SELECT r.source_receipt_id, r.event_id, r.source_payload_hash,
                   r.source_receipt_json, r.accounting_receipt_json,
                   l.canonical_input_hash, l.price_disposition, l.price_amount, l.price_unit,
                   l.rate_card_id, l.rate_card_version, l.rate_card_unit, l.cost_binding
            FROM {_receipts} r
            JOIN {_usageLedger} l ON l.event_id = r.event_id
            WHERE r.source_receipt_id = ANY(@receipts)
              AND l.tenant_id = @tenant AND l.project_id = @project AND l.run_id = @run
              AND l.session_id = @session AND l.meter_source = @meter
            ORDER BY r.source_receipt_id
            """, connection, transaction);
        read.Parameters.AddWithValue("receipts", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            request.RequiredReceipts.Select(reference => reference.SourceReceiptId).ToArray());
        read.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        read.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        read.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        read.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, request.SessionId);
        read.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, request.MeterSource);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sourceReceiptId = reader.GetGuid(0);
            found.Add(sourceReceiptId);
            var source = Deserialize<RuntimeUsageSourceReceipt>(reader.GetString(3));
            var accounting = Deserialize<UsageAccountingReceipt>(reader.GetString(4));
            var expected = required[sourceReceiptId];
            var ledgerBinding = reader.IsDBNull(12)
                ? null
                : Deserialize<CostBinding>(reader.GetString(12));
            if (!MatchesReceipt(
                    sourceReceiptId, reader.GetGuid(1), reader.GetString(2), source, accounting,
                    expected, reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    ledgerBinding, pinnedBinding, tenantId, projectId, runId, request))
            {
                mismatched.Add(sourceReceiptId);
                continue;
            }
            // Unpriced is valid accounting state and remains represented without becoming zero cost.
            represented.Add(new(sourceReceiptId, accounting));
        }
        await reader.CloseAsync().ConfigureAwait(false);

        var missing = required.Keys.Where(receiptId => !found.Contains(receiptId)).ToImmutableArray();
        if (!missing.IsEmpty)
            throw new NativeUsageReconciliationException(
                "runtime_usage_reconciliation_incomplete", missing);
        if (mismatched.Count > 0)
            throw new NativeUsageReconciliationException(
                "runtime_usage_reconciliation_mismatch", mismatched.ToImmutable());
        return represented.ToImmutable();
    }

    /// <summary>Checks the full scope and immutable accounting fields for one reconciliation receipt.</summary>
    private static bool MatchesReceipt(
        Guid sourceReceiptId,
        Guid eventId,
        string sourcePayloadHash,
        RuntimeUsageSourceReceipt source,
        UsageAccountingReceipt accounting,
        RuntimeUsageCostReceiptReference expected,
        string accountingPayloadHash,
        string priceDisposition,
        decimal? priceAmount,
        string? priceUnit,
        string? rateCardId,
        string? rateCardVersion,
        string? rateCardUnit,
        CostBinding? ledgerBinding,
        CostBinding pinnedBinding,
        string tenantId,
        string projectId,
        string runId,
        RuntimeUsageCostReconciliationRequest request)
    {
        RuntimeUsageSourceReceiptContract.Validate(source);
        var binding = source.Registration.Binding;
        var usage = source.Usage;
        var sourceFacts = usage.SdkSource;
        return source.ReceiptId == sourceReceiptId &&
            source.CanonicalPayloadHash == sourcePayloadHash &&
            source.CanonicalPayloadHash == RuntimeUsageSourceReceiptContract.Hash(
                source.Registration, source.Usage) &&
            usage.EventId == eventId && accounting.EventId == eventId &&
            accounting == expected.Accounting &&
            accounting.CanonicalPayloadHash == accountingPayloadHash &&
            accounting.Disposition.ToString() == priceDisposition &&
            accounting.Amount == priceAmount && accounting.Unit == priceUnit &&
            accounting.RateCardId == rateCardId && accounting.RateCardVersion == rateCardVersion &&
            binding.TenantId == tenantId && binding.ProjectId == projectId && binding.RunId == runId &&
            binding.SessionId == request.SessionId && binding.TurnId == request.RuntimeTurnId &&
            binding.ExecutionFence == request.ExecutionFence &&
            binding.AcceptedSelectionHash == request.AcceptedSelectionHash &&
            binding.ModelSelectionReference == request.ModelSelectionReference &&
            binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
            source.Registration.RuntimeInstanceId == request.RuntimeInstanceId &&
            source.Registration.Revision == request.RegistrationRevision &&
            sourceFacts is not null && sourceFacts.AcceptedSelectionHash == request.AcceptedSelectionHash &&
            sourceFacts.ModelSelectionReference == request.ModelSelectionReference &&
            sourceFacts.ModelId == request.ModelId && sourceFacts.MeterSource == request.MeterSource &&
            usage.A2AMessageId == request.CostTurnId &&
            usage.Attribution.TenantId == tenantId && usage.Attribution.ProjectId == projectId &&
            usage.Attribution.RunId == runId && usage.Attribution.SessionId == request.SessionId &&
            usage.Attribution.TurnId == request.RuntimeTurnId &&
            usage.ModelBinding.ModelReference == request.ModelSelectionReference &&
            usage.ModelBinding.ModelId == request.ModelId && usage.ModelBinding.MeterSource == request.MeterSource &&
            rateCardId == request.RateCardId && rateCardVersion == request.RateCardVersion &&
            rateCardUnit == request.RateCardUnit &&
            ledgerBinding is not null && CostBindingsEqual(ledgerBinding, pinnedBinding);
    }

    private async Task<RunCostBinding> ReadPinnedBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CostRunScope scope,
        CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand($"""
            SELECT binding_json, unpriced_reason FROM {_bindings}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND meter_source = @meter
            """, connection, transaction);
        AddScope(read, scope);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new(null, "cost-binding-not-pinned");
        return new(reader.IsDBNull(0) ? null : Deserialize<CostBinding>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static bool CostBindingsEqual(CostBinding left, CostBinding right) =>
        left.MeterSource == right.MeterSource &&
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        left.ResourceId == right.ResourceId &&
        left.ResourceGeneration == right.ResourceGeneration &&
        left.NegotiatedCapabilities is not null &&
        left.NegotiatedCapabilities.SetEquals(right.NegotiatedCapabilities) &&
        CopilotCostProvider.RateCardsEqual(left.RateCard, right.RateCard);

    /// <summary>Reads an existing run binding or persists the first resolution, including unpriced outcomes.</summary>
    private async Task<RunCostBinding> ReadOrPinBindingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string tenantId, string projectId, string runId, string meterSource, CancellationToken cancellationToken)
    {
        await using (var read = new NpgsqlCommand($"""
            SELECT binding_json, unpriced_reason FROM {_bindings}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND meter_source = @meter
            """, connection, transaction))
        {
            AddScope(read, tenantId, projectId, runId, meterSource);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new(reader.IsDBNull(0) ? null : Deserialize<CostBinding>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        var resolution = bindings?.ResolveAndPin(meterSource, runId);
        var pinned = resolution?.IsSuccess == true && resolution.Value is not null
            ? new RunCostBinding(resolution.Value, null)
            : new RunCostBinding(null, resolution?.Error?.Code.ToString() ?? "cost-provider-unavailable");
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_bindings}(tenant_id, project_id, run_id, meter_source, binding_json, unpriced_reason)
            VALUES (@tenant, @project, @run, @meter, @binding, @reason)
            """, connection, transaction);
        AddScope(insert, tenantId, projectId, runId, meterSource);
        insert.Parameters.AddWithValue("binding", NpgsqlDbType.Varchar,
            pinned.Binding is null ? DBNull.Value : JsonSerializer.Serialize(pinned.Binding, JsonOptions));
        insert.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, (object?)pinned.UnpricedReason ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return pinned;
    }

    private CostPrice Price(UsageSubmission usage, RunCostBinding pinned)
    {
        if (pinned.Binding is null)
            return new(null, UnpricedUnit(usage.ModelBinding.MeterSource),
                CostDisposition.Unpriced, null, pinned.UnpricedReason);
        var verified = bindings?.VerifyPinned(
            usage.ModelBinding.MeterSource, usage.Attribution.RunId, pinned.Binding);
        if (provider is null || verified?.IsSuccess != true)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "pinned-cost-binding-unavailable");
        return provider.Price(usage.Measurement, usage.ModelBinding, pinned.Binding);
    }

    private CostPrice Quote(string modelId, string runId, RunCostBinding pinned)
    {
        if (pinned.Binding is null)
            return new(null, CopilotCostProvider.AiCreditUnit,
                CostDisposition.Unpriced, null, pinned.UnpricedReason);
        var verified = bindings?.VerifyPinned(SdkMeterSources.CopilotNanoAiu, runId, pinned.Binding);
        if (provider is null || verified?.IsSuccess != true)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "pinned-cost-binding-unavailable");
        return provider.Quote(new(modelId, 0, CostQuoteBasis.ProviderWeightedNanoAiu), pinned.Binding);
    }

    private NativeUsageCostPreflightResult QuotePreflight(
        string meterSource,
        string modelId,
        string runId,
        RunCostBinding pinned)
    {
        if (pinned.Binding is null)
            return new(null, false, pinned.UnpricedReason ?? "cost-provider-unavailable");

        var verified = bindings?.VerifyPinned(meterSource, runId, pinned.Binding);
        if (provider is null || verified?.IsSuccess != true)
            return new(pinned.Binding, false, "pinned-cost-binding-unavailable");

        var quote = provider.Quote(
            new CostQuoteRequest(modelId, 0m, CostQuoteBasis.ProviderWeightedNanoAiu),
            pinned.Binding);
        return quote.Disposition == CostDisposition.Estimate &&
               quote.Amount is not null &&
               quote.Unit == pinned.Binding.RateCard.Unit &&
               CopilotCostProvider.RateCardsEqual(quote.RateCard, pinned.Binding.RateCard)
            ? new(pinned.Binding, true, null)
            : new(pinned.Binding, false, quote.UnpricedReason ?? "cost-quote-invalid");
    }

    /// <summary>Keeps the known Copilot accounting unit visible without assigning a price.</summary>
    private static string? UnpricedUnit(string meterSource) =>
        meterSource == SdkMeterSources.CopilotNanoAiu ? CopilotCostProvider.AiCreditUnit : null;

    private static async Task AcquireCostLockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string tenantId, string projectId, string runId, string meterSource, CancellationToken cancellationToken)
    {
        await using var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.usage.cost'), hashtext(@run))",
            connection, transaction);
        acquire.Parameters.AddWithValue("run", NpgsqlDbType.Text,
            JsonSerializer.Serialize(new[] { tenantId, projectId, runId, meterSource }));
        await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddScope(
        NpgsqlCommand command, string tenantId, string projectId, string runId, string meterSource)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, tenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        command.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, meterSource);
    }

    private static void AddScope(NpgsqlCommand command, CostRunScope scope) =>
        AddScope(command, scope.TenantId, scope.ProjectId, scope.RunId, scope.MeterSource);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native usage accounting receipt is invalid.");

    private sealed record CostRunScope(
        string TenantId, string ProjectId, string RunId, string MeterSource)
    {
        internal static CostRunScope From(UsageSubmission usage) =>
            new(usage.Attribution.TenantId, usage.Attribution.ProjectId,
                usage.Attribution.RunId, usage.ModelBinding.MeterSource);
    }

    private sealed record RunCostBinding(CostBinding? Binding, string? UnpricedReason);
}
