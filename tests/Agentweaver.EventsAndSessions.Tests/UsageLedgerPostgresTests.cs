using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class UsageLedgerPostgresTests : IAsyncLifetime
{
    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "usage_" + Guid.NewGuid().ToString("N");
    private PostgresUsageLedger _ledger = null!;

    public UsageLedgerPostgresTests(SessionsPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await CreateSessionAsync("project-1", "run-1", "session-1");
        await CreateSessionAsync("project-1", "run-1", "session-2");
        _ledger = new PostgresUsageLedger(_fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task NativeMetadataCacheWritesAndUnknownRequestCountPersistAcrossRestart()
    {
        var usage = UsageContractTests.Submission() with
        {
            Attribution = UsageContractTests.Submission().Attribution with { TurnId = "native-turn" },
            Measurement = new(17, 11, 7, 3, null, 1234567.25m, "nano_aiu", 12.5m)
            {
                CacheWriteTokens = 5
            },
            SdkSource = new(Guid.NewGuid(), "native-session", "1.0.11", "runtime-v1", "model/ref", "model-1",
                new string('a', 64), 2.5m, "hosted-copilot", "meter-a", new string('b', 64), 1),
            SdkEventId = Guid.NewGuid().ToString("D")
        };
        var first = await _ledger.AppendAsync(usage, UsageContractTests.Binding(), UsageContractTests.Price());
        var restarted = new PostgresUsageLedger(_fixture.DataSource, _schema);
        var replay = await restarted.AppendAsync(usage, UsageContractTests.Binding(), UsageContractTests.Price());
        Assert.True(replay.IsDuplicate);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(usage, replay.Entry.Usage);
        var totals = await restarted.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        var agent = Assert.Single(totals.Agents);
        Assert.Null(agent.RequestCount);
        Assert.Equal(5, agent.CacheWriteTokens);
        await AssertConflictAsync(usage with
        {
            SdkSource = usage.SdkSource! with { CatalogHash = new string('c', 64) }
        }, UsageContractTests.Binding(), UsageContractTests.Price());
        await AssertConflictAsync(usage with
        {
            Measurement = usage.Measurement with { CacheWriteTokens = 6 }
        }, UsageContractTests.Binding(), UsageContractTests.Price());
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await restarted.AppendWithinTransactionAsync(
            connection, transaction, usage with { EventId = Guid.NewGuid() },
            UsageContractTests.Binding(), UsageContractTests.Price());
        await transaction.RollbackAsync();
        Assert.Equal(1, (await restarted.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    [Fact]
    public async Task ExactDuplicateReturnsOriginalAndChangedScopeModelUnitBindingOrPriceConflicts()
    {
        var usage = UsageContractTests.Submission();
        var binding = UsageContractTests.Binding();
        var price = UsageContractTests.Price();
        var original = await _ledger.AppendAsync(usage, binding, price);
        Assert.False(original.IsDuplicate);

        var reconstructed = new PostgresUsageLedger(_fixture.DataSource, _schema);
        var duplicate = await reconstructed.AppendAsync(usage, binding, price);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(original.Entry.RecordedAt, duplicate.Entry.RecordedAt);
        Assert.Equal(original.Receipt, duplicate.Receipt);
        Assert.Equal(usage, duplicate.Entry.Usage);
        Assert.Equal(price.Amount, duplicate.Entry.Price.Amount);
        Assert.Equal(price.Unit, duplicate.Entry.Price.Unit);
        Assert.Equal(price.Disposition, duplicate.Entry.Price.Disposition);
        Assert.Equal(price.RateCard?.Id, duplicate.Entry.Price.RateCard?.Id);
        Assert.Equal(price.RateCard?.Version, duplicate.Entry.Price.RateCard?.Version);
        Assert.Equal(
            price.RateCard?.ModelMultipliers["model-1"],
            duplicate.Entry.Price.RateCard?.ModelMultipliers["model-1"]);

        await AssertConflictAsync(usage with
        {
            Attribution = usage.Attribution with { TenantId = "tenant-2" },
        }, binding, price);
        await AssertConflictAsync(usage with
        {
            Attribution = usage.Attribution with { SessionId = "session-2" },
        }, binding, price);
        await AssertConflictAsync(usage with
        {
            Attribution = usage.Attribution with { ProjectId = "project-2" },
        }, binding, price);
        await AssertConflictAsync(usage with
        {
            Attribution = usage.Attribution with { RunId = "run-2" },
        }, binding, price);
        await AssertConflictAsync(usage with
        {
            ModelBinding = usage.ModelBinding with { ModelId = "model-other" },
        }, binding, price);
        await AssertConflictAsync(usage, binding with { ResourceId = "resource-other" }, price);
        await AssertConflictAsync(usage, binding, price with { Amount = 2m });

        var euroCard = binding.RateCard with { Version = "v2", Unit = "EUR" };
        var euroBinding = binding with { RateCard = euroCard };
        await AssertConflictAsync(
            usage,
            euroBinding,
            price with { Amount = 2m, Unit = "EUR", RateCard = euroCard });
        Assert.Equal(0, await RateCardCountAsync(euroCard.Id, euroCard.Version));
    }

    [Fact]
    public async Task AccountingReceiptBindsCommittedCanonicalUsageAndImmutablePrice()
    {
        var usage = UsageContractTests.Submission();
        var binding = UsageContractTests.Binding();
        var price = UsageContractTests.Price();
        var result = await _ledger.AppendAsync(usage, binding, price);
        var receipt = result.Receipt;
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            UsageLedgerCanonicalizer.Serialize(usage, binding, price))));

        Assert.Equal(usage.EventId, receipt.EventId);
        Assert.Equal(usage.Attribution, receipt.Attribution);
        Assert.Equal(expectedHash, receipt.CanonicalPayloadHash);
        Assert.Matches("^[0-9a-f]{64}$", receipt.CanonicalPayloadHash);
        Assert.Equal(price.Disposition, receipt.Disposition);
        Assert.Equal(price.Amount, receipt.Amount);
        Assert.Equal(price.Unit, receipt.Unit);
        Assert.Null(receipt.UnpricedReason);
        Assert.Equal(binding.RateCard.Id, receipt.RateCardId);
        Assert.Equal(binding.RateCard.Version, receipt.RateCardVersion);
        Assert.Equal(result.Entry.RecordedAt, receipt.RecordedAt);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand($"""
            SELECT canonical_input_hash, price_amount, price_unit, rate_card_id,
                   rate_card_version, recorded_at
            FROM "{_schema}".usage_ledger WHERE event_id = @event
            """, connection);
        read.Parameters.AddWithValue("event", NpgsqlDbType.Uuid, usage.EventId);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(receipt.CanonicalPayloadHash, reader.GetString(0));
        Assert.Equal(receipt.Amount, reader.GetDecimal(1));
        Assert.Equal(receipt.Unit, reader.GetString(2));
        Assert.Equal(receipt.RateCardId, reader.GetString(3));
        Assert.Equal(receipt.RateCardVersion, reader.GetString(4));
        Assert.Equal(receipt.RecordedAt, reader.GetFieldValue<DateTimeOffset>(5));
    }

    [Fact]
    public async Task ConcurrentDuplicateAndDistinctEventsPersistAcrossLedgerInstances()
    {
        var firstLedger = _ledger;
        var secondLedger = new PostgresUsageLedger(_fixture.DataSource, _schema);
        var shared = UsageContractTests.Submission();
        var binding = UsageContractTests.Binding();
        var price = UsageContractTests.Price();
        var duplicateCalls = Enumerable.Range(0, 20)
            .Select(index => (index % 2 == 0 ? firstLedger : secondLedger)
                .AppendAsync(shared, binding, price))
            .ToArray();
        var duplicateResults = await Task.WhenAll(duplicateCalls);
        Assert.Single(duplicateResults, result => !result.IsDuplicate);
        Assert.Equal(19, duplicateResults.Count(result => result.IsDuplicate));
        Assert.All(duplicateResults,
            result => Assert.Equal(duplicateResults[0].Entry.RecordedAt, result.Entry.RecordedAt));

        var distinctCalls = Enumerable.Range(0, 30)
            .Select(index => (index % 2 == 0 ? firstLedger : secondLedger)
                .AppendAsync(
                    UsageContractTests.Submission(
                        measurement: new UsageMeasurement(1, 2, 0, 0, 1, null, null, null)),
                    binding,
                    price))
            .ToArray();
        var distinctResults = await Task.WhenAll(distinctCalls);
        Assert.All(distinctResults, result => Assert.False(result.IsDuplicate));

        var totals = await new PostgresUsageLedger(_fixture.DataSource, _schema)
            .GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.Equal(31, totals.Events);
        Assert.Equal(31, Assert.Single(totals.Agents).Events);
    }

    [Fact]
    public async Task RateCardsAreImmutableAndRateChangesNeverRepriceHistoricalRows()
    {
        var cardV1 = UsageContractTests.Card();
        var bindingV1 = UsageContractTests.Binding(cardV1);
        var usageV1 = UsageContractTests.Submission();
        var priceV1 = UsageContractTests.Price(cardV1) with { Amount = 1.25m };
        var first = await _ledger.AppendAsync(usageV1, bindingV1, priceV1);

        var changedCardSameVersion = cardV1 with { NanoUnitsPerUnit = 2_000_000_000m };
        var changedBinding = UsageContractTests.Binding(changedCardSameVersion);
        var changedPrice = UsageContractTests.Price(changedCardSameVersion) with { Amount = 2.5m };
        await Assert.ThrowsAsync<UsageLedgerConflictException>(() =>
            _ledger.AppendAsync(
                UsageContractTests.Submission(eventId: Guid.NewGuid()),
                changedBinding,
                changedPrice));

        var cardV2 = cardV1 with
        {
            Version = "v2",
            Unit = "EUR",
            NanoUnitsPerUnit = 2_000_000_000m,
        };
        var usageV2 = UsageContractTests.Submission(eventId: Guid.NewGuid());
        var priceV2 = UsageContractTests.Price(cardV2) with { Amount = 2.5m, Unit = "EUR" };
        await _ledger.AppendAsync(usageV2, UsageContractTests.Binding(cardV2), priceV2);

        var historicalDuplicate = await _ledger.AppendAsync(usageV1, bindingV1, priceV1);
        Assert.True(historicalDuplicate.IsDuplicate);
        Assert.Equal(1.25m, historicalDuplicate.Entry.Price.Amount);
        Assert.Equal(first.Entry.RecordedAt, historicalDuplicate.Entry.RecordedAt);

        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.Equal(2, totals.Events);
        Assert.Equal(2, totals.Amounts.Length);
        Assert.Equal(1.25m, Assert.Single(totals.Amounts, amount => amount.Unit == "USD").Amount);
        Assert.Equal(2.5m, Assert.Single(totals.Amounts, amount => amount.Unit == "EUR").Amount);
    }

    [Fact]
    public async Task LedgerAndRateCardHistoryRejectUpdatesAndDeletes()
    {
        var usage = UsageContractTests.Submission();
        await _ledger.AppendAsync(
            usage, UsageContractTests.Binding(), UsageContractTests.Price());

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteSqlAsync($"""
                UPDATE "{_schema}".usage_ledger SET price_amount = 0
                WHERE event_id = '{usage.EventId:D}'
                """));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteSqlAsync($"""
                DELETE FROM "{_schema}".usage_ledger WHERE event_id = '{usage.EventId:D}'
                """));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteSqlAsync($"""
                UPDATE "{_schema}".usage_rate_cards SET unit = 'EUR'
                WHERE card_id = 'rate-card-1' AND version = 'v1'
                """));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await ExecuteSqlAsync($"""
                DELETE FROM "{_schema}".usage_rate_cards
                WHERE card_id = 'rate-card-1' AND version = 'v1'
                """));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSqlAsync($"TRUNCATE TABLE \"{_schema}\".usage_ledger"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSqlAsync($"TRUNCATE TABLE \"{_schema}\".usage_rate_cards"));
    }

    [Fact]
    public async Task MissingSdkFieldsAndUnknownSourceStayUnpricedAndTotalsRemainUnknown()
    {
        var firstUsage = UsageContractTests.Submission(
            model: new UsageModelBinding("unknown/model", "model-unknown", "unknown-meter", "selection-1"),
            measurement: new UsageMeasurement(null, null, null, null, 0, null, null, null));
        var unpriced = new CostPrice(null, null, CostDisposition.Unpriced, null, "Source is not pinned.");
        var first = await _ledger.AppendAsync(firstUsage, binding: null, unpriced);
        Assert.False(first.IsDuplicate);
        Assert.Equal(CostDisposition.Unpriced, first.Receipt.Disposition);
        Assert.Null(first.Receipt.Amount);
        Assert.Null(first.Receipt.Unit);
        Assert.Null(first.Receipt.RateCardId);
        Assert.Null(first.Receipt.RateCardVersion);
        Assert.Equal(unpriced.UnpricedReason, first.Receipt.UnpricedReason);

        var partialUsage = UsageContractTests.Submission(
            model: firstUsage.ModelBinding,
            measurement: new UsageMeasurement(10, null, 3, null, 1, 4m, null, null));
        await _ledger.AppendAsync(partialUsage, binding: null, unpriced);

        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        var agent = Assert.Single(totals.Agents);
        Assert.False(totals.IsFullyPriced);
        Assert.False(agent.IsFullyPriced);
        Assert.Equal(2, agent.Events);
        Assert.Equal(1, agent.RequestCount);
        Assert.Null(agent.InputTokens);
        Assert.Null(agent.OutputTokens);
        Assert.Null(agent.CachedTokens);
        Assert.Null(agent.ReasoningTokens);
        Assert.Null(agent.DurationMilliseconds);
        Assert.Empty(totals.Amounts);
        Assert.Empty(agent.Amounts);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT input_tokens, output_tokens, cached_tokens, reasoning_tokens,
                   provider_units, provider_unit, duration_milliseconds
            FROM "{_schema}".usage_ledger
            WHERE event_id = @event_id
            """, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, firstUsage.EventId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 7), column => Assert.True(reader.IsDBNull(column)));
    }

    [Fact]
    public async Task UsageMustReferenceAnExistingNativeSession()
    {
        var usage = UsageContractTests.Submission() with
        {
            Attribution = new UsageAttribution(
                "tenant-1", "project-1", "run-1", "missing-session", "agent-1"),
        };
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            _ledger.AppendAsync(usage, UsageContractTests.Binding(), UsageContractTests.Price()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);

        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.Equal(0, totals.Events);
    }

    [Fact]
    public async Task UnsupportedLedgerContractVersionsAreRejectedByPostgres()
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSqlAsync($"""
                INSERT INTO "{_schema}".usage_ledger (
                    contract_version, tenant_id, project_id, run_id, session_id, event_id, occurred_at,
                    agent_id, model_reference, model_id, meter_source, selection_revision, request_count,
                    price_disposition, unpriced_reason, canonical_input, canonical_input_hash, payload)
                VALUES (
                    2, 'tenant-1', 'project-1', 'run-1', 'session-1',
                    '10000000-0000-0000-0000-000000000001', '2026-10-06T12:34:56Z',
                    'agent-1', 'model/ref', 'model-1', 'meter-a', 'selection-1', 0,
                    'Unpriced', 'No pinned source', 'canonical', repeat('0', 64), jsonb_build_object())
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Fact]
    public async Task TotalsFailExplicitlyWhenIntegerOrDecimalArithmeticOverflows()
    {
        await CreateSessionAsync("project-1", "overflow-tokens", "session-1");
        var tokenScope = new UsageAttribution(
            "tenant-1", "project-1", "overflow-tokens", "session-1", "agent-1");
        var noTokens = new UsageMeasurement(null, null, null, null, 0, null, null, null);
        await AppendForScopeAsync(tokenScope, noTokens, UsageContractTests.Price());
        await AppendForScopeAsync(
            tokenScope,
            noTokens with { InputTokens = long.MaxValue },
            UsageContractTests.Price());
        await AppendForScopeAsync(
            tokenScope,
            noTokens with { InputTokens = 1 },
            UsageContractTests.Price());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _ledger.GetRunTotalsAsync("tenant-1", "project-1", "overflow-tokens"));

        await CreateSessionAsync("project-1", "overflow-money", "session-1");
        var moneyScope = new UsageAttribution(
            "tenant-1", "project-1", "overflow-money", "session-1", "agent-1");
        var maxPrice = UsageContractTests.Price() with { Amount = decimal.MaxValue };
        await AppendForScopeAsync(moneyScope, noTokens, maxPrice);
        await AppendForScopeAsync(moneyScope, noTokens, maxPrice);
        await Assert.ThrowsAsync<OverflowException>(() =>
            _ledger.GetRunTotalsAsync("tenant-1", "project-1", "overflow-money"));
    }

    private async Task AssertConflictAsync(
        UsageSubmission submission,
        CostBinding binding,
        CostPrice price) =>
        await Assert.ThrowsAsync<UsageLedgerConflictException>(() =>
            _ledger.AppendAsync(submission, binding, price));

    private async Task AppendForScopeAsync(
        UsageAttribution attribution,
        UsageMeasurement measurement,
        CostPrice price)
    {
        var usage = UsageContractTests.Submission(
            attribution,
            measurement: measurement);
        await _ledger.AppendAsync(usage, UsageContractTests.Binding(), price);
    }

    private async Task CreateSessionAsync(string projectId, string runId, string sessionId)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using (var binding = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".session_provider_bindings (
                project_id, run_id, provider_id, adapter_version, options_schema_version,
                options_revision, resource_id, resource_generation, negotiated_capabilities)
            VALUES (@project_id, @run_id, 'test-provider', 'v1', 1, 'options-v1',
                'test-resource', 1, '[]'::jsonb)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection))
        {
            binding.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
            binding.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);
            await binding.ExecuteNonQueryAsync();
        }
        await using (var stream = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".session_run_streams (project_id, run_id)
            VALUES (@project_id, @run_id)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection))
        {
            stream.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
            stream.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);
            await stream.ExecuteNonQueryAsync();
        }
        await using (var session = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".sessions (project_id, run_id, session_id)
            VALUES (@project_id, @run_id, @session_id)
            ON CONFLICT (project_id, run_id, session_id) DO NOTHING
            """, connection))
        {
            session.Parameters.AddWithValue("project_id", NpgsqlDbType.Varchar, projectId);
            session.Parameters.AddWithValue("run_id", NpgsqlDbType.Varchar, runId);
            session.Parameters.AddWithValue("session_id", NpgsqlDbType.Varchar, sessionId);
            await session.ExecuteNonQueryAsync();
        }
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> RateCardCountAsync(string cardId, string version)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".usage_rate_cards
            WHERE card_id = @card_id AND version = @version
            """, connection);
        command.Parameters.AddWithValue("card_id", NpgsqlDbType.Varchar, cardId);
        command.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, version);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
