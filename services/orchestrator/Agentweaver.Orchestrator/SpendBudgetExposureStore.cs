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
        // Run and agent budgets come from the accepted selection, so they use the project configuration
        // revision that the selection pinned. Editing the project later doesn't change them for this run.
        foreach (var budget in context.PinnedBudgets.IsDefault ? [] : context.PinnedBudgets)
        {
            // A budget limits only usage from its own meter, for example one model provider.
            if (budget.MeterSource != meterSource)
                continue;
            // Run budgets apply to every agent in the run. Agent budgets apply only to the named agent.
            // The bucket key includes the run ID so two runs never share run or agent exposure.
            if (budget.Scope == SpendBudgetScopes.Run ||
                budget.Scope == SpendBudgetScopes.Agent && budget.AgentId == agentId)
                selected.Add(new(budget, RunBucketKey(runId, budget), pinnedProjectConfigurationRevision));
        }
        // Project budgets are read from the current project configuration for each new dispatch, so a
        // budget edit takes effect on the next dispatch, even inside a run that is already in progress.
        if (context.Project is { } project && !project.Budgets.IsDefault)
        {
            var projectBudgets = project.Budgets.Where(budget =>
                budget.Scope == SpendBudgetScopes.Project && budget.MeterSource == meterSource).ToArray();
            // Only the period that contains the current time limits this dispatch. The bucket key has no
            // run ID, so every run in the project shares the same project bucket.
            foreach (var budget in projectBudgets.Where(budget => Covers(budget.Period!, now)))
                selected.Add(new(budget, SpendBudgetContract.BucketKey(budget), project.ProjectConfigurationRevision));
            // A hard project limit must always cover the current time. If a hard budget exists for this meter
            // and unit but none of its periods contains "now", the send fails closed instead of running
            // without a limit. Another hard budget for the same unit whose period covers "now" is enough.
            foreach (var uncovered in projectBudgets.Where(budget => budget.HardLimit is not null &&
                         !Covers(budget.Period!, now)))
                if (!projectBudgets.Any(other => other.Unit == uncovered.Unit && other.HardLimit is not null &&
                        Covers(other.Period!, now)))
                    throw new RuntimeAuthorizationException(PeriodUncovered);
        }
        // Sort by bucket key so every admission takes the per-bucket locks in the same order. A fixed lock
        // order prevents deadlocks between concurrent admissions that share more than one bucket.
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
        // Without usable evidence, a hard budget fails closed: the send is denied because spend can't be
        // proven to be under the limit. A soft-only budget fails open because it only notifies.
        if (committed is null)
            return new(budget.HardLimit is null ? null : staleEvidence ? EvidenceStale : EvidenceUnavailable, false);
        // The soft limit compares only committed priced spend. Exposure isn't spent yet, so it doesn't
        // trigger the soft-limit notification.
        var softReached = budget.SoftLimit is { } soft && committed.PricedAmount >= soft;
        if (budget.HardLimit is not { } hard)
            return new(null, softReached);
        // Unpriced usage has an unknown cost, so the bucket total can't be compared with a hard limit.
        if (committed.UnpricedEvents > 0)
            return new(PricingMissing, softReached);
        // Count the full reserve of every outstanding dispatch plus the reserve of this new dispatch.
        // This is the worst case if all of them spend their full reserve before Events records the usage.
        return committed.PricedAmount + outstandingExposure + budget.ReservePerDispatch > hard
            ? new(Exhausted, softReached)
            : new(null, softReached);
    }

    /// <summary>Returns the Events total that a budget's scope limits.</summary>
    public static UsageSpendTotal? CommittedTotal(SpendBudget budget, UsageSpendEvidence evidence) => budget.Scope switch
    {
        // Events returns one total per scope. Project budgets use the total for the budget period.
        // Any other scope value falls through to the run total.
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
        // Only project budgets send a period, because run and agent totals cover the whole run.
        // The agent filter is set only for agent budgets. The dispatch IDs ask Events to return an
        // accounting witness for each outstanding dispatch, in the same order.
        new(UsageSpendEvidenceContract.CurrentVersion, budget.MeterSource, budget.Unit,
            budget.Scope == SpendBudgetScopes.Project ? budget.Period : null, dispatchIds)
        {
            AgentId = budget.Scope == SpendBudgetScopes.Agent ? budget.AgentId : null
        };

    // Prefixes the budget bucket key with "run" and the run ID, separated by the unit separator control
    // character (U+001F). The key has no revision or limits, so editing a budget keeps prior exposure in the
    // same bucket.
    private static string RunBucketKey(string runId, SpendBudget budget) =>
        string.Join('\u001f', "run", runId, SpendBudgetContract.BucketKey(budget));

    // A period includes its start time and excludes its end time, so back-to-back periods never overlap.
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

    // One evaluated bucket. AlreadyReserved is true when a retry of this dispatch already stored the
    // reservation, so admission doesn't insert it again.
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
        // No budget means no limit, so skip the locks and the Events read.
        if (budgets.IsEmpty)
            return null;
        var binding = registration.Binding;
        // Selection can throw PeriodUncovered when a hard project budget has no period for the current time.
        var applicable = SpendBudgetAdmissionMath.SelectApplicable(
            budgets, binding.RunId, binding.AgentId, meterSource, binding.ProjectConfigurationRevision,
            timeProvider.GetUtcNow());
        if (applicable.IsEmpty)
            return null;
        // Lock every bucket before reading anything. Concurrent admissions in other runs can share a
        // project bucket, and without the lock two of them could each see room for one more dispatch and
        // both be admitted. The advisory lock is transaction scoped, so PostgreSQL releases it on commit or
        // rollback. The buckets are already sorted, so every caller takes the locks in the same order and
        // can't deadlock. Tenant and project are part of the lock key so other tenants never wait.
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
        // Evaluate every bucket, even after one denies, so each bucket can still record its notifications
        // and retire accounted reservations in this transaction.
        foreach (var item in applicable)
        {
            // A retry of an admitted dispatch already holds this reservation. Don't evaluate it again, or
            // its own reserve would count twice and a dispatch that was admitted could be denied.
            if (await ReadExistingReservationAsync(
                    connection, transaction, registration, dispatchId, item, cancellationToken).ConfigureAwait(false))
            {
                pending.Add(new(item, true));
                continue;
            }
            var decision = await EvaluateBucketAsync(
                connection, transaction, registration, meterSource, dispatchId, item, readEvidence,
                cancellationToken).ConfigureAwait(false);
            // The soft-limit notification has no dispatch ID in its key, so it's sent once per bucket,
            // period, and budget revision, not once per dispatch.
            if (decision.SoftLimitReached)
                await EnqueueNotificationAsync(connection, transaction, registration, item, null,
                    "orchestrator.run.spend_budget_soft_limit_reached", cancellationToken).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                await EnqueueNotificationAsync(connection, transaction, registration, item, dispatchId,
                    "orchestrator.run.spend_budget_dispatch_denied", cancellationToken,
                    decision.DenialCode).ConfigureAwait(false);
                // Report the first denial in bucket order so the code is the same on every retry.
                denial ??= decision.DenialCode;
            }
            pending.Add(new(item, false));
        }
        // On denial, return before inserting any reservation. The caller still commits the transaction, so
        // notifications and retirements persist, but a denied dispatch never holds exposure.
        if (denial is not null)
            return denial;
        // Admission is all or nothing: reservations are inserted only after every bucket admitted.
        // A zero reserve adds no exposure, and the table rejects zero amounts, so it isn't stored.
        foreach (var item in pending.Where(item => !item.AlreadyReserved &&
                     item.Applicable.Budget.ReservePerDispatch > 0))
            await InsertReservationAsync(
                connection, transaction, registration, dispatchId, item.Applicable, cancellationToken)
                .ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Decides one locked bucket. Reads the outstanding reservations of other dispatches, reads Events
    /// evidence, retires reservations that Events fully accounts for, and compares committed spend plus the
    /// remaining exposure with the budget.
    /// </summary>
    private async Task<SpendBudgetDecision> EvaluateBucketAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeRegistration registration,
        string meterSource, Guid dispatchId, ApplicableSpendBudget item,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> readEvidence,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var budget = item.Budget;
        var outstanding = new List<(Guid DispatchId, string RunId, decimal Reserve)>();
        // Read the reservations that other dispatches still hold in this bucket. The current dispatch is
        // excluded because Decide adds its reserve separately. Ordering by creation time witnesses the
        // oldest dispatches first, and the dispatch ID breaks ties so the order is stable.
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
        // One Events read can witness at most MaximumDispatches dispatches (64). With more outstanding
        // dispatches, some reservations can't be checked or retired, so the exposure can't be bounded.
        // A hard budget denies in that case. A soft-only budget continues because it never blocks.
        if (budget.HardLimit is not null && outstanding.Count > UsageSpendEvidenceContract.MaximumDispatches)
            return new(SpendBudgetAdmissionMath.ExposureUnbounded, false);
        // For a soft-only budget, ask about the oldest dispatches only. The rest stay outstanding.
        var witnessed = outstanding.Take(UsageSpendEvidenceContract.MaximumDispatches).ToArray();
        var request = SpendBudgetAdmissionMath.EvidenceRequest(
            budget, [.. witnessed.Select(item => item.DispatchId.ToString("D"))]);
        UsageSpendEvidence evidence;
        try
        {
            evidence = await readEvidence(request, cancellationToken).ConfigureAwait(false);
            // Check that the response answers this exact request for this tenant, project, and run, with one
            // witness per requested dispatch. Evidence for a different scope is treated as unavailable.
            UsageSpendEvidenceContract.ValidateEvidence(
                evidence, request, binding.TenantId, binding.ProjectId, binding.RunId);
        }
        // Any read or validation failure means "no evidence": hard budgets deny and soft-only budgets
        // admit. Cancellation that the caller requested isn't caught, so it still stops the operation.
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            return SpendBudgetAdmissionMath.Decide(budget, null, 0m);
        }
        // Reject a response older than one that admission already used. Old evidence can miss recent
        // spend, so it could otherwise reopen budget capacity that was already used.
        if (!await AdvanceCursorAsync(connection, transaction, binding, meterSource, evidence.AccountingCursor,
                cancellationToken).ConfigureAwait(false))
            return SpendBudgetAdmissionMath.Decide(budget, null, 0m, staleEvidence: true);

        var retired = new HashSet<Guid>();
        // Events returns witnesses in request order, so evidence.Dispatches[index] belongs to
        // witnessed[index].
        for (var index = 0; index < witnessed.Length; index++)
        {
            // A reservation retires only when the dispatch finished accounting locally and Events shows
            // every receipt the dispatch acknowledged. Until then its spend can be missing from the Events
            // total, so the full reserve stays counted as exposure.
            var required = await ReadAccountedReceiptsAsync(
                connection, transaction, binding.ProjectId, witnessed[index].RunId, witnessed[index].DispatchId,
                cancellationToken).ConfigureAwait(false);
            if (required is null || !SpendBudgetAdmissionMath.IsAccounted(required, evidence.Dispatches[index]))
                continue;
            // Record the cursor that proved the accounting. The state filter makes the update a no-op
            // if the reservation is already retired.
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
        // Exposure is the full reserve of every reservation that is still outstanding. Retired dispatches
        // are excluded because their real spend is now part of the Events total.
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
        // Only a completed dispatch whose accounting finished (priced or unpriced) can be retired. A dispatch
        // that is still running or still accounting returns no row, so its reservation stays outstanding.
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
        // Required receipts are the union of the receipts the dispatch acknowledged and the receipts in its
        // completion manifest. Events must show all of them before the reservation retires.
        var required = ImmutableHashSet.CreateBuilder<Guid>();
        using (var references = JsonDocument.Parse(reader.GetString(0)))
            foreach (var reference in references.RootElement.EnumerateArray())
                required.Add(Property(reference, "sourceReceiptId").GetGuid());
        // The completion manifest column can be NULL. In that case only the acknowledged receipts are required.
        if (!reader.IsDBNull(1))
        {
            using var manifest = JsonDocument.Parse(reader.GetString(1));
            foreach (var receipt in Property(manifest.RootElement, "sourceReceipts").EnumerateArray())
                required.Add(Property(receipt, "sourceReceiptId").GetGuid());
        }
        return required.ToImmutable();
    }

    // Returns a JSON property by name. The match ignores case so it doesn't depend on the naming policy used
    // when the JSON was stored. A missing property means the stored accounting is malformed, so the call
    // fails closed.
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
        // Create the cursor row on first use. If the row already exists, keep the stored value; the
        // comparison below decides whether to move it.
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
        // The cursor is shared by every bucket for this meter, including buckets that other admissions
        // lock. FOR UPDATE locks the row so concurrent admissions compare and update it one at a time.
        await using (var read = new NpgsqlCommand($"""
            SELECT accounting_cursor FROM {_cursors}
            WHERE tenant_id = @tenant AND project_id = @project AND meter_source = @meter
            FOR UPDATE
            """, connection, transaction))
        {
            AddCursorScope(read, binding, meterSource, cursor);
            stored = (long)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        // An older cursor means this Events response is out of date compared with evidence already used.
        if (cursor < stored)
            return false;
        // The same cursor is current evidence. Nothing changes, so skip the update.
        if (cursor == stored)
            return true;
        // A newer cursor moves forward. The table trigger also rejects any move backward.
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
        // A retry must match the stored reservation exactly. A different owner, budget revision, reserve,
        // configuration revision, or accepted selection means the dispatch ID was reused for a different
        // request. Reusing the old reservation would hide a change, so admission fails with a conflict.
        if (reader.GetString(0) != binding.TenantId || reader.GetString(1) != binding.ProjectId ||
            reader.GetString(2) != binding.RunId || reader.GetString(3) != binding.AgentId ||
            reader.GetString(4) != item.Budget.BudgetId || reader.GetInt64(5) != item.Budget.Revision ||
            reader.GetDecimal(6) != item.Budget.ReservePerDispatch ||
            reader.GetInt64(7) != item.ProjectConfigurationRevision ||
            !string.Equals(reader.GetString(8), binding.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeAuthorizationException(SpendBudgetAdmissionMath.ReservationConflict);
        return true;
    }

    /// <summary>
    /// Stores the reservation for one admitted bucket. The insert is idempotent: a matching existing row is
    /// accepted, and a different existing row is a conflict.
    /// </summary>
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
        // Store the full budget identity with the reservation so a retry can be compared with it, and so the
        // reserve amount stays fixed even if the budget is edited later.
        insert.Parameters.AddWithValue("reserve", NpgsqlDbType.Numeric, budget.ReservePerDispatch);
        insert.Parameters.AddWithValue("configuration", NpgsqlDbType.Bigint, item.ProjectConfigurationRevision);
        insert.Parameters.AddWithValue("selection", NpgsqlDbType.Char, binding.AcceptedSelectionHash);
        // Zero rows inserted means a row with this (dispatch, bucket) key already exists. Accept it only if
        // it matches this request; ReadExistingReservationAsync throws a conflict when it differs.
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
        // Build a deterministic notification ID from the event type and its scope. The same inputs always
        // produce the same ID, so retries and later dispatches find the existing notification instead of
        // sending a duplicate. Including the budget revision sends a new notification after a budget edit.
        var key = string.Join('\0', eventType, binding.TenantId, binding.ProjectId, item.BucketKey,
            budget.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            dispatchId?.ToString("D") ?? string.Empty);
        // Use the first 32 hex characters of the hash as the GUID.
        var notificationId = Guid.ParseExact(
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(key))[..32], "N");
        // Skip the notification if the outbox already has it. The bucket lock serializes callers, so this
        // check and the insert below can't race for the same bucket.
        await using (var exists = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {_outboxEvents} WHERE id = @id)", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, notificationId);
            if ((bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return;
        }
        // Enqueue in the caller's transaction so the notification commits together with the admission
        // decision. The payload describes the budget limits, not spend totals, because Events owns spend.
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

    // Adds the parameters that identify one bucket. Tenant and project are always included so a query
    // can't read or change another project's reservations.
    private static void AddBucketScope(NpgsqlCommand command, RuntimeBinding binding, ApplicableSpendBudget item)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("bucket", NpgsqlDbType.Varchar, item.BucketKey);
    }

    // Adds the parameters that identify one cursor row (tenant, project, meter) and the new cursor value.
    private static void AddCursorScope(
        NpgsqlCommand command, RuntimeBinding binding, string meterSource, long cursor)
    {
        command.Parameters.AddWithValue("tenant", NpgsqlDbType.Varchar, binding.TenantId);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, binding.ProjectId);
        command.Parameters.AddWithValue("meter", NpgsqlDbType.Varchar, meterSource);
        command.Parameters.AddWithValue("cursor", NpgsqlDbType.Bigint, cursor);
    }
}
