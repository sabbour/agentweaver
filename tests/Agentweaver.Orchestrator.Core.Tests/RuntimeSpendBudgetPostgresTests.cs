using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

/// <summary>
/// Spend budget admission against the real owner schema. Events evidence is supplied by a controlled reader,
/// so these tests prove the Orchestrator exposure, cursor, and retirement rules, not Events pricing.
/// </summary>
public sealed partial class RuntimeNumericLimitsPostgresTests
{
    private const string DeniedEvent = "orchestrator.run.spend_budget_dispatch_denied";
    private const string SoftEvent = "orchestrator.run.spend_budget_soft_limit_reached";

    [Fact]
    public async Task ReservationsBoundBackToBackDispatchesBeforeEventsAccountsForThem()
    {
        // Two dispatches in one run, a hard limit of 7, and a reserve of 4 per dispatch.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 7m, reserve: 4m));
        // Events lags behind: it reports 0 committed spend for every read.
        var lagging = Evidence(priced: 0m, cursor: 1);

        // First dispatch: 0 committed + 0 outstanding + 4 reserve = 4 <= 7, so it is admitted.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, lagging));
        // Committed spend is still 0, so only the first reservation keeps the second dispatch from sending.
        // Second dispatch: 0 committed + 4 outstanding + 4 reserve = 8 > 7, so it is denied.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted),
            await PrepareOutcomeAsync(1, null, budgets, lagging));

        // Only the admitted dispatch exists and holds a reservation; the denial wrote one notification.
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
        Assert.Equal(1, await CountOutboxAsync(DeniedEvent));
        // Retrying the denied dispatch is denied again, and the denial notification is not duplicated.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted),
            await PrepareOutcomeAsync(1, null, budgets, lagging));
        Assert.Equal(1, await CountOutboxAsync(DeniedEvent));
    }

    [Fact]
    public async Task RetriedDispatchReservesOnceAndTheReservationSurvivesOwnerRestart()
    {
        // Hard limit 10 with reserve 4 leaves room for two dispatches: 4 + 4 = 8 <= 10.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 10m, reserve: 4m));
        // Records every Events request so the test can count reads and inspect their dispatch lists.
        var requests = new List<UsageSpendEvidenceRequest>();
        var evidence = Evidence(priced: 0m, cursor: 1, requests: requests);

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, evidence));
        // Each call builds a new owner store, which is the same state a restarted owner reads.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, evidence));
        // The retry reuses the stored reservation: no second Events read and no second reservation.
        Assert.Single(requests);
        Assert.Equal(1, await CountReservationsAsync("outstanding"));

        // Second dispatch: 0 committed + 4 outstanding + 4 reserve = 8 <= 10, so it is admitted.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, evidence));
        Assert.Equal(2, requests.Count);
        // Its Events request lists the first dispatch, so Events can report whether that dispatch is accounted.
        Assert.Equal([_intents[0].MessageId.ToString("D")], requests[1].DispatchIds.ToArray());
        Assert.Equal(2, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task StaleEvidenceIsRejectedAndTheCursorNeverMovesBackward()
    {
        // The limit is large, so only evidence age can deny a dispatch in this test.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 100m, reserve: 1m));

        // The first admission stores cursor 5.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, Evidence(0m, cursor: 5)));
        // Evidence at cursor 4 is older than the stored cursor 5, so the hard budget denies it as stale.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.EvidenceStale),
            await PrepareOutcomeAsync(1, null, budgets, Evidence(0m, cursor: 4)));
        // The cursor stays at 5 and the denied dispatch was not created.
        Assert.Equal(5, await ReadCursorAsync());
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));

        // Database guards reject direct writes that would move the cursor back, delete rows, or shrink a reserve.
        await AssertOwnerRejectsAsync(
            $"UPDATE \"{_schema}\".spend_budget_accounting_cursors SET accounting_cursor = 1");
        await AssertOwnerRejectsAsync($"DELETE FROM \"{_schema}\".spend_budget_accounting_cursors");
        await AssertOwnerRejectsAsync($"DELETE FROM \"{_schema}\".spend_budget_reservations");
        await AssertOwnerRejectsAsync(
            $"UPDATE \"{_schema}\".spend_budget_reservations SET reserve_amount = 0.5");

        // Newer evidence at cursor 6 admits the dispatch and moves the cursor forward.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, Evidence(0m, cursor: 6)));
        Assert.Equal(6, await ReadCursorAsync());
    }

    [Fact]
    public async Task HardBudgetFailsClosedAndSoftOnlyBudgetFailsOpenWithoutNotification()
    {
        await ArrangeBudgetedPairAsync();
        // Every Events read fails, so admission has no spend evidence.
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> unavailable =
            (_, _) => throw new HttpRequestException("Events is unavailable.");

        // A hard budget fails closed without evidence: the dispatch is denied and not created.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.EvidenceUnavailable), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: 1m, hard: 10m, reserve: 1m)), unavailable));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));

        // A soft-only budget fails open: the dispatch is admitted.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: 1m, hard: null, reserve: 0m)), unavailable));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        // Without evidence the soft limit can't be known as reached, so no soft notification is written.
        Assert.Equal(0, await CountOutboxAsync(SoftEvent));
        // A reserve of 0 creates no reservation.
        Assert.Equal(0, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task UnpricedUsageBlocksAHardBudget()
    {
        await ArrangeBudgetedPairAsync();
        // Events reports 1 unpriced usage event, so the true spend is unknown and the hard budget denies the dispatch.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.PricingMissing), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: null, hard: 10m, reserve: 1m)),
            Evidence(priced: 0m, cursor: 1, unpriced: 1)));
        // The denied dispatch was not created.
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task SoftLimitNotificationIsEnqueuedOncePerBucketAndPeriod()
    {
        // Soft-only budget with soft limit 2; Events reports 3 committed, which is >= 2.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: 2m, hard: null, reserve: 0m));

        // A soft limit never blocks, so both dispatches are admitted.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, Evidence(3m, cursor: 1)));
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, Evidence(3m, cursor: 2)));

        // Both admissions see the soft limit reached, but only one notification is written for the bucket and period.
        Assert.Equal(1, await CountOutboxAsync(SoftEvent));
        Assert.Equal(2, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ProjectBudgetUsesTheProjectPeriodTotal()
    {
        await ArrangeBudgetedPairAsync();
        // A project budget whose period covers now: hard limit 10, reserve 2.
        var now = DateTimeOffset.UtcNow;
        var period = new UsageSpendPeriod("current",
            new(now.UtcDateTime.AddDays(-1), TimeSpan.Zero), new(now.UtcDateTime.AddDays(1), TimeSpan.Zero));
        var project = new SpendBudget("project-aic", 1, SpendBudgetScopes.Project,
            SdkMeterSources.CopilotNanoAiu, "AIC", null, 10m, 2m) { Period = period };
        var budgets = new SpendBudgetAdmissionContext([],
            new(_root.ProjectId, 1, 1, [project]));
        var requests = new List<UsageSpendEvidenceRequest>();

        // The project period total is 9: 9 committed + 0 outstanding + 2 reserve = 11 > 10, so it is denied.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted), await PrepareOutcomeAsync(
            0, null, budgets, Evidence(priced: 9m, cursor: 1, requests: requests)));
        // The single Events request asked for the configured project period.
        Assert.Equal(period, Assert.Single(requests).ProjectPeriod);
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ReservationRetiresOnlyAfterTheCompletedDispatchReceiptsAreInEvents()
    {
        // The limit is large so every admission here succeeds; the test only checks reservation states.
        var budgets = RunBudgets(RunBudget(soft: null, hard: 100m, reserve: 4m));
        // Both dispatches are admitted and each holds an outstanding reservation.
        var admissions = await PrepareNativeAccountingPairAsync(budgets, Evidence(0m, cursor: 1));
        // The first dispatch records one usage event, which becomes its source receipt.
        var usageEventId = Guid.NewGuid();
        var recorded = await RecordNativeAsync(admissions[0], NativeObservation(admissions[0], usageEventId));
        // Events witnesses the first dispatch's real receipt and an unrelated receipt for the second dispatch.
        var receipts = new Dictionary<Guid, Guid[]>
        {
            [admissions[0].MessageId] = [usageEventId],
            [admissions[1].MessageId] = [Guid.NewGuid()]
        };

        // Receipts alone don't retire exposure while the dispatch hasn't completed accounting.
        Assert.Null(await AdmitDirectAsync(budgets, Evidence(0m, cursor: 2, receipts: receipts)));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[0].MessageId));

        // The dispatch now completed accounting, but this Events read witnesses none of its receipts.
        await CompleteNativeAsync(recorded);
        Assert.Null(await AdmitDirectAsync(budgets, Evidence(0m, cursor: 3)));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[0].MessageId));

        // Completed accounting plus witnessed receipts: the first reservation retires.
        Assert.Null(await AdmitDirectAsync(budgets, Evidence(1m, cursor: 4, receipts: receipts)));
        Assert.Equal("retired", await ReadReservationStateAsync(admissions[0].MessageId));
        // The second dispatch never completed accounting, so its reservation stays outstanding.
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[1].MessageId));
    }

    [Fact]
    public async Task OlderSpendResponseAfterRetirementCannotReopenCapacity()
    {
        // Hard limit 10, reserve 4. Both dispatches are admitted: 0 + 4 + 4 = 8 <= 10.
        var budgets = RunBudgets(RunBudget(soft: null, hard: 10m, reserve: 4m));
        var admissions = await PrepareNativeAccountingPairAsync(budgets, Evidence(0m, cursor: 1));
        // The first dispatch records one usage event and completes accounting.
        var usageEventId = Guid.NewGuid();
        await CompleteNativeAsync(
            await RecordNativeAsync(admissions[0], NativeObservation(admissions[0], usageEventId)));
        var receipts = new Dictionary<Guid, Guid[]> { [admissions[0].MessageId] = [usageEventId] };

        // The current read represents the first dispatch's charge, so its reservation retires at cursor 4.
        // A new dispatch: 4 committed + 4 outstanding (second dispatch) + 4 reserve = 12 > 10, so it is denied.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            await AdmitDirectAsync(budgets, Evidence(4m, cursor: 4, receipts: receipts)));
        Assert.Equal("retired", await ReadReservationStateAsync(admissions[0].MessageId));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[1].MessageId));

        // An in-flight response from before the charge would admit 0 + 4 + 4 <= 10 if it were authoritative.
        Assert.Equal(SpendBudgetAdmissionMath.EvidenceStale,
            await AdmitDirectAsync(budgets, Evidence(0m, cursor: 3, receipts: receipts)));
        // Cursor 3 is older than the stored cursor 4, so it is rejected as stale and changes nothing.
        Assert.Equal(4, await ReadCursorAsync());
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
        Assert.Equal(1, await CountReservationsAsync("retired"));

        // Each admission builds a new owner store, so the persisted cursor constrains admission after restart.
        // Same arithmetic as before: 4 committed + 4 outstanding + 4 reserve = 12 > 10.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted,
            await AdmitDirectAsync(budgets, Evidence(4m, cursor: 4, receipts: receipts)));
        // Denials add no reservation.
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task ConcurrentRunsAdmitOnlyTheExposureThatFitsTheProjectBucket()
    {
        // One shared project bucket: hard limit 10, reserve 6. Only one dispatch fits: 6 + 6 = 12 > 10.
        var period = CurrentPeriod();
        var project = ProjectBudget(revision: 1, hard: 10m, reserve: 6m, period);
        var budgets = new SpendBudgetAdmissionContext([], new(_root.ProjectId, 1, 1, [project]));
        // A second run in the same project, so both runs compete for the same project bucket.
        var otherRun = _registrations[0] with
        {
            Binding = _registrations[0].Binding with { RunId = Guid.NewGuid().ToString("N") }
        };
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> Delayed(string runId)
        {
            var read = Evidence(0m, cursor: 1, runId: runId);
            return async (request, token) =>
            {
                // Holding the evidence read open keeps both admissions in flight together.
                await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                return await read(request, token);
            };
        }

        // Both runs admit at the same time. The per-bucket advisory lock makes the second wait for the first.
        var outcomes = await Task.WhenAll(
            AdmitDirectAsync(budgets, Delayed(_registrations[0].Binding.RunId)),
            AdmitDirectAsync(budgets, Delayed(otherRun.Binding.RunId), otherRun));

        // First: 0 + 0 + 6 = 6 <= 10, admitted. Second: 0 + 6 + 6 = 12 > 10, denied.
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome == SpendBudgetAdmissionMath.Exhausted);
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
        // A later attempt from the other run still sees the outstanding reserve and is denied.
        Assert.Equal(SpendBudgetAdmissionMath.Exhausted, await AdmitDirectAsync(
            budgets, Evidence(0m, cursor: 1, runId: otherRun.Binding.RunId), otherRun));
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task ProjectEditsApplyToNewDispatchesWhilePinnedBudgetsKeepTheirRevision()
    {
        await ArrangeBudgetedPairAsync();
        // The run budget is pinned at the run's configuration revision. The project budget is read fresh
        // for each dispatch, so the test passes a new configuration revision with each project edit.
        var pinnedRevision = _registrations[0].Binding.ProjectConfigurationRevision;
        var run = RunBudget(soft: null, hard: 100m, reserve: 1m);
        var accepted = ProjectBudget(revision: 1, hard: 10m, reserve: 1m, CurrentPeriod());
        SpendBudgetAdmissionContext Budgets(long configurationRevision, SpendBudget project) =>
            new([run], new(_root.ProjectId, configurationRevision, 1, [project]));
        var requests = new List<UsageSpendEvidenceRequest>();

        // First dispatch fits both budgets and reserves 1 in each bucket.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(
            0, null, Budgets(pinnedRevision, accepted), Evidence(0m, cursor: 1)));

        // Editing the period bounds keeps the bucket, so the first dispatch's reserve still counts.
        var edited = accepted with
        {
            Revision = 2, HardLimit = 1.5m,
            Period = accepted.Period! with { EndsAt = accepted.Period!.EndsAt.AddDays(1) }
        };
        // The new hard limit 1.5 applies at once: 0 committed + 1 outstanding + 1 reserve = 2 > 1.5, so it is denied.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted), await PrepareOutcomeAsync(
            1, null, Budgets(pinnedRevision + 1, edited), Evidence(0m, cursor: 2, requests: requests)));
        // The project Events request used the edited period.
        Assert.Equal(edited.Period, Assert.Single(requests, request => request.ProjectPeriod is not null).ProjectPeriod);
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));

        // Raising the hard limit to 50 admits the same dispatch: 0 + 1 + 1 = 2 <= 50.
        var raised = edited with { Revision = 3, HardLimit = 50m };
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(
            1, null, Budgets(pinnedRevision + 2, raised), Evidence(0m, cursor: 3)));

        // Each reservation records the budget and configuration revisions that admitted it.
        // The first dispatch keeps project revision 1; the run budget stays at its pinned revision for both.
        Assert.Equal([("project", 1L, pinnedRevision), ("run", 1L, pinnedRevision)],
            await ReadReservationRevisionsAsync(_intents[0].MessageId));
        // The second dispatch was admitted under project budget revision 3.
        Assert.Equal([("project", 3L, pinnedRevision + 2), ("run", 1L, pinnedRevision)],
            await ReadReservationRevisionsAsync(_intents[1].MessageId));
    }

    [Fact]
    public async Task NotificationWriteFailureLeavesNoDispatchAndNoReservation()
    {
        // Soft limit 2, hard limit 5, reserve 2. Events reports 3 committed, which reaches the soft limit.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: 2m, hard: 5m, reserve: 2m));

        // The soft limit notification insert fails, so the whole admission transaction throws.
        await FailOutboxInsertsAsync(SoftEvent);
        Assert.Equal("P0001", (await Assert.ThrowsAnyAsync<PostgresException>(() =>
            PrepareOutcomeAsync(0, null, budgets, Evidence(3m, cursor: 1)))).SqlState);
        // The rollback leaves no dispatch, no reservation, no cursor, and no notification.
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(0, await CountAllReservationsAsync());
        Assert.Equal(0, await CountAsync("spend_budget_accounting_cursors"));
        Assert.Equal(0, await CountOutboxAsync(SoftEvent));

        // With writes restored, the same dispatch fits: 3 committed + 0 outstanding + 2 reserve = 5 <= 5.
        await RestoreOutboxInsertsAsync();
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, Evidence(3m, cursor: 1)));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(1, await CountAllReservationsAsync());
        Assert.Equal(1, await CountOutboxAsync(SoftEvent));

        // 3 committed + 2 outstanding + 2 reserve exceeds 5, and the denial journal write fails.
        await FailOutboxInsertsAsync(DeniedEvent);
        Assert.Equal("P0001", (await Assert.ThrowsAnyAsync<PostgresException>(() =>
            PrepareOutcomeAsync(1, null, budgets, Evidence(3m, cursor: 2)))).SqlState);
        // The rollback keeps only the first dispatch's state, and the cursor stays at 1, not 2.
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(1, await CountAllReservationsAsync());
        Assert.Equal(0, await CountOutboxAsync(DeniedEvent));
        Assert.Equal(1, await ReadCursorAsync());

        // With writes restored, the denial commits its notification and still creates no dispatch.
        await RestoreOutboxInsertsAsync();
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted),
            await PrepareOutcomeAsync(1, null, budgets, Evidence(3m, cursor: 2)));
        Assert.Equal(1, await CountOutboxAsync(DeniedEvent));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task AuthorityLossDuringAdmissionRollsBackTheReservationAndSendsNothing()
    {
        // Hard limit 4 with reserve 4: exactly one dispatch fits.
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 4m, reserve: 4m));
        // The evidence reader marks ownership as lost after it returns, so the owner check after admission fails.
        var authorityLost = false;
        var read = Evidence(0m, cursor: 1);
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> losing = async (request, token) =>
        {
            var evidence = await read(request, token);
            authorityLost = true;
            return evidence;
        };

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => PrepareAsCurrentOwnerAsync(
            0, budgets, losing, async (connection, transaction) =>
            {
                // The first owner check runs before admission and passes.
                if (!authorityLost)
                    return;
                // Admission wrote its reservation and cursor in the owner transaction before the owner recheck.
                Assert.Equal(1, await CountAllReservationsAsync(connection, transaction));
                throw new RuntimeAuthorizationException("coordination_owner_lost");
            }));
        Assert.Equal("coordination_owner_lost", failure.Code);
        // The rollback removed the dispatch, its reservation, and the cursor.
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(0, await CountAllReservationsAsync());
        Assert.Equal(0, await CountAsync("spend_budget_accounting_cursors"));

        // No exposure leaked from the rolled-back attempt: a different dispatch still fits 0 + 0 + 4 <= 4.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, Evidence(0m, cursor: 1)));
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
    }

    private async Task ArrangeBudgetedPairAsync()
    {
        // Allow two model turns so the run can send two dispatches.
        await RegisterSourceAsync(0, _registrations[0].Binding with { MaxModelTurns = 2 });
        // The second dispatch reuses the first registration, grant, and source, but gets its own message ID.
        _registrations[1] = _registrations[0];
        _grants[1] = _grants[0];
        _sources[1] = _sources[0];
        _intents[1] = new("item-1", _registrations[0].Binding.SessionId, Guid.NewGuid(), _intents[0].PromptHash);
        _messages[1] = _messages[0] with
        {
            Message = _messages[0].Message with { MessageId = _intents[1].MessageId }
        };
        // Save a checkpoint that lists both dispatches as pending, so both can be prepared.
        await AdvanceNativeCheckpointAsync($"budgeted-pair-{Guid.NewGuid():N}", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1,
            PendingDispatches = _intents.ToImmutableDictionary(intent => intent.AssociationId)
        });
    }

    // A run-scoped Copilot budget in AIC at revision 1.
    private static SpendBudget RunBudget(decimal? soft, decimal? hard, decimal reserve) =>
        new("run-aic", 1, SpendBudgetScopes.Run, SdkMeterSources.CopilotNanoAiu, "AIC", soft, hard, reserve);

    // Admission context with only pinned run budgets and no project budgets.
    private static SpendBudgetAdmissionContext RunBudgets(params SpendBudget[] budgets) => new([.. budgets], null);

    // A project-scoped Copilot budget in AIC for one period.
    private static SpendBudget ProjectBudget(long revision, decimal? hard, decimal reserve, UsageSpendPeriod period) =>
        new("project-aic", revision, SpendBudgetScopes.Project, SdkMeterSources.CopilotNanoAiu, "AIC", null, hard,
            reserve) { Period = period };

    // A period from yesterday to tomorrow, so it always covers the current time.
    private static UsageSpendPeriod CurrentPeriod()
    {
        var now = DateTimeOffset.UtcNow;
        return new("current", new(now.UtcDateTime.AddDays(-1), TimeSpan.Zero),
            new(now.UtcDateTime.AddDays(1), TimeSpan.Zero));
    }

    // Fake Events reader. Every response reports the same committed total (priced amount, unpriced count) for
    // the run, agent, and project period, at the given accounting cursor. For each requested dispatch it returns
    // a witness with the receipts listed in receipts, or no receipts. Optionally records each request.
    private Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> Evidence(
        decimal priced, long cursor, long unpriced = 0, IReadOnlyDictionary<Guid, Guid[]>? receipts = null,
        List<UsageSpendEvidenceRequest>? requests = null, string? runId = null) => (request, _) =>
    {
        // Concurrent admissions can call this at the same time, so the list is updated under a lock.
        lock (_registrations)
            requests?.Add(request);
        var binding = _registrations[0].Binding with { RunId = runId ?? _registrations[0].Binding.RunId };
        var total = new UsageSpendTotal(priced, priced > 0 ? 1 : 0, unpriced);
        var scope = new UsageDispatchAccountingScope(binding.TenantId, binding.ProjectId, binding.RunId,
            request.MeterSource, request.Unit, request.ProjectPeriod?.PeriodId);
        // One witness per requested dispatch, in request order, with a valid receipt digest.
        var witnesses = request.DispatchIds.Select(dispatchId =>
        {
            var items = (receipts?.GetValueOrDefault(Guid.Parse(dispatchId)) ?? [])
                .Select(receipt => new UsageDispatchSourceReceiptIdentity(receipt, Guid.NewGuid(), new string('f', 64)))
                .ToImmutableArray();
            return new UsageDispatchAccountingWitness(dispatchId, scope, cursor, items,
                UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(dispatchId, scope, items),
                0m, items.Length, 0,
                items.IsEmpty ? UsageDispatchAccountingStatus.Unknown : UsageDispatchAccountingStatus.Partial,
                null, null, null);
        }).ToImmutableArray();
        return Task.FromResult(new UsageSpendEvidence(UsageSpendEvidenceContract.CurrentVersion,
            binding.TenantId, binding.ProjectId, binding.RunId, request.MeterSource, request.Unit, cursor, total,
            request.ProjectPeriod is null ? null : total, request.ProjectPeriod?.PeriodId, witnesses)
        {
            Agent = request.AgentId is null ? null : total
        });
    };

    // Runs only the spend budget admission for a new random dispatch ID and commits the result.
    // Returns the denial code, or null when the dispatch is admitted.
    private async Task<string?> AdmitDirectAsync(
        SpendBudgetAdmissionContext budgets,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> evidence,
        RuntimeRegistration? registration = null)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var denial = await new SpendBudgetExposureStore(fixture.DataSource, _options, TimeProvider.System)
            .AdmitWithinTransactionAsync(connection, transaction, registration ?? _registrations[0],
                SdkMeterSources.CopilotNanoAiu, Guid.NewGuid(), budgets, evidence, default);
        await transaction.CommitAsync();
        return denial;
    }

    /// <summary>Runs native preparation with owner rechecks before and after it, in the owner endpoint's order.</summary>
    private Task<RuntimeNativePreparationOutcome> PrepareAsCurrentOwnerAsync(
        int index, SpendBudgetAdmissionContext budgets,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> evidence,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> requireCurrentOwner)
    {
        var store = SourceStore();
        return store.ExecuteLockedAsync(_registrations[index].RuntimeInstanceId,
            async (connection, transaction, token) =>
            {
                await requireCurrentOwner(connection, transaction);
                var prepared = await store.PrepareNativeTurnWithinTransactionAsync(
                    connection, transaction, _root, _checkpoint, _intents[index], _registrations[index],
                    _messages[index], NoCreditSnapshot, token, budgets, evidence);
                await requireCurrentOwner(connection, transaction);
                await transaction.CommitAsync(token);
                return prepared;
            }, default);
    }

    // Reads (scope, budget revision, project configuration revision) for each reservation of one dispatch.
    private async Task<(string Scope, long BudgetRevision, long ConfigurationRevision)[]>
        ReadReservationRevisionsAsync(Guid dispatchId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT scope, budget_revision, project_configuration_revision
            FROM "{_schema}".spend_budget_reservations WHERE dispatch_id = @dispatch ORDER BY scope
            """, connection);
        command.Parameters.AddWithValue("dispatch", dispatchId);
        var rows = new List<(string, long, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return [.. rows];
    }

    // Counts reservations in every state. The overload with a transaction sees uncommitted rows.
    private async Task<long> CountAllReservationsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await CountAllReservationsAsync(connection, null);
    }

    private async Task<long> CountAllReservationsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".spend_budget_reservations", connection, transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Installs a trigger that raises an error (SQLSTATE P0001) for every outbox insert of the given event type.
    private async Task FailOutboxInsertsAsync(string eventType)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION "{_schema}".test_reject_outbox_event() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.event_type = TG_ARGV[0] THEN
                    RAISE EXCEPTION 'outbox_write_rejected';
                END IF;
                RETURN NEW;
            END;
            $$;
            DROP TRIGGER IF EXISTS test_reject_outbox_event ON "{_schema}".outbox_events;
            CREATE TRIGGER test_reject_outbox_event
            BEFORE INSERT ON "{_schema}".outbox_events
            FOR EACH ROW EXECUTE FUNCTION "{_schema}".test_reject_outbox_event('{eventType}');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    // Removes the failing trigger so outbox inserts work again.
    private async Task RestoreOutboxInsertsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP TRIGGER test_reject_outbox_event ON \"{_schema}\".outbox_events", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountReservationsAsync(string state)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".spend_budget_reservations WHERE reservation_state = @state
            """, connection);
        command.Parameters.AddWithValue("state", state);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadReservationStateAsync(Guid dispatchId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT reservation_state FROM "{_schema}".spend_budget_reservations WHERE dispatch_id = @dispatch
            """, connection);
        command.Parameters.AddWithValue("dispatch", dispatchId);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<long> ReadCursorAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT accounting_cursor FROM \"{_schema}\".spend_budget_accounting_cursors", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> CountOutboxAsync(string eventType)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".outbox_events WHERE event_type = @type", connection);
        command.Parameters.AddWithValue("type", eventType);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Runs a direct write and expects the owner schema guard to reject it with SQLSTATE 55000.
    private async Task AssertOwnerRejectsAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", failure.SqlState);
    }
}
