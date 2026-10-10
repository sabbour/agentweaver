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

internal sealed class NativeUsageReconciliationException(
    string code, ImmutableArray<Guid> sourceReceiptIds = default) : InvalidOperationException(code)
{
    internal string Code { get; } = code;
    internal ImmutableArray<Guid> SourceReceiptIds { get; } = sourceReceiptIds;
}

internal sealed class NativeUsageSourceCompletionUnavailableException()
    : InvalidOperationException("runtime_usage_source_completion_unavailable");

internal sealed record NativeUsageCostPreflightResult(
    CostBinding? Binding,
    bool IsPriced,
    string? UnpricedReason);

internal sealed class NativeUsageReceiptConsumer(
    NpgsqlDataSource dataSource, PostgresSessionsProviderOptions options, PostgresUsageLedger ledger,
    IReadOnlyDictionary<string, ICostProvider> providers, ICostProviderBinder bindings)
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
    private readonly string _completions = $"\"{options.Schema}\".usage_dispatch_source_completions";
    private readonly ImmutableDictionary<string, ICostProvider> _providers =
        providers.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    internal async Task<UsageDispatchAccountingWitness> RecordDispatchSourceCompletionAsync(
        UsageDispatchSourceCompletionManifest manifest,
        ImmutableArray<RuntimeUsageSourceReceipt> sourceReceipts,
        Func<CancellationToken, Task> revalidateOwnerProofAndAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(revalidateOwnerProofAndAuthority);
        var receiptsById = ValidateOwnerProofReceipts(manifest, sourceReceipts);
        if (sourceReceipts.Any(receipt =>
                receipt.Usage.SdkSource?.SourceMode != "hosted-copilot" ||
                receipt.Usage.ModelBinding.MeterSource != SdkMeterSources.CopilotNanoAiu))
            throw new NativeUsageReconciliationException("runtime_usage_source_completion_scope_mismatch");

        var dispatchId = Guid.ParseExact(manifest.DispatchId, "D");
        var canonicalManifest = UsageDispatchSourceCompletionManifestContract.SerializeCanonical(manifest);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await PostgresUsageLedger.AcquireRunAccountingScopeLockAsync(
            connection, transaction, manifest.TenantId, manifest.ProjectId, manifest.RunId, cancellationToken)
            .ConfigureAwait(false);

        var actualEvents = await ReadDispatchEventIdsAsync(connection, transaction, manifest, dispatchId, cancellationToken)
            .ConfigureAwait(false);
        var expectedEvents = manifest.SourceReceipts.Select(receipt => receipt.EventId).ToHashSet();
        if (!actualEvents.SetEquals(expectedEvents))
            throw new NativeUsageReconciliationException("runtime_usage_source_completion_incomplete",
                manifest.SourceReceipts.Where(receipt => !actualEvents.Contains(receipt.EventId))
                    .Select(receipt => receipt.SourceReceiptId).ToImmutableArray());

        var joined = await ReadDispatchAccountingEntriesAsync(
            connection, transaction, manifest, dispatchId, receiptsById, cancellationToken).ConfigureAwait(false);
        if (joined.Length != manifest.SourceReceipts.Length)
            throw new NativeUsageReconciliationException("runtime_usage_source_completion_incomplete",
                manifest.SourceReceipts.Where(receipt => !joined.Any(row =>
                    row.Identity.SourceReceiptId == receipt.SourceReceiptId))
                    .Select(receipt => receipt.SourceReceiptId).ToImmutableArray());

        var existing = await ReadSourceCompletionAsync(connection, transaction, manifest, dispatchId, cancellationToken)
            .ConfigureAwait(false);
        long accountingRevision;
        if (existing is not null)
        {
            if (existing.Value.CanonicalManifest != canonicalManifest)
                throw new UsageLedgerConflictException(
                    "A dispatch source-completion identity cannot be reused with changed membership.");
            accountingRevision = existing.Value.AccountingRevision;
        }
        else
        {
            accountingRevision = await AllocateNextAccountingRevisionAsync(
                connection, transaction, manifest, cancellationToken).ConfigureAwait(false);
            await InsertSourceCompletionAsync(
                connection, transaction, manifest, dispatchId, accountingRevision, canonicalManifest,
                cancellationToken).ConfigureAwait(false);
        }

        decimal knownPricedSubtotal = 0m;
        var pricedReceiptCount = 0L;
        var unpricedReceiptCount = 0L;
        foreach (var row in joined)
        {
            if (row.Accounting.Unit is not null && row.Accounting.Unit != "AIC")
                throw new NativeUsageReconciliationException("runtime_usage_source_completion_scope_mismatch",
                    [row.Identity.SourceReceiptId]);
            if (row.Accounting.Disposition == CostDisposition.Unpriced)
            {
                if (row.Accounting.Amount is not null)
                    throw new InvalidOperationException("An unpriced native usage receipt has an amount.");
                unpricedReceiptCount = checked(unpricedReceiptCount + 1);
            }
            else
            {
                if (row.Accounting.Amount is not { } amount || row.Accounting.Unit != "AIC")
                    throw new InvalidOperationException("A priced Copilot receipt has no AIC amount.");
                knownPricedSubtotal = checked(knownPricedSubtotal + amount);
                pricedReceiptCount = checked(pricedReceiptCount + 1);
            }
        }

        var scope = new UsageDispatchAccountingScope(
            manifest.TenantId, manifest.ProjectId, manifest.RunId, SdkMeterSources.CopilotNanoAiu, "AIC", null);
        var identities = joined.Select(row => row.Identity).ToImmutableArray();
        var totals = await ledger.GetRunTotalsWhileRunLockedAsync(
            connection, transaction, manifest.TenantId, manifest.ProjectId, manifest.RunId,
            SdkMeterSources.CopilotNanoAiu, cancellationToken).ConfigureAwait(false);
        var witness = new UsageDispatchAccountingWitness(
            manifest.DispatchId,
            scope,
            accountingRevision,
            identities,
            UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(manifest.DispatchId, scope, identities),
            knownPricedSubtotal,
            pricedReceiptCount,
            unpricedReceiptCount,
            UsageDispatchAccountingStatus.SourceComplete,
            manifest.CompletionReceiptId,
            manifest.CompletionRevision,
            manifest.ReceiptDigest)
        {
            RunTotals = totals
        };

        await revalidateOwnerProofAndAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return witness;
    }

    internal static ImmutableDictionary<Guid, RuntimeUsageSourceReceipt> ValidateOwnerProofReceipts(
        UsageDispatchSourceCompletionManifest manifest,
        ImmutableArray<RuntimeUsageSourceReceipt> sourceReceipts)
    {
        UsageDispatchSourceCompletionManifestContract.Validate(manifest);
        if (manifest.SourceReceipts.Length > RuntimeUsageCostReconciliationContract.MaximumReceiptReferences ||
            sourceReceipts.IsDefault || sourceReceipts.Length != manifest.SourceReceipts.Length)
            throw new NativeUsageReconciliationException("runtime_usage_source_completion_incomplete");

        var expected = manifest.SourceReceipts.ToDictionary(receipt => receipt.SourceReceiptId);
        var validated = ImmutableDictionary.CreateBuilder<Guid, RuntimeUsageSourceReceipt>();
        foreach (var source in sourceReceipts)
        {
            RuntimeUsageSourceReceiptContract.Validate(source);
            if (!expected.Remove(source.ReceiptId, out var identity) ||
                source.Usage.EventId != identity.EventId ||
                source.CanonicalPayloadHash != identity.CanonicalPayloadHash ||
                source.Usage.Attribution.DispatchId != manifest.DispatchId ||
                source.Registration.RuntimeInstanceId != manifest.RuntimeInstanceId ||
                source.Registration.Revision != manifest.RegistrationRevision ||
                source.Registration.Binding.ExecutionFence != manifest.ExecutionFence ||
                source.Registration.Binding.TenantId != manifest.TenantId ||
                source.Registration.Binding.ProjectId != manifest.ProjectId ||
                source.Registration.Binding.RunId != manifest.RunId ||
                source.Registration.Binding.SessionId != manifest.SessionId ||
                !validated.TryAdd(source.ReceiptId, source))
                throw new NativeUsageReconciliationException(
                    "runtime_usage_source_completion_mismatch", [source.ReceiptId]);
        }
        if (expected.Count != 0)
            throw new NativeUsageReconciliationException(
                "runtime_usage_source_completion_incomplete", expected.Keys.ToImmutableArray());
        return validated.ToImmutable();
    }

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
        var totals = await ledger.GetRunTotalsWithinSnapshotAsync(
            connection, transaction, tenantId, projectId, runId, request.MeterSource, cancellationToken)
            .ConfigureAwait(false);
        if (totals.Events < request.MinimumAccountingRevision)
            throw new NativeUsageReconciliationException("runtime_usage_reconciliation_stale");
        if (!totals.IsFullyPriced)
            throw new NativeUsageReconciliationException("runtime_usage_reconciliation_unpriced");

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
        await PostgresUsageLedger.AcquireRunAccountingScopeLockAsync(
            connection, transaction, tenantId, projectId, runId, cancellationToken).ConfigureAwait(false);
        var pinned = await ReadOrPinBindingAsync(
            connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var result = QuotePreflight(meterSource, verifiedModelId, runId, pinned);
        await revalidateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
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
        await PostgresUsageLedger.AcquireRunAccountingLockAsync(
            connection, transaction, usage, cancellationToken).ConfigureAwait(false);
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
            var storedAccounting = Deserialize<UsageAccountingReceipt>(reader.GetString(2));
            if (storedAccounting.SourceReceiptId is not null &&
                storedAccounting.SourceReceiptId != receipt.ReceiptId)
                throw new InvalidOperationException("The stored accounting ACK has a different source receipt identity.");
            acknowledgment = new(
                receipt.ReceiptId,
                storedAccounting with { SourceReceiptId = receipt.ReceiptId },
                true);
        }
        else
        {
            var pinned = await ReadOrPinBindingAsync(connection, transaction, usage, cancellationToken)
                .ConfigureAwait(false);
            var price = Price(usage, pinned);
            var appended = await ledger.AppendWithinTransactionAsync(
                connection, transaction, usage, pinned.Binding, price, cancellationToken).ConfigureAwait(false);
            acknowledgment = new(
                receipt.ReceiptId,
                appended.Receipt with { SourceReceiptId = receipt.ReceiptId },
                false);
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

    private async Task<ImmutableHashSet<Guid>> ReadDispatchEventIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageDispatchSourceCompletionManifest manifest,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT event_id
            FROM {_usageLedger}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                AND dispatch_id = @dispatch
            ORDER BY event_id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, manifest.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, manifest.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, manifest.RunId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        var eventIds = ImmutableHashSet.CreateBuilder<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (!eventIds.Add(reader.GetGuid(0)))
                throw new InvalidOperationException("A dispatch contains duplicate usage events.");
        return eventIds.ToImmutable();
    }

    private async Task<ImmutableArray<VerifiedDispatchReceipt>> ReadDispatchAccountingEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageDispatchSourceCompletionManifest manifest,
        Guid dispatchId,
        ImmutableDictionary<Guid, RuntimeUsageSourceReceipt> ownerReceipts,
        CancellationToken cancellationToken)
    {
        var expected = manifest.SourceReceipts.ToDictionary(receipt => receipt.SourceReceiptId);
        var joined = ImmutableArray.CreateBuilder<VerifiedDispatchReceipt>(expected.Count);
        await using var command = new NpgsqlCommand($"""
            SELECT r.source_receipt_id, r.event_id, r.source_payload_hash,
                   r.source_receipt_json, r.accounting_receipt_json,
                   l.dispatch_id, l.tenant_id, l.project_id, l.run_id, l.session_id,
                   l.accounting_revision, l.canonical_input_hash, l.meter_source,
                   l.price_disposition, l.price_amount, l.price_unit,
                   (l.payload->'usage')::text, l.rate_card_id, l.rate_card_version, l.rate_card_unit
            FROM {_receipts} r
            LEFT JOIN {_usageLedger} l ON l.event_id = r.event_id
            WHERE r.source_receipt_id = ANY(@receipts)
            ORDER BY r.source_receipt_id
            """, connection, transaction);
        command.Parameters.AddWithValue(
            "receipts", NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            manifest.SourceReceipts.Select(receipt => receipt.SourceReceiptId).ToArray());
        var found = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var receiptId = reader.GetGuid(0);
            if (!found.Add(receiptId) || !expected.TryGetValue(receiptId, out var identity) ||
                !ownerReceipts.TryGetValue(receiptId, out var ownerReceipt))
                throw new NativeUsageReconciliationException(
                    "runtime_usage_source_completion_mismatch", [receiptId]);
            if (reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(7) ||
                reader.IsDBNull(8) || reader.IsDBNull(10) || reader.IsDBNull(11) ||
                reader.IsDBNull(12) || reader.IsDBNull(13) || reader.IsDBNull(16))
                throw new NativeUsageReconciliationException(
                    "runtime_usage_source_completion_incomplete", [receiptId]);

            var storedSource = Deserialize<RuntimeUsageSourceReceipt>(reader.GetString(3));
            var accounting = Deserialize<UsageAccountingReceipt>(reader.GetString(4));
            var eventId = reader.GetGuid(1);
            var accountingRevision = reader.GetInt64(10);
            var canonicalInputHash = reader.GetString(11);
            var meterSource = reader.GetString(12);
            var disposition = Enum.Parse<CostDisposition>(reader.GetString(13), ignoreCase: false);
            var amount = reader.IsDBNull(14) ? (decimal?)null : reader.GetDecimal(14);
            var unit = reader.IsDBNull(15) ? null : reader.GetString(15);
            var ledgerUsage = Deserialize<UsageSubmission>(reader.GetString(16));

            if (eventId != identity.EventId || reader.GetString(2) != identity.CanonicalPayloadHash ||
                storedSource != ownerReceipt || storedSource.ReceiptId != receiptId ||
                storedSource.CanonicalPayloadHash != identity.CanonicalPayloadHash ||
                reader.GetGuid(5) != dispatchId || reader.GetString(6) != manifest.TenantId ||
                reader.GetString(7) != manifest.ProjectId || reader.GetString(8) != manifest.RunId ||
                reader.GetString(9) != manifest.SessionId ||
                reader.GetString(2) != RuntimeUsageSourceReceiptContract.Hash(
                    storedSource.Registration, storedSource.Usage) ||
                meterSource != SdkMeterSources.CopilotNanoAiu || ledgerUsage != ownerReceipt.Usage ||
                accounting.SourceReceiptId != receiptId || accounting.EventId != eventId ||
                accounting.CanonicalPayloadHash != canonicalInputHash ||
                accounting.AccountingCursor != accountingRevision ||
                accounting.DispatchId != manifest.DispatchId ||
                accounting.Attribution != ownerReceipt.Usage.Attribution ||
                accounting.Disposition != disposition || accounting.Amount != amount || accounting.Unit != unit ||
                accounting.RateCardId != (reader.IsDBNull(17) ? null : reader.GetString(17)) ||
                accounting.RateCardVersion != (reader.IsDBNull(18) ? null : reader.GetString(18)) ||
                accounting.RateCardId is not null &&
                    accounting.Unit != (reader.IsDBNull(19) ? null : reader.GetString(19)))
                throw new NativeUsageReconciliationException(
                    "runtime_usage_source_completion_mismatch", [receiptId]);

            joined.Add(new(identity, accounting, accountingRevision));
        }

        await reader.CloseAsync().ConfigureAwait(false);
        var missing = expected.Keys.Where(receiptId => !found.Contains(receiptId)).ToImmutableArray();
        if (!missing.IsEmpty)
            throw new NativeUsageReconciliationException(
                "runtime_usage_source_completion_incomplete", missing);
        return joined.ToImmutable();
    }

    private async Task<(long AccountingRevision, string CanonicalManifest)?> ReadSourceCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageDispatchSourceCompletionManifest manifest,
        Guid dispatchId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT accounting_revision, canonical_manifest
            FROM {_completions}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND dispatch_id = @dispatch
            FOR UPDATE
            """, connection, transaction);
        AddManifestScope(command, manifest, dispatchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<long> AllocateNextAccountingRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageDispatchSourceCompletionManifest manifest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT COALESCE(MAX(accounting_revision), 0)
            FROM (
                SELECT accounting_revision FROM {_usageLedger}
                WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
                UNION ALL
                SELECT accounting_revision FROM {_completions}
                WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run
            ) revisions
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, manifest.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, manifest.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, manifest.RunId);
        var maximum = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The accounting watermark query returned no value."));
        return checked(maximum + 1);
    }

    private async Task InsertSourceCompletionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageDispatchSourceCompletionManifest manifest,
        Guid dispatchId,
        long accountingRevision,
        string canonicalManifest,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_completions} (
                tenant_id, project_id, run_id, dispatch_id, session_id, runtime_instance_id,
                registration_revision, execution_fence, completion_receipt_id, completion_revision,
                accounting_revision, receipt_digest, canonical_manifest, manifest_json)
            VALUES (
                @tenant, @project, @run, @dispatch, @session, @runtime,
                @registration_revision, @execution_fence, @completion_receipt, @completion_revision,
                @accounting_revision, @receipt_digest, @canonical_manifest, @manifest_json)
            """, connection, transaction);
        AddManifestScope(command, manifest, dispatchId);
        command.Parameters.AddWithValue("session", NpgsqlDbType.Varchar, manifest.SessionId);
        command.Parameters.AddWithValue("runtime", NpgsqlDbType.Uuid, manifest.RuntimeInstanceId);
        command.Parameters.AddWithValue("registration_revision", NpgsqlDbType.Bigint, manifest.RegistrationRevision);
        command.Parameters.AddWithValue("execution_fence", NpgsqlDbType.Bigint, manifest.ExecutionFence);
        command.Parameters.AddWithValue("completion_receipt", NpgsqlDbType.Uuid, manifest.CompletionReceiptId);
        command.Parameters.AddWithValue("completion_revision", NpgsqlDbType.Bigint, manifest.CompletionRevision);
        command.Parameters.AddWithValue("accounting_revision", NpgsqlDbType.Bigint, accountingRevision);
        command.Parameters.AddWithValue("receipt_digest", NpgsqlDbType.Varchar, manifest.ReceiptDigest);
        command.Parameters.AddWithValue("canonical_manifest", NpgsqlDbType.Text, canonicalManifest);
        command.Parameters.AddWithValue("manifest_json", NpgsqlDbType.Jsonb, canonicalManifest);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddManifestScope(
        NpgsqlCommand command, UsageDispatchSourceCompletionManifest manifest, Guid dispatchId)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, manifest.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, manifest.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, manifest.RunId);
        command.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
    }

    private sealed record VerifiedDispatchReceipt(
        UsageDispatchSourceReceiptIdentity Identity,
        UsageAccountingReceipt Accounting,
        long AccountingRevision);

    private async Task<RunCostBinding> ReadOrPinBindingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, UsageSubmission usage,
        CancellationToken cancellationToken) =>
        await ReadOrPinBindingAsync(
            connection, transaction, CostRunScope.From(usage), cancellationToken).ConfigureAwait(false);

    private async Task<RunCostBinding> ReadOrPinBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CostRunScope scope,
        CancellationToken cancellationToken)
    {
        await using (var read = new NpgsqlCommand($"""
            SELECT binding_json, unpriced_reason FROM {_bindings}
            WHERE tenant_id = @tenant AND project_id = @project AND run_id = @run AND meter_source = @meter
            """, connection, transaction))
        {
            AddScope(read, scope);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new(reader.IsDBNull(0) ? null : Deserialize<CostBinding>(reader.GetString(0)),
                    reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        var resolution = bindings.ResolveAndPin(scope.MeterSource, scope.RunId);
        var pinned = resolution.IsSuccess && resolution.Value is not null
            ? new RunCostBinding(resolution.Value, null)
            : new RunCostBinding(null, resolution.Error?.Code.ToString() ?? "cost-provider-unavailable");
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_bindings}(tenant_id, project_id, run_id, meter_source, binding_json, unpriced_reason)
            VALUES (@tenant, @project, @run, @meter, @binding, @reason)
            """, connection, transaction);
        AddScope(insert, scope);
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
        if (!_providers.TryGetValue(usage.ModelBinding.MeterSource, out var provider))
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "cost-provider-unavailable");
        var verified = bindings.VerifyPinned(
            usage.ModelBinding.MeterSource, usage.Attribution.RunId, pinned.Binding);
        if (!verified.IsSuccess || verified.Value is null)
            return new(null, pinned.Binding.RateCard.Unit, CostDisposition.Unpriced,
                pinned.Binding.RateCard, "pinned-cost-binding-unavailable");
        return provider.Price(usage.Measurement, usage.ModelBinding, pinned.Binding);
    }

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
        var unpriced = ImmutableArray.CreateBuilder<Guid>();
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
            if (accounting.Disposition == CostDisposition.Unpriced)
                unpriced.Add(sourceReceiptId);
            else
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
        if (unpriced.Count > 0)
            throw new NativeUsageReconciliationException(
                "runtime_usage_reconciliation_unpriced", unpriced.ToImmutable());
        return represented.ToImmutable();
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

    private NativeUsageCostPreflightResult QuotePreflight(
        string meterSource,
        string modelId,
        string runId,
        RunCostBinding pinned)
    {
        if (pinned.Binding is null)
            return new(null, false, pinned.UnpricedReason ?? "cost-provider-unavailable");
        if (!_providers.TryGetValue(meterSource, out var provider))
            return new(pinned.Binding, false, "cost-provider-unavailable");

        var verified = bindings.VerifyPinned(meterSource, runId, pinned.Binding);
        if (!verified.IsSuccess || verified.Value is null)
            return new(pinned.Binding, false, "pinned-cost-binding-unavailable");

        var quote = provider.Quote(
            new CostQuoteRequest(modelId, 1m, CostQuoteBasis.UnweightedAiCredits),
            pinned.Binding);
        return quote.Disposition == CostDisposition.Estimate &&
               quote.Amount is not null &&
               quote.Unit == pinned.Binding.RateCard.Unit &&
               CopilotCostProvider.RateCardsEqual(quote.RateCard, pinned.Binding.RateCard)
            ? new(pinned.Binding, true, null)
            : new(pinned.Binding, false, quote.UnpricedReason ?? "cost-quote-invalid");
    }

    private static void AddScope(NpgsqlCommand command, UsageSubmission usage) =>
        AddScope(command, CostRunScope.From(usage));

    private static void AddScope(NpgsqlCommand command, CostRunScope scope)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, scope.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, scope.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, scope.RunId);
        command.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, scope.MeterSource);
    }

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
