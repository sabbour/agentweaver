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

    internal async Task<RuntimeUsageAccountingAcknowledgment> AppendAsync(
        RuntimeUsageSourceReceipt receipt, Func<CancellationToken, Task> revalidateCurrentAuthority,
        CancellationToken cancellationToken)
    {
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        var sourceJson = JsonSerializer.Serialize(receipt, JsonOptions);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var usage = receipt.Usage;
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.usage.cost'), hashtext(@run))",
            connection, transaction))
        {
            acquire.Parameters.AddWithValue("run", NpgsqlDbType.Text, JsonSerializer.Serialize(new[]
            {
                usage.Attribution.TenantId, usage.Attribution.ProjectId,
                usage.Attribution.RunId, usage.ModelBinding.MeterSource
            }));
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
            var pinned = await ReadOrPinBindingAsync(connection, transaction, usage, cancellationToken)
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

    private async Task<RunCostBinding> ReadOrPinBindingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, UsageSubmission usage,
        CancellationToken cancellationToken)
    {
        await using (var read = new NpgsqlCommand($"""
            SELECT binding_json, unpriced_reason FROM {_bindings}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND meter_source = @meter
            """, connection, transaction))
        {
            AddScope(read, usage);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new(reader.IsDBNull(0) ? null : Deserialize<CostBinding>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        var resolution = bindings?.ResolveAndPin(usage.ModelBinding.MeterSource, usage.Attribution.RunId);
        var pinned = resolution?.IsSuccess == true && resolution.Value is not null
            ? new RunCostBinding(resolution.Value, null)
            : new RunCostBinding(null, resolution?.Error?.Code.ToString() ?? "cost-provider-unavailable");
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_bindings}(tenant_id, project_id, run_id, meter_source, binding_json, unpriced_reason)
            VALUES (@tenant, @project, @run, @meter, @binding, @reason)
            """, connection, transaction);
        AddScope(insert, usage);
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

    private static void AddScope(NpgsqlCommand command, UsageSubmission usage)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, usage.Attribution.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, usage.Attribution.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, usage.Attribution.RunId);
        command.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, usage.ModelBinding.MeterSource);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("A stored native usage accounting receipt is invalid.");

    private sealed record RunCostBinding(CostBinding? Binding, string? UnpricedReason);
}
