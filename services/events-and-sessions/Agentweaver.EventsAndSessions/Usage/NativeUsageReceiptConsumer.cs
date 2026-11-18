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
    CopilotCostProvider? provider = null, ICostProviderBinder? bindings = null,
    AzureCostProvider? azureProvider = null, AzureCostProviderBinder? azureBindings = null,
    Agentweaver.AgentRuntime.RuntimeModelBindingsResolver? models = null,
    RuntimeRateCardStore? runtimeRateCards = null)
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

    /// <summary>Pins the accepted cost identity and returns current accounting for run admission.</summary>
    internal async Task<RuntimeRunAdmissionReceipt> ReadRunAdmissionAsync(
        string tenantId, string projectId, string runId, RuntimeRunAdmissionRequest request,
        JsonElement selection, RuntimeAcceptedModelSelection model, RuntimeModelBindingPin modelPin,
        ImmutableArray<RuntimeAcceptedModelSelection> rateCardModels,
        Func<CancellationToken, Task> revalidateCurrentAuthority, CancellationToken cancellationToken)
    {
        RuntimeRunAdmissionContract.ValidateRequest(request);
        var meterSource = model.SourceMode == ModelSourceMode.Byok
            ? SdkMeterSources.ByokTokens
            : SdkMeterSources.CopilotNanoAiu;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireCostLockAsync(connection, transaction, tenantId, projectId, runId,
            meterSource, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(connection, transaction,
            tenantId, projectId, runId, meterSource, cancellationToken,
            model.ProviderBindingRevision,
            model.SourceMode == ModelSourceMode.Byok &&
                model.ProviderBindingRevision is { } primaryProviderBinding
                    ? RuntimeByokCostBindingIdentity.From(modelPin, primaryProviderBinding)
                    : null).ConfigureAwait(false);
        if (rateCardModels.Any(candidate =>
                candidate.Reference != model.Reference || candidate.ReasoningEffort != model.ReasoningEffort))
            await AcquireCostLockAsync(connection, transaction, tenantId, projectId, runId,
                SdkMeterSources.ByokTokens, cancellationToken).ConfigureAwait(false);
        var admissions = ImmutableArray.CreateBuilder<RuntimeRateCardAdmission>(rateCardModels.Length);
        foreach (var candidate in rateCardModels)
        {
            var providerBinding = candidate.ProviderBindingRevision
                ?? throw new RuntimeAuthorizationException("runtime_run_admission_model_binding_invalid");
            var candidatePin = candidate.ModelBindingPin
                ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required");
            var candidateBinding = candidate.Reference == model.Reference &&
                candidate.ReasoningEffort == model.ReasoningEffort
                    ? pinned
                    : await ReadOrPinBindingAsync(connection, transaction,
                        tenantId, projectId, runId, SdkMeterSources.ByokTokens, cancellationToken,
                        providerBinding, RuntimeByokCostBindingIdentity.From(candidatePin, providerBinding))
                        .ConfigureAwait(false);
            admissions.Add(new(candidatePin, providerBinding,
                candidateBinding.Binding
                    ?? throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_unavailable")));
        }
        var quote = model.SourceMode == ModelSourceMode.Byok
            ? new CostPrice(null, pinned.Binding?.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding?.RateCard, "byok-token-quotes-unsupported")
            : Quote(modelPin.ModelId, runId, pinned);
        var totals = await ledger.GetCopilotRunTotalsWithinTransactionAsync(
            connection, transaction, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var receipt = new RuntimeRunAdmissionReceipt(
            1, tenantId, projectId, runId, request.AcceptedSelectionHash, model, modelPin, pinned.Binding, quote, totals)
        {
            RateCardContentHash = RateCardContentHash(pinned.Binding),
            RateCardAdmissions = admissions.MoveToImmutable()
        };
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
            costBinding.RateCard.Unit != request.RateCardUnit ||
            RateCardContentHash(costBinding) != request.RateCardContentHash)
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
            totals)
        {
            RateCardContentHash = RateCardContentHash(costBinding)
        };
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
        CancellationToken cancellationToken,
        ModelProviderBindingRevision? providerBinding = null,
        RuntimeModelBindingPin? modelBindingPin = null)
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
            scope.MeterSource, cancellationToken, providerBinding,
            providerBinding is null ? null : RuntimeByokCostBindingIdentity.From(
                modelBindingPin ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required"),
                providerBinding)).ConfigureAwait(false);
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
        // An Azure BYOK claim enters the ledger only with consistent owner-proven source facts.
        // Usage without a dispatch message ID is recorded as Unpriced; it is never rejected or dropped.
        // Only dispatch-correlated usage that passes RequireTrustedAzureByok can be priced.
        var trustedAzureByok = false;
        var trustedRuntimeByok = false;
        var providerBinding = receipt.Registration.Binding.ProviderBindingRevision;
        var azureOpenAIEndpoint = IsAzureOpenAIV1Endpoint(receipt);
        if (providerBinding is not null)
        {
            RuntimeUsageSourceReceiptContract.RequireTrustedRuntimeByok(receipt);
            trustedRuntimeByok = receipt.Usage.A2AMessageId is { } dispatchId && dispatchId != Guid.Empty;
        }
        if (RuntimeUsageSourceReceiptContract.ClaimsAzureByok(receipt, azureOpenAIEndpoint))
        {
            // Step 1: every Azure claim must have matching provider, deployment, and configuration
            // facts. A failed check throws, so a false Azure claim never reaches the ledger.
            RuntimeUsageSourceReceiptContract.RequireAzureByokProvenance(receipt, azureOpenAIEndpoint);
            // Step 2: pricing also needs the dispatch ID, so the charge can be traced to one dispatch.
            // Usage without it is still recorded, but it stays unpriced.
            if (receipt.Usage.A2AMessageId is { } dispatchId && dispatchId != Guid.Empty)
            {
                RuntimeUsageSourceReceiptContract.RequireTrustedAzureByok(receipt, azureOpenAIEndpoint);
                trustedAzureByok = true;
            }
        }
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
            RuntimeUsageSourceReceiptContract.Validate(stored);
            if (stored.ContractVersion != receipt.ContractVersion ||
                stored.ReceiptId != receipt.ReceiptId ||
                stored.RecordedAt != receipt.RecordedAt ||
                stored.CanonicalPayloadHash != receipt.CanonicalPayloadHash ||
                reader.GetString(1) != receipt.CanonicalPayloadHash)
                throw new UsageLedgerConflictException("The source receipt identity has a different immutable payload.");
            acknowledgment = new(receipt.ReceiptId, Deserialize<UsageAccountingReceipt>(reader.GetString(2)), true);
        }
        else
        {
            var pinned = await ReadOrPinBindingAsync(connection, transaction, usage.Attribution.TenantId,
                usage.Attribution.ProjectId, usage.Attribution.RunId, usage.ModelBinding.MeterSource, cancellationToken,
                providerBinding,
                providerBinding is null ? null : RuntimeByokCostBindingIdentity.From(
                    receipt.Registration.Binding.ModelBindingPin
                        ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required"),
                    providerBinding))
                .ConfigureAwait(false);
            var price = Price(usage, pinned, trustedAzureByok, trustedRuntimeByok);
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
            scope.TenantId, scope.ProjectId, scope.RunId, request.Source.MeterSource, cancellationToken,
            scope.ProviderBindingRevision,
            scope.ProviderBindingRevision is null ? null : RuntimeByokCostBindingIdentity.From(
                scope.ModelBindingPin
                    ?? throw new RuntimeAuthorizationException("runtime_model_binding_pin_required"),
                scope.ProviderBindingRevision))
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
            DispatchId = request.DispatchId,
            RepresentedReceipts = represented,
            RateCardContentHash = RateCardContentHash(pinned.Binding)
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
                    ledger.rate_card_id, ledger.rate_card_version, ledger.recorded_at, ledger.cost_binding
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
            var ledgerBinding = reader.IsDBNull(12) ? null : Deserialize<CostBinding>(reader.GetString(12));
            if (source.ReceiptId != reference.SourceReceiptId ||
                source.Usage.SdkSource is not { } sourceFacts ||
                RuntimeUsageSourceReceiptContract.HashSource(source.Registration, sourceFacts) !=
                    RuntimeUsageSourceReceiptContract.HashSource(request.Registration, request.Source) ||
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
                accounting.RateCardContentHash != RateCardContentHash(ledgerBinding) ||
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
    private bool IsAzureOpenAIV1Endpoint(RuntimeUsageSourceReceipt receipt)
    {
        // An openai pin is treated as Azure only when this service's own bindings resolve the exact
        // pin to an Azure OpenAI v1 endpoint. Other openai receipts keep their normal unpriced path.
        if (models is null ||
            receipt?.Registration?.Binding?.ModelBindingPin is not { ProviderType: "openai", SourceMode: ModelSourceMode.Byok } pin)
            return false;
        try
        {
            return models.Resolve(pin.ModelSelectionReference, ModelSourceMode.Byok, pin).Provider
                is { IsAzureOpenAIV1Endpoint: true };
        }
        catch (RuntimeAuthorizationException)
        {
            return false;
        }
    }

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
            accounting.RateCardContentHash == request.RateCardContentHash &&
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
            ledgerBinding?.RateCard?.ContentHash == request.RateCardContentHash &&
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
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND meter_source = @meter AND binding_identity_hash = ''
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
        CopilotCostProvider.RateCardsEqual(left.RateCard, right.RateCard) &&
        left.RuntimeByokIdentity == right.RuntimeByokIdentity &&
        RuntimeRateCardPinsEqual(left, right);

    /// <summary>Reads an existing run binding or persists the first resolution, including unpriced outcomes.</summary>
    private async Task<RunCostBinding> ReadOrPinBindingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string tenantId, string projectId, string runId, string meterSource,
        CancellationToken cancellationToken, ModelProviderBindingRevision? providerBinding = null,
        RuntimeByokCostBindingIdentity? runtimeIdentity = null)
    {
        RuntimeRateCardPin? requestedRateCardPin = null;
        if (providerBinding is not null)
        {
            try
            {
                Agentweaver.AgentRuntime.RuntimeModelBindingsResolver.ValidateProviderBinding(providerBinding);
            }
            catch (RuntimeAuthorizationException)
            {
                throw new RuntimeAuthorizationException("runtime_run_admission_model_binding_invalid");
            }
            if (meterSource != SdkMeterSources.ByokTokens ||
                providerBinding.RateCardPin is not { } rateCardPin ||
                runtimeIdentity is null ||
                runtimeIdentity.ProviderId != providerBinding.ProviderId ||
                runtimeIdentity.ProviderRevision != providerBinding.ProviderRevision ||
                runtimeIdentity.ConfigurationHash != providerBinding.ConfigurationHash)
                throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_pin_required");
            requestedRateCardPin = rateCardPin;
        }
        else if (runtimeIdentity is not null)
            throw new RuntimeAuthorizationException("runtime_run_admission_model_binding_invalid");
        var identityHash = runtimeIdentity is null
            ? string.Empty
            : RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(runtimeIdentity, JsonOptions));

        await using (var read = new NpgsqlCommand($"""
            SELECT binding_json, unpriced_reason FROM {_bindings}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
              AND meter_source = @meter AND binding_identity_hash = @identity
            """, connection, transaction))
        {
            AddScope(read, tenantId, projectId, runId, meterSource);
            read.Parameters.AddWithValue("identity", NpgsqlDbType.Varchar, identityHash);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existing = new RunCostBinding(
                    reader.IsDBNull(0) ? null : Deserialize<CostBinding>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
                if (requestedRateCardPin is not null &&
                    (existing.Binding is not { } existingBinding ||
                     existing.UnpricedReason is not null ||
                     !IsRuntimeRateCardBinding(
                         existingBinding, providerBinding!, requestedRateCardPin, runtimeIdentity!)))
                    throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_mismatch");
                if (requestedRateCardPin is null && existing.Binding?.RuntimeRateCardPin is not null)
                    throw new RuntimeAuthorizationException("runtime_cost_preflight_rate_card_pin_required");
                return existing;
            }
        }
        RunCostBinding pinned;
        if (requestedRateCardPin is not null)
        {
            if (runtimeRateCards is null)
                throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_unavailable");
            var storedCard = await runtimeRateCards.GetAsync(
                requestedRateCardPin.RateCardId,
                requestedRateCardPin.RateCardVersion,
                cancellationToken).ConfigureAwait(false);
            if (storedCard is null)
                throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_unavailable");
            var response = RuntimeRateCardContract.ToReadResponse(storedCard);
            if (!RuntimeRateCardReadContract.MatchesPin(response, requestedRateCardPin))
                throw new RuntimeAuthorizationException("runtime_run_admission_rate_card_mismatch");
            pinned = new(CreateRuntimeRateCardBinding(
                providerBinding!, storedCard, requestedRateCardPin, runtimeIdentity!), null);
        }
        else
        {
            var resolution = BinderFor(meterSource)?.ResolveAndPin(meterSource, runId);
            pinned = resolution?.IsSuccess == true && resolution.Value is not null
                ? new RunCostBinding(resolution.Value, null)
                : new RunCostBinding(null, resolution?.Error?.Code.ToString() ?? "cost-provider-unavailable");
        }
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_bindings}
                (tenant_id, project_id, run_id, meter_source, binding_identity_hash, binding_json, unpriced_reason)
            VALUES (@tenant, @project, @run, @meter, @identity, @binding, @reason)
            """, connection, transaction);
        AddScope(insert, tenantId, projectId, runId, meterSource);
        insert.Parameters.AddWithValue("identity", NpgsqlDbType.Varchar, identityHash);
        insert.Parameters.AddWithValue("binding", NpgsqlDbType.Varchar,
            pinned.Binding is null ? DBNull.Value : JsonSerializer.Serialize(pinned.Binding, JsonOptions));
        insert.Parameters.AddWithValue("reason", NpgsqlDbType.Varchar, (object?)pinned.UnpricedReason ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return pinned;
    }

    private static CostBinding CreateRuntimeRateCardBinding(
        ModelProviderBindingRevision providerBinding,
        RuntimeRateCard runtimeRateCard,
        RuntimeRateCardPin rateCardPin,
        RuntimeByokCostBindingIdentity runtimeIdentity) =>
        new(
            SdkMeterSources.ByokTokens,
            "events.runtime-rate-card",
            "1.0.0",
            1,
            providerBinding.ConfigurationHash,
            rateCardPin.RateCardId,
            providerBinding.ProviderRevision,
            ImmutableHashSet.Create(StringComparer.Ordinal, AzureCostCapabilities.PriceUsage),
            runtimeRateCard.RateCard)
        {
            RuntimeRateCardPin = rateCardPin,
            RuntimeByokIdentity = runtimeIdentity
        };

    private static bool IsRuntimeRateCardBinding(
        CostBinding binding,
        ModelProviderBindingRevision providerBinding,
        RuntimeRateCardPin rateCardPin,
        RuntimeByokCostBindingIdentity runtimeIdentity) =>
        binding.MeterSource == SdkMeterSources.ByokTokens &&
        binding.ProviderId == "events.runtime-rate-card" &&
        binding.AdapterVersion == "1.0.0" &&
        binding.OptionsSchemaVersion == 1 &&
        binding.OptionsRevision == providerBinding.ConfigurationHash &&
        binding.ResourceId == rateCardPin.RateCardId &&
        binding.ResourceGeneration == providerBinding.ProviderRevision &&
        binding.RuntimeByokIdentity == runtimeIdentity &&
        binding.RuntimeRateCardPin is { } storedPin &&
        storedPin.RateCardId == rateCardPin.RateCardId &&
        storedPin.RateCardVersion == rateCardPin.RateCardVersion &&
        storedPin.ContentHash == rateCardPin.ContentHash &&
        RuntimeRateCardReadContract.MatchesPin(
            new(RuntimeRateCardReadContract.CurrentSchemaVersion, binding.RateCard, storedPin.Provenance),
            storedPin) &&
        RuntimeRateCardReadContract.MatchesPin(
            new(RuntimeRateCardReadContract.CurrentSchemaVersion, binding.RateCard, rateCardPin.Provenance),
            rateCardPin);

    private static bool RuntimeRateCardPinsEqual(CostBinding left, CostBinding right)
    {
        if (left.RuntimeRateCardPin is not { } leftPin ||
            right.RuntimeRateCardPin is not { } rightPin)
            return left.RuntimeRateCardPin is null && right.RuntimeRateCardPin is null;
        return leftPin.RateCardId == rightPin.RateCardId &&
            leftPin.RateCardVersion == rightPin.RateCardVersion &&
            leftPin.ContentHash == rightPin.ContentHash &&
            RuntimeRateCardReadContract.MatchesPin(
                new(RuntimeRateCardReadContract.CurrentSchemaVersion, left.RateCard, leftPin.Provenance),
                leftPin) &&
            RuntimeRateCardReadContract.MatchesPin(
                new(RuntimeRateCardReadContract.CurrentSchemaVersion, right.RateCard, rightPin.Provenance),
                rightPin);
    }

    private static string? RateCardContentHash(CostBinding? binding) =>
        binding?.RuntimeRateCardPin?.ContentHash ?? binding?.RateCard?.ContentHash;

    /// <summary>Selects the binder that serves a meter source; BYOK tokens use the Azure adapter only.</summary>
    private ICostProviderBinder? BinderFor(string meterSource) =>
        meterSource == SdkMeterSources.ByokTokens ? azureBindings : bindings;

    private CostPrice Price(
        UsageSubmission usage,
        RunCostBinding pinned,
        bool trustedAzureByok,
        bool trustedRuntimeByok)
    {
        // BYOK token usage has its own pricing rules and provider; all other meters use the Copilot path.
        if (usage.ModelBinding.MeterSource == SdkMeterSources.ByokTokens)
            return pinned.Binding?.RuntimeRateCardPin is not null
                ? PriceRuntimeByok(usage, pinned, trustedRuntimeByok)
                : PriceAzureByok(usage, pinned, trustedAzureByok);
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

    /// <summary>
    /// Prices BYOK token usage only for receipts that passed trusted Azure provenance. Other BYOK
    /// providers stay unpriced; the adapter never substitutes zero or reprices a pinned rate card.
    /// </summary>
    private CostPrice PriceAzureByok(UsageSubmission usage, RunCostBinding pinned, bool trustedAzureByok)
    {
        // Non-Azure BYOK usage, and Azure usage without a dispatch ID, is never priced.
        if (!trustedAzureByok)
            return new(null, null, CostDisposition.Unpriced, null, "byok-provider-unpriced");
        // No rate card was pinned for this run, for example because Azure pricing is not configured.
        if (pinned.Binding is null)
            return new(null, null, CostDisposition.Unpriced, null, pinned.UnpricedReason);
        // The run keeps the rate card it pinned first. If the configured card has changed since then,
        // verification fails and new usage stays unpriced instead of being priced at a different rate.
        var verified = azureBindings?.VerifyPinned(
            usage.ModelBinding.MeterSource, usage.Attribution.RunId, pinned.Binding);
        if (azureProvider is null || verified?.IsSuccess != true)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "pinned-cost-binding-unavailable");
        return azureProvider.Price(usage.Measurement, usage.ModelBinding, pinned.Binding);
    }

    private static CostPrice PriceRuntimeByok(
        UsageSubmission usage, RunCostBinding pinned, bool trustedRuntimeByok)
    {
        var binding = pinned.Binding!;
        var card = binding.RateCard;
        if (!trustedRuntimeByok)
            return new(null, card.Unit, CostDisposition.Unpriced, card, "byok-dispatch-provenance-unavailable");
        if (binding.RuntimeRateCardPin is not { } pin ||
            !RuntimeRateCardReadContract.MatchesPin(
                new(RuntimeRateCardReadContract.CurrentSchemaVersion, card, pin.Provenance), pin))
            return new(null, card.Unit, CostDisposition.Unpriced, card, "runtime-rate-card-pin-invalid");
        var measurement = usage.Measurement;
        if (measurement.ProviderUnits is not null || measurement.ProviderUnit is not (null or "tokens"))
            return new(null, card.Unit, CostDisposition.Unpriced, card, "provider-units-unsupported");
        if (measurement.InputTokens is null || measurement.OutputTokens is null)
            return new(null, card.Unit, CostDisposition.Unpriced, card, "token-measurements-missing");
        if (measurement.CachedTokens is null || measurement.CacheWriteTokens is null)
            return new(null, card.Unit, CostDisposition.Unpriced, card, "token-cache-measurements-missing");
        if (measurement.CacheWriteTokens != 0)
            return new(null, card.Unit, CostDisposition.Unpriced, card, "token-cache-pricing-unavailable");
        if (measurement.ReasoningTokens is not null &&
            measurement.ReasoningTokens > measurement.OutputTokens)
            return new(null, card.Unit, CostDisposition.Unpriced, card, "token-measurement-inconsistent");
        if (!card.ModelMultipliers.TryGetValue(
                AzureCostProviderOptions.InputRateKey(usage.ModelBinding.ModelId), out var inputRate) ||
            !card.ModelMultipliers.TryGetValue(
                AzureCostProviderOptions.OutputRateKey(usage.ModelBinding.ModelId), out var outputRate))
            return new(null, card.Unit, CostDisposition.Unpriced, card, "model-rate-unavailable");
        var cachedRate = 0m;
        if (measurement.CachedTokens != 0 && !card.ModelMultipliers.TryGetValue(
                AzureCostProviderOptions.CachedInputRateKey(usage.ModelBinding.ModelId), out cachedRate))
            return new(null, card.Unit, CostDisposition.Unpriced, card, "token-cache-pricing-unavailable");
        decimal amount;
        checked
        {
            amount = ((decimal)measurement.InputTokens.Value * inputRate +
                (decimal)measurement.CachedTokens.Value * cachedRate +
                (decimal)measurement.OutputTokens.Value * outputRate) / card.NanoUnitsPerUnit;
        }
        return new(amount, card.Unit, CostDisposition.Estimate, card, null);
    }

    private CostPrice Quote(string modelId, string runId, RunCostBinding pinned)
    {
        if (pinned.Binding is null)
            return new(null, CopilotCostProvider.AiCreditUnit,
                CostDisposition.Unpriced, null, pinned.UnpricedReason);
        if (pinned.Binding.RuntimeRateCardPin is not null)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "byok-token-quotes-unsupported");
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
        // BYOK work is never quoted before it runs, because its cost depends on tokens that are not yet
        // known. The checks below only choose the reason that is reported for the missing quote.
        if (meterSource == SdkMeterSources.ByokTokens)
        {
            if (pinned.Binding.RuntimeRateCardPin is not null)
                return new(pinned.Binding, false, "byok-token-quotes-unsupported");
            var azureVerified = azureBindings?.VerifyPinned(meterSource, runId, pinned.Binding);
            if (azureProvider is null || azureVerified?.IsSuccess != true)
                return new(pinned.Binding, false, "pinned-cost-binding-unavailable");
            var azureQuote = azureProvider.Quote(
                new CostQuoteRequest(modelId, 0m, CostQuoteBasis.ProviderWeightedNanoAiu), pinned.Binding);
            return new(pinned.Binding, false, azureQuote.UnpricedReason ?? "cost-quote-invalid");
        }

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
