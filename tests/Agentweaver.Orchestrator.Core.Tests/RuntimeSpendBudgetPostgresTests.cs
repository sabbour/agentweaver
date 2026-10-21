using System.Collections.Immutable;
using Agentweaver.Abstractions;
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
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 7m, reserve: 4m));
        var lagging = Evidence(priced: 0m, cursor: 1);

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, lagging));
        // Committed spend is still 0, so only the first reservation keeps the second dispatch from sending.
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted),
            await PrepareOutcomeAsync(1, null, budgets, lagging));

        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(1, await CountReservationsAsync("outstanding"));
        Assert.Equal(1, await CountOutboxAsync(DeniedEvent));
        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted),
            await PrepareOutcomeAsync(1, null, budgets, lagging));
        Assert.Equal(1, await CountOutboxAsync(DeniedEvent));
    }

    [Fact]
    public async Task RetriedDispatchReservesOnceAndTheReservationSurvivesOwnerRestart()
    {
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 10m, reserve: 4m));
        var requests = new List<UsageSpendEvidenceRequest>();
        var evidence = Evidence(priced: 0m, cursor: 1, requests: requests);

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, evidence));
        // Each call builds a new owner store, which is the same state a restarted owner reads.
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, evidence));
        Assert.Single(requests);
        Assert.Equal(1, await CountReservationsAsync("outstanding"));

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, evidence));
        Assert.Equal(2, requests.Count);
        Assert.Equal([_intents[0].MessageId.ToString("D")], requests[1].DispatchIds.ToArray());
        Assert.Equal(2, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task StaleEvidenceIsRejectedAndTheCursorNeverMovesBackward()
    {
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: null, hard: 100m, reserve: 1m));

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, Evidence(0m, cursor: 5)));
        Assert.Equal(new(null, SpendBudgetAdmissionMath.EvidenceStale),
            await PrepareOutcomeAsync(1, null, budgets, Evidence(0m, cursor: 4)));
        Assert.Equal(5, await ReadCursorAsync());
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));

        await AssertOwnerRejectsAsync(
            $"UPDATE \"{_schema}\".spend_budget_accounting_cursors SET accounting_cursor = 1");
        await AssertOwnerRejectsAsync($"DELETE FROM \"{_schema}\".spend_budget_accounting_cursors");
        await AssertOwnerRejectsAsync($"DELETE FROM \"{_schema}\".spend_budget_reservations");
        await AssertOwnerRejectsAsync(
            $"UPDATE \"{_schema}\".spend_budget_reservations SET reserve_amount = 0.5");

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, Evidence(0m, cursor: 6)));
        Assert.Equal(6, await ReadCursorAsync());
    }

    [Fact]
    public async Task HardBudgetFailsClosedAndSoftOnlyBudgetFailsOpenWithoutNotification()
    {
        await ArrangeBudgetedPairAsync();
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> unavailable =
            (_, _) => throw new HttpRequestException("Events is unavailable.");

        Assert.Equal(new(null, SpendBudgetAdmissionMath.EvidenceUnavailable), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: 1m, hard: 10m, reserve: 1m)), unavailable));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: 1m, hard: null, reserve: 0m)), unavailable));
        Assert.Equal(1, await CountAsync("maf_execution_dispatches"));
        Assert.Equal(0, await CountOutboxAsync(SoftEvent));
        Assert.Equal(0, await CountReservationsAsync("outstanding"));
    }

    [Fact]
    public async Task UnpricedUsageBlocksAHardBudget()
    {
        await ArrangeBudgetedPairAsync();
        Assert.Equal(new(null, SpendBudgetAdmissionMath.PricingMissing), await PrepareOutcomeAsync(
            0, null, RunBudgets(RunBudget(soft: null, hard: 10m, reserve: 1m)),
            Evidence(priced: 0m, cursor: 1, unpriced: 1)));
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task SoftLimitNotificationIsEnqueuedOncePerBucketAndPeriod()
    {
        await ArrangeBudgetedPairAsync();
        var budgets = RunBudgets(RunBudget(soft: 2m, hard: null, reserve: 0m));

        Assert.Equal(new(null, null), await PrepareOutcomeAsync(0, null, budgets, Evidence(3m, cursor: 1)));
        Assert.Equal(new(null, null), await PrepareOutcomeAsync(1, null, budgets, Evidence(3m, cursor: 2)));

        Assert.Equal(1, await CountOutboxAsync(SoftEvent));
        Assert.Equal(2, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ProjectBudgetUsesTheProjectPeriodTotal()
    {
        await ArrangeBudgetedPairAsync();
        var now = DateTimeOffset.UtcNow;
        var period = new UsageSpendPeriod("current",
            new(now.UtcDateTime.AddDays(-1), TimeSpan.Zero), new(now.UtcDateTime.AddDays(1), TimeSpan.Zero));
        var project = new SpendBudget("project-aic", 1, SpendBudgetScopes.Project,
            SdkMeterSources.CopilotNanoAiu, "AIC", null, 10m, 2m) { Period = period };
        var budgets = new SpendBudgetAdmissionContext([],
            new(_root.ProjectId, 1, 1, [project]));
        var requests = new List<UsageSpendEvidenceRequest>();

        Assert.Equal(new(null, SpendBudgetAdmissionMath.Exhausted), await PrepareOutcomeAsync(
            0, null, budgets, Evidence(priced: 9m, cursor: 1, requests: requests)));
        Assert.Equal(period, Assert.Single(requests).ProjectPeriod);
        Assert.Equal(0, await CountAsync("maf_execution_dispatches"));
    }

    [Fact]
    public async Task ReservationRetiresOnlyAfterTheCompletedDispatchReceiptsAreInEvents()
    {
        var budgets = RunBudgets(RunBudget(soft: null, hard: 100m, reserve: 4m));
        var admissions = await PrepareNativeAccountingPairAsync(budgets, Evidence(0m, cursor: 1));
        var usageEventId = Guid.NewGuid();
        var recorded = await RecordNativeAsync(admissions[0], NativeObservation(admissions[0], usageEventId));
        var receipts = new Dictionary<Guid, Guid[]>
        {
            [admissions[0].MessageId] = [usageEventId],
            [admissions[1].MessageId] = [Guid.NewGuid()]
        };

        // Receipts alone don't retire exposure while the dispatch hasn't completed accounting.
        Assert.Null(await AdmitDirectAsync(budgets, Evidence(0m, cursor: 2, receipts: receipts)));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[0].MessageId));

        await CompleteNativeAsync(recorded);
        Assert.Null(await AdmitDirectAsync(budgets, Evidence(0m, cursor: 3)));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[0].MessageId));

        Assert.Null(await AdmitDirectAsync(budgets, Evidence(1m, cursor: 4, receipts: receipts)));
        Assert.Equal("retired", await ReadReservationStateAsync(admissions[0].MessageId));
        Assert.Equal("outstanding", await ReadReservationStateAsync(admissions[1].MessageId));
    }

    private async Task ArrangeBudgetedPairAsync()
    {
        await RegisterSourceAsync(0, _registrations[0].Binding with { MaxModelTurns = 2 });
        _registrations[1] = _registrations[0];
        _grants[1] = _grants[0];
        _sources[1] = _sources[0];
        _intents[1] = new("item-1", _registrations[0].Binding.SessionId, Guid.NewGuid(), _intents[0].PromptHash);
        _messages[1] = _messages[0] with
        {
            Message = _messages[0].Message with { MessageId = _intents[1].MessageId }
        };
        await AdvanceNativeCheckpointAsync($"budgeted-pair-{Guid.NewGuid():N}", _checkpoint.State with
        {
            Revision = _checkpoint.State.Revision + 1,
            PendingDispatches = _intents.ToImmutableDictionary(intent => intent.AssociationId)
        });
    }

    private static SpendBudget RunBudget(decimal? soft, decimal? hard, decimal reserve) =>
        new("run-aic", 1, SpendBudgetScopes.Run, SdkMeterSources.CopilotNanoAiu, "AIC", soft, hard, reserve);

    private static SpendBudgetAdmissionContext RunBudgets(params SpendBudget[] budgets) => new([.. budgets], null);

    private Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> Evidence(
        decimal priced, long cursor, long unpriced = 0, IReadOnlyDictionary<Guid, Guid[]>? receipts = null,
        List<UsageSpendEvidenceRequest>? requests = null) => (request, _) =>
    {
        requests?.Add(request);
        var binding = _registrations[0].Binding;
        var total = new UsageSpendTotal(priced, priced > 0 ? 1 : 0, unpriced);
        var scope = new UsageDispatchAccountingScope(binding.TenantId, binding.ProjectId, binding.RunId,
            request.MeterSource, request.Unit, request.ProjectPeriod?.PeriodId);
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

    private async Task<string?> AdmitDirectAsync(
        SpendBudgetAdmissionContext budgets,
        Func<UsageSpendEvidenceRequest, CancellationToken, Task<UsageSpendEvidence>> evidence)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var denial = await new SpendBudgetExposureStore(fixture.DataSource, _options, TimeProvider.System)
            .AdmitWithinTransactionAsync(connection, transaction, _registrations[0],
                SdkMeterSources.CopilotNanoAiu, Guid.NewGuid(), budgets, evidence, default);
        await transaction.CommitAsync();
        return denial;
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

    private async Task AssertOwnerRejectsAsync(string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", failure.SqlState);
    }
}
