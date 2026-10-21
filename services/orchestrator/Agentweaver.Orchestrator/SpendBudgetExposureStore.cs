using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Spend budgets that apply to one dispatch: run and agent budgets pinned by the accepted selection, and
/// the current project budgets read for this dispatch.
/// </summary>
/// <param name="PinnedBudgets">Run and agent budgets from the accepted selection.</param>
/// <param name="Project">Current project budgets, or <see langword="null"/> when the project has none.</param>
internal sealed record SpendBudgetAdmissionContext(
    ImmutableArray<SpendBudget> PinnedBudgets, CurrentProjectSpendBudgets? Project)
{
    /// <summary>No budgets apply.</summary>
    public static SpendBudgetAdmissionContext None { get; } = new([], null);

    /// <summary>Whether any budget is configured.</summary>
    public bool IsEmpty => PinnedBudgets.IsDefaultOrEmpty && (Project is null || Project.Budgets.IsDefaultOrEmpty);
}

/// <summary>Result of preparing a native dispatch.</summary>
/// <param name="CreditDenial">Copilot hard credit limit denial, when the credit limit blocks the send.</param>
/// <param name="SpendBudgetDenialCode">Spend budget denial code, when a spend budget blocks the send.</param>
internal sealed record RuntimeNativePreparationOutcome(
    RuntimeNativeCreditDenialReceipt? CreditDenial, string? SpendBudgetDenialCode);

/// <summary>One budget that applies to a dispatch, with its run-qualified bucket key.</summary>
/// <param name="Budget">The budget.</param>
/// <param name="BucketKey">Stable bucket identity; run and agent buckets include the run ID.</param>
/// <param name="ProjectConfigurationRevision">Project configuration revision that supplied the budget.</param>
internal sealed record ApplicableSpendBudget(SpendBudget Budget, string BucketKey, long ProjectConfigurationRevision);

/// <summary>Result of evaluating one budget bucket.</summary>
/// <param name="DenialCode">Error code when the dispatch can't be sent, otherwise <see langword="null"/>.</param>
/// <param name="SoftLimitReached">Whether priced committed spend reached the soft limit.</param>
internal sealed record SpendBudgetDecision(string? DenialCode, bool SoftLimitReached)
{
    /// <summary>Whether the dispatch is admitted for this bucket.</summary>
    public bool Admitted => DenialCode is null;
}

/// <summary>Pure spend budget selection and admission rules.</summary>
internal static class SpendBudgetAdmissionMath
{
    /// <summary>Error code for a hard limit that committed spend plus exposure would exceed.</summary>
    public const string Exhausted = "runtime_spend_budget_exhausted";
    /// <summary>Error code for a hard budget whose Events evidence can't be read or validated.</summary>
    public const string EvidenceUnavailable = "runtime_spend_evidence_unavailable";
    /// <summary>Error code for Events evidence older than evidence that admission already observed.</summary>
    public const string EvidenceStale = "runtime_spend_evidence_stale";
    /// <summary>Error code for a hard budget whose bucket contains unpriced usage.</summary>
    public const string PricingMissing = "runtime_spend_budget_pricing_missing";
    /// <summary>Error code for a hard project budget whose configured periods don't cover the current time.</summary>
    public const string PeriodUncovered = "runtime_spend_budget_period_uncovered";
    /// <summary>Error code for a hard budget with more outstanding dispatches than one evidence read can witness.</summary>
    public const string ExposureUnbounded = "runtime_spend_budget_exposure_unbounded";
    /// <summary>Error code for a retried dispatch whose stored reservation differs from the new request.</summary>
    public const string ReservationConflict = "runtime_spend_budget_reservation_conflict";

    /// <summary>
    /// Selects the budgets that limit a dispatch on <paramref name="meterSource"/>: every run budget, agent
    /// budgets for <paramref name="agentId"/>, and project budgets whose period contains <paramref name="now"/>.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">
    /// A hard project budget exists for the meter and unit, but no configured period contains the current time.
    /// </exception>
    public static ImmutableArray<ApplicableSpendBudget> SelectApplicable(
        SpendBudgetAdmissionContext context, string runId, string agentId, string meterSource,
        long pinnedProjectConfigurationRevision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        var selected = ImmutableArray.CreateBuilder<ApplicableSpendBudget>();
        foreach (var budget in context.PinnedBudgets.IsDefault ? [] : context.PinnedBudgets)
        {
            if (budget.MeterSource != meterSource)
                continue;
            if (budget.Scope == SpendBudgetScopes.Run ||
                budget.Scope == SpendBudgetScopes.Agent && budget.AgentId == agentId)
                selected.Add(new(budget, RunBucketKey(runId, budget), pinnedProjectConfigurationRevision));
        }
        if (context.Project is { } project && !project.Budgets.IsDefault)
        {
            var projectBudgets = project.Budgets.Where(budget =>
                budget.Scope == SpendBudgetScopes.Project && budget.MeterSource == meterSource).ToArray();
            foreach (var budget in projectBudgets.Where(budget => Covers(budget.Period!, now)))
                selected.Add(new(budget, SpendBudgetContract.BucketKey(budget), project.ProjectConfigurationRevision));
            foreach (var uncovered in projectBudgets.Where(budget => budget.HardLimit is not null &&
                         !Covers(budget.Period!, now)))
                if (!projectBudgets.Any(other => other.Unit == uncovered.Unit && other.HardLimit is not null &&
                        Covers(other.Period!, now)))
                    throw new RuntimeAuthorizationException(PeriodUncovered);
        }
        return [.. selected.OrderBy(item => item.BucketKey, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Decides one bucket. A hard limit admits only when priced committed spend, outstanding exposure, and
    /// the new reserve stay within the limit. A soft-only budget never blocks.
    /// </summary>
    /// <param name="budget">The budget.</param>
    /// <param name="committed">Events total for the bucket, or <see langword="null"/> when evidence is unavailable or stale.</param>
    /// <param name="outstandingExposure">Reserves held by other admitted dispatches that Events hasn't accounted for.</param>
    /// <param name="staleEvidence">Whether the evidence was older than evidence that admission already observed.</param>
    public static SpendBudgetDecision Decide(
        SpendBudget budget, UsageSpendTotal? committed, decimal outstandingExposure, bool staleEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (committed is null)
            return new(budget.HardLimit is null ? null : staleEvidence ? EvidenceStale : EvidenceUnavailable, false);
        var softReached = budget.SoftLimit is { } soft && committed.PricedAmount >= soft;
        if (budget.HardLimit is not { } hard)
            return new(null, softReached);
        if (committed.UnpricedEvents > 0)
            return new(PricingMissing, softReached);
        return committed.PricedAmount + outstandingExposure + budget.ReservePerDispatch > hard
            ? new(Exhausted, softReached)
            : new(null, softReached);
    }

    /// <summary>Returns the Events total that a budget's scope limits.</summary>
    public static UsageSpendTotal? CommittedTotal(SpendBudget budget, UsageSpendEvidence evidence) => budget.Scope switch
    {
        SpendBudgetScopes.Project => evidence.ProjectPeriod,
        SpendBudgetScopes.Agent => evidence.Agent,
        _ => evidence.Run
    };

    /// <summary>
    /// Whether Events witnesses every receipt that the completed dispatch acknowledged. Partial receipts never
    /// retire exposure.
    /// </summary>
    public static bool IsAccounted(
        ImmutableHashSet<Guid> requiredReceipts, UsageDispatchAccountingWitness witness) =>
        requiredReceipts.All(required => witness.Receipts.Any(receipt => receipt.SourceReceiptId == required));

    /// <summary>Builds the Events request for a budget bucket and its outstanding dispatches.</summary>
    public static UsageSpendEvidenceRequest EvidenceRequest(SpendBudget budget, ImmutableArray<string> dispatchIds) =>
        new(UsageSpendEvidenceContract.CurrentVersion, budget.MeterSource, budget.Unit,
            budget.Scope == SpendBudgetScopes.Project ? budget.Period : null, dispatchIds)
        {
            AgentId = budget.Scope == SpendBudgetScopes.Agent ? budget.AgentId : null
        };

    private static string RunBucketKey(string runId, SpendBudget budget) =>
        string.Join('\u001f', "run", runId, SpendBudgetContract.BucketKey(budget));

    private static bool Covers(UsageSpendPeriod period, DateTimeOffset now) =>
        period.StartsAt <= now && now < period.EndsAt;
}

/// <summary>
/// Serializes spend budget admission against committed Events spend plus outstanding dispatch exposure.
/// Events stays the only usage ledger: this store keeps reservations, the highest observed Events cursor,
/// and notification markers in the outbox, never spend totals.
/// </summary>
internal sealed class SpendBudgetExposureStore(
    NpgsqlDataSource dataSource, OrchestratorOptions options, TimeProvider timeProvider)
{
    private readonly string _reservations = $"\"{options.Schema}\".spend_budget_reservations";
    private readonly string _cursors = $"\"{options.Schema}\".spend_budget_accounting_cursors";
    private readonly string _dispatches = $"\"{options.Schema}\".maf_execution_dispatches";
    private readonly string _outboxEvents = $"\"{options.Schema}\".outbox_events";
    private readonly PostgresOutbox _outbox = new(dataSource, options.Schema);

    private sealed record Pending(ApplicableSpendBudget Applicable, bool AlreadyReserved);

    /// <summary>
    /// Admits one dispatch against every applicable budget and records its reservations, or returns the
    /// denial code. On denial nothing is reserved, so the caller can commit the denial evidence and stop before send.
    /// </summary>
    /// <param name="connection">The caller's connection.</param>
    /// <param name="transaction">The caller's transaction, which also inserts the dispatch.</param>
    /// <param name="registration">Runtime registration for the dispatch.</param>
    /// <param name="meterSource">Meter source of the dispatch's model session.</param>
    /// <param name="dispatchId">Dispatch identity, which is also the reservation identity.</param>
    /// <param name="budgets">Budgets configured for the run.</param>
    /// <param name="readEvidence">Reads Events spend evidence for one bucket.</param>
    /// <param name="cancellationToken">Cancels database and Events operations.</param>
    /// <returns>The denial code, or <see langword="null"/> when the dispatch is admitted.</returns>
    internal async Task<string?> AdmitWithinTransactionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string meterSource, Guid dispatchId, SpendBudgetAdmissionContext budgets,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> readEvidence,
        CancellationToken cancellationToken)
    {
        if (budgets.IsEmpty)
            return null;
        var binding = registration.Binding;
        var applicable = SpendBudgetAdmissionMath.SelectApplicable(
            budgets, binding.RunId, binding.AgentId, meterSource, binding.ProjectConfigurationRevision,
            timeProvider.GetUtcNow());
        if (applicable.IsEmpty)
            return null;
        foreach (var item in applicable)
        {
            await using var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtext('agentweaver.spend_budget'), hashtext(@key))",
                connection, transaction);
            lockCommand.Parameters.AddWithValue("key", NpgsqlDbType.Text,
                string.Join('\u001f', binding.TenantId, binding.ProjectId, item.BucketKey));
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var pending = new List<Pending>();
        string? denial = null;
        foreach (var item in applicable)
        {
            if (await ReadExistingReservationAsync(
                    connection, transaction, registration, dispatchId, item, cancellationToken).ConfigureAwait(false))
            {
                pending.Add(new(item, true));
                continue;
            }
            var decision = await EvaluateBucketAsync(
                connection, transaction, registration, meterSource, dispatchId, item, readEvidence,
                cancellationToken).ConfigureAwait(false);
            if (decision.SoftLimitReached)
                await EnqueueNotificationAsync(connection, transaction, registration, item, null,
                    "orchestrator.run.spend_budget_soft_limit_reached", cancellationToken).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                await EnqueueNotificationAsync(connection, transaction, registration, item, dispatchId,
                    "orchestrator.run.spend_budget_dispatch_denied", cancellationToken,
                    decision.DenialCode).ConfigureAwait(false);
                denial ??= decision.DenialCode;
            }
            pending.Add(new(item, false));
        }
        if (denial is not null)
            return denial;
        foreach (var item in pending.Where(item => !item.AlreadyReserved &&
                     item.Applicable.Budget.ReservePerDispatch > 0))
            await InsertReservationAsync(
                connection, transaction, registration, dispatchId, item.Applicable, cancellationToken)
                .ConfigureAwait(false);
        return null;
    }

    private async Task<SpendBudgetDecision> EvaluateBucketAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string meterSource, Guid dispatchId, ApplicableSpendBudget item,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> readEvidence,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var budget = item.Budget;
        var outstanding = new List<(Guid DispatchId, string RunId, decimal Reserve)>();
        await using (var read = new NpgsqlCommand($"""
            SELECT dispatch_id, run_id, reserve_amount FROM {_reservations}
            WHERE tenant_id = @tenant AND project_id = @project AND bucket_key = @bucket
                AND reservation_state = 'outstanding' AND dispatch_id <> @dispatch
            ORDER BY created_at, dispatch_id
            """, connection, transaction))
        {
            AddBucketScope(read, binding, item);
            read.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                outstanding.Add((reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
        }
        if (budget.HardLimit is not null && outstanding.Count > UsageSpendEvidenceContract.MaximumDispatches)
            return new(SpendBudgetAdmissionMath.ExposureUnbounded, false);
        var witnessed = outstanding.Take(UsageSpendEvidenceContract.MaximumDispatches).ToArray();
        var request = SpendBudgetAdmissionMath.EvidenceRequest(
            budget, [.. witnessed.Select(item => item.DispatchId.ToString("D"))]);
        UsageSpendEvidence evidence;
        try
        {
            evidence = await readEvidence(request, cancellationToken).ConfigureAwait(false);
            UsageSpendEvidenceContract.ValidateEvidence(
                evidence, request, binding.TenantId, binding.ProjectId, binding.RunId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return SpendBudgetAdmissionMath.Decide(budget, null, 0m);
        }
        if (!await AdvanceCursorAsync(connection, transaction, binding, meterSource, evidence.AccountingCursor,
                cancellationToken).ConfigureAwait(false))
            return SpendBudgetAdmissionMath.Decide(budget, null, 0m, staleEvidence: true);

        var retired = new HashSet<Guid>();
        for (var index = 0; index < witnessed.Length; index++)
        {
            var required = await ReadAccountedReceiptsAsync(
                connection, transaction, binding.ProjectId, witnessed[index].RunId, witnessed[index].DispatchId,
                cancellationToken).ConfigureAwait(false);
            if (required is null || !SpendBudgetAdmissionMath.IsAccounted(required, evidence.Dispatches[index]))
                continue;
            await using var retire = new NpgsqlCommand($"""
                UPDATE {_reservations}
                SET reservation_state = 'retired', retired_cursor = @cursor, retired_at = clock_timestamp()
                WHERE dispatch_id = @dispatch AND bucket_key = @bucket AND reservation_state = 'outstanding'
                """, connection, transaction);
            retire.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, witnessed[index].DispatchId);
            retire.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, item.BucketKey);
            retire.Parameters.AddWithValue("cursor", NpgsqlDbType.Bigint, evidence.AccountingCursor);
            await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            retired.Add(witnessed[index].DispatchId);
        }
        var exposure = outstanding.Where(reservation => !retired.Contains(reservation.DispatchId))
            .Sum(reservation => reservation.Reserve);
        return SpendBudgetAdmissionMath.Decide(
            budget, SpendBudgetAdmissionMath.CommittedTotal(budget, evidence), exposure);
    }

    /// <summary>
    /// Returns the source receipts that a completed, accounted dispatch acknowledged, or <see langword="null"/>
    /// while accounting is still pending.
    /// </summary>
    private async Task<ImmutableHashSet<Guid>?> ReadAccountedReceiptsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string projectId, string runId,
        Guid dispatchId, CancellationToken cancellationToken)
    {
        await using var read = new NpgsqlCommand($"""
            SELECT acknowledgment_references::text, source_completion_manifest_json::text
            FROM {_dispatches}
            WHERE project_id = @project AND run_id = @run AND dispatch_id = @dispatch
                AND dispatch_state = 'completed' AND accounting_status IN ('priced', 'unpriced')
            """, connection, transaction);
        read.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
        read.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, runId);
        read.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var required = ImmutableHashSet.CreateBuilder<Guid>();
        using (var references = JsonDocument.Parse(reader.GetString(0)))
            foreach (var reference in references.RootElement.EnumerateArray())
                required.Add(Property(reference, "sourceReceiptId").GetGuid());
        if (!reader.IsDBNull(1))
        {
            using var manifest = JsonDocument.Parse(reader.GetString(1));
            foreach (var receipt in Property(manifest.RootElement, "sourceReceipts").EnumerateArray())
                required.Add(Property(receipt, "sourceReceiptId").GetGuid());
        }
        return required.ToImmutable();
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        throw new RuntimeAuthorizationException("runtime_native_turn_accounting_invalid");
    }

    /// <summary>Moves the per-meter cursor forward, or returns <see langword="false"/> when the evidence is stale.</summary>
    private async Task<bool> AdvanceCursorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeBinding binding, string meterSource,
        long cursor, CancellationToken cancellationToken)
    {
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {_cursors} (tenant_id, project_id, meter_source, accounting_cursor)
            VALUES (@tenant, @project, @meter, @cursor)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddCursorScope(insert, binding, meterSource, cursor);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        long stored;
        await using (var read = new NpgsqlCommand($"""
            SELECT accounting_cursor FROM {_cursors}
            WHERE tenant_id = @tenant AND project_id = @project AND meter_source = @meter
            FOR UPDATE
            """, connection, transaction))
        {
            AddCursorScope(read, binding, meterSource, cursor);
            stored = (long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        if (cursor < stored)
            return false;
        if (cursor == stored)
            return true;
        await using var update = new NpgsqlCommand($"""
            UPDATE {_cursors} SET accounting_cursor = @cursor, updated_at = clock_timestamp()
            WHERE tenant_id = @tenant AND project_id = @project AND meter_source = @meter
            """, connection, transaction);
        AddCursorScope(update, binding, meterSource, cursor);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns whether a retried dispatch already holds its reservation. A stored reservation that differs
    /// from the current budget or registration is a conflict.
    /// </summary>
    private async Task<bool> ReadExistingReservationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        Guid dispatchId, ApplicableSpendBudget item, CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        await using var read = new NpgsqlCommand($"""
            SELECT tenant_id, project_id, run_id, agent_id, budget_id, budget_revision, reserve_amount,
                project_configuration_revision, accepted_selection_hash
            FROM {_reservations} WHERE dispatch_id = @dispatch AND bucket_key = @bucket
            """, connection, transaction);
        read.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        read.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, item.BucketKey);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return false;
        if (reader.GetString(0) != binding.TenantId || reader.GetString(1) != binding.ProjectId ||
            reader.GetString(2) != binding.RunId || reader.GetString(3) != binding.AgentId ||
            reader.GetString(4) != item.Budget.BudgetId || reader.GetInt64(5) != item.Budget.Revision ||
            reader.GetDecimal(6) != item.Budget.ReservePerDispatch ||
            reader.GetInt64(7) != item.ProjectConfigurationRevision ||
            !string.Equals(reader.GetString(8), binding.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeAuthorizationException(SpendBudgetAdmissionMath.ReservationConflict);
        return true;
    }

    private async Task InsertReservationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        Guid dispatchId, ApplicableSpendBudget item, CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var budget = item.Budget;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {_reservations}
                (dispatch_id, bucket_key, tenant_id, project_id, run_id, agent_id, budget_id, budget_revision,
                 scope, meter_source, unit, period_id, period_starts_at, period_ends_at, reserve_amount,
                 project_configuration_revision, accepted_selection_hash)
            VALUES (@dispatch, @bucket, @tenant, @project, @run, @agent, @budget, @revision,
                @scope, @meter, @unit, @period, @starts, @ends, @reserve, @configuration, @selection)
            ON CONFLICT DO NOTHING
            """, connection, transaction);
        AddBucketScope(insert, binding, item);
        insert.Parameters.AddWithValue("dispatch", NpgsqlDbType.Uuid, dispatchId);
        insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, binding.RunId);
        insert.Parameters.AddWithValue("agent", NpgsqlDbType.Varchar, binding.AgentId);
        insert.Parameters.AddWithValue("budget", NpgsqlDbType.Varchar, budget.BudgetId);
        insert.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, budget.Revision);
        insert.Parameters.AddWithValue("scope", NpgsqlDbType.Varchar, budget.Scope);
        insert.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, budget.MeterSource);
        insert.Parameters.AddWithValue("unit", NpgsqlDbType.Varchar, budget.Unit);
        insert.Parameters.AddWithValue("period", NpgsqlDbType.Varchar, (object?)budget.Period?.PeriodId ?? DBNull.Value);
        insert.Parameters.AddWithValue("starts", NpgsqlDbType.TimestampTz,
            budget.Period is { } start ? start.StartsAt : DBNull.Value);
        insert.Parameters.AddWithValue("ends", NpgsqlDbType.TimestampTz,
            budget.Period is { } end ? end.EndsAt : DBNull.Value);
        insert.Parameters.AddWithValue("reserve", NpgsqlDbType.Numeric, budget.ReservePerDispatch);
        insert.Parameters.AddWithValue("configuration", NpgsqlDbType.Bigint, item.ProjectConfigurationRevision);
        insert.Parameters.AddWithValue("selection", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0 &&
            !await ReadExistingReservationAsync(
                connection, transaction, registration, dispatchId, item, cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException(SpendBudgetAdmissionMath.ReservationConflict);
    }

    /// <summary>
    /// Enqueues a notification once. Soft notifications are keyed by bucket, period, and budget revision;
    /// denial notifications also include the dispatch.
    /// </summary>
    private async Task EnqueueNotificationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        ApplicableSpendBudget item, Guid? dispatchId, string eventType, CancellationToken cancellationToken,
        string? denialCode = null)
    {
        var binding = registration.Binding;
        var budget = item.Budget;
        var key = string.Join('\0', eventType, binding.TenantId, binding.ProjectId, item.BucketKey,
            budget.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            dispatchId?.ToString("D") ?? string.Empty);
        var notificationId = Guid.ParseExact(
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(key))[..32], "N");
        await using (var exists = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_outboxEvents} WHERE id = @id)", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, notificationId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return;
        }
        await _outbox.EnqueueAsync(connection, transaction, new OutboxEvent(
            notificationId, $"coordination/{binding.ProjectId}/{binding.RunId}/spend-budgets",
            $"spend-budget/{notificationId:N}", eventType, 1,
            JsonSerializer.SerializeToElement(new
            {
                notificationId,
                binding.ProjectId,
                binding.RunId,
                binding.AgentId,
                budget.BudgetId,
                budgetRevision = budget.Revision,
                budget.Scope,
                budget.MeterSource,
                budget.Unit,
                periodId = budget.Period?.PeriodId,
                budget.SoftLimit,
                budget.HardLimit,
                dispatchId,
                code = denialCode
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), timeProvider.GetUtcNow()), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddBucketScope(NpgsqlCommand command, RuntimeBinding binding, ApplicableSpendBudget item)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, item.BucketKey);
    }

    private static void AddCursorScope(
        NpgsqlCommand command, RuntimeBinding binding, string meterSource, long cursor)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, meterSource);
        command.Parameters.AddWithValue("cursor", NpgsqlDbType.Bigint, cursor);
    }
}
