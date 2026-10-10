using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

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
    private readonly string _receipts = $"\"{options.Schema}\".usage_source_receipts";
    private readonly string _bindings = $"\"{options.Schema}\".usage_run_cost_bindings";
    private readonly string _ledger = $"\"{options.Schema}\".usage_ledger";

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

    internal async Task<RuntimeUsageCostSnapshotReceipt> ReadCostSnapshotAsync(
        RuntimeUsageCostSnapshotRequest request, Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        RuntimeUsageCostSnapshotContract.ValidateRequest(request);
        var scope = request.Registration.Binding;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireCostLockAsync(connection, transaction, scope.TenantId, scope.ProjectId, scope.RunId,
            request.Source.MeterSource, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(connection, transaction,
            scope.TenantId, scope.ProjectId, scope.RunId, request.Source.MeterSource, cancellationToken)
            .ConfigureAwait(false);
        var quote = Quote(request.Source.ModelId, scope.RunId, pinned);
        var represented = await ReadRequiredReceiptsAsync(connection, transaction, request, cancellationToken)
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
        CancellationToken cancellationToken)
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
                source.Usage.SdkSource != request.Source || source.Usage.A2AMessageId != request.DispatchId ||
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
            return new(null, null, CostDisposition.Unpriced, null, pinned.UnpricedReason);
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
            return new(null, null, CostDisposition.Unpriced, null, pinned.UnpricedReason);
        var verified = bindings?.VerifyPinned(SdkMeterSources.CopilotNanoAiu, runId, pinned.Binding);
        if (provider is null || verified?.IsSuccess != true)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "pinned-cost-binding-unavailable");
        return provider.Quote(new(modelId, 0, CostQuoteBasis.ProviderWeightedNanoAiu), pinned.Binding);
    }

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

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native usage accounting receipt is invalid.");

    private sealed record RunCostBinding(CostBinding? Binding, string? UnpricedReason);
}
