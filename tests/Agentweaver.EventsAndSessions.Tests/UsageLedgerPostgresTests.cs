using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.EventsAndSessions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Identity;
using Agentweaver.Providers;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    public async Task NativeConsumerCommitsPriceLedgerRateCardInboxAndReceiptOnceAcrossRestart()
    {
        var receipt = NativeReceipt();
        var consumer = NativeConsumer();
        var first = await consumer.AppendAsync(receipt, _ => Task.CompletedTask, default);
        Assert.False(first.IsDuplicate);
        Assert.Equal(0.01572m, first.Accounting.Amount);
        Assert.Equal(CostDisposition.Estimate, first.Accounting.Disposition);
        var restarted = NativeConsumer();
        var replays = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            restarted.AppendAsync(receipt, _ => Task.CompletedTask, default)));
        Assert.All(replays, replay =>
        {
            Assert.True(replay.IsDuplicate);
            Assert.Equal(first.Accounting, replay.Accounting);
        });
        var changedUsage = receipt.Usage with
        {
            Measurement = receipt.Usage.Measurement with { CacheWriteTokens = 6 }
        };
        await Assert.ThrowsAsync<UsageLedgerConflictException>(() => restarted.AppendAsync(receipt with
        {
            Usage = changedUsage,
            CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(receipt.Registration, changedUsage)
        }, _ => Task.CompletedTask, default));
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.Equal(1, totals.Events);
        var agent = Assert.Single(totals.Agents);
        Assert.Equal(5, agent.CacheWriteTokens);
        Assert.Null(agent.RequestCount);
        Assert.Equal(0.01572m, Assert.Single(totals.Amounts).Amount);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var counts = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".usage_ledger),
                   (SELECT count(*) FROM "{_schema}".usage_rate_cards),
                   (SELECT count(*) FROM "{_schema}".usage_run_cost_bindings),
                   (SELECT count(*) FROM "{_schema}".usage_source_receipts),
                   (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events.native-sdk-usage.v1')
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 5), column => Assert.Equal(1L, reader.GetInt64(column)));
    }

    [Fact]
    public async Task NativeCostPreflightPinsAndChecksPricingWithoutAppendingUsage()
    {
        await CreateSessionAsync("project-1", "preflight-run", "session-1");
        var preflights = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            NativeConsumer().PreflightAsync(
                "tenant-1", "project-1", "preflight-run", SdkMeterSources.CopilotNanoAiu, "gpt-6.1-sol",
                _ => Task.CompletedTask, default)));

        Assert.All(preflights, preflight =>
        {
            Assert.True(preflight.IsPriced);
            Assert.NotNull(preflight.Binding);
            Assert.Null(preflight.UnpricedReason);
        });
        Assert.Equal("github-copilot-published", preflights[0].Binding!.RateCard.Id);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "preflight-run");
        Assert.Equal(0, totals.Events);
        Assert.Empty(totals.Amounts);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var counts = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".usage_ledger),
                   (SELECT count(*) FROM "{_schema}".usage_rate_cards),
                   (SELECT count(*) FROM "{_schema}".usage_run_cost_bindings),
                   (SELECT count(*) FROM "{_schema}".usage_source_receipts),
                   (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts)
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(0L, reader.GetInt64(3));
        Assert.Equal(0L, reader.GetInt64(4));
    }

    [Fact]
    public async Task NativeCostReconciliationReturnsExactReceiptsAndRunScopedAccountingRevision()
    {
        var consumer = NativeConsumer();
        var first = NativeReceipt();
        var firstAck = await consumer.AppendAsync(first, _ => Task.CompletedTask, default);
        var second = NativeReceipt(sessionId: "session-2");
        await consumer.AppendAsync(second, _ => Task.CompletedTask, default);
        var request = ReconciliationRequest(first, firstAck, minimumAccountingRevision: 1);

        var result = await consumer.ReconcileAsync(
            "tenant-1", "project-1", "run-1", request, _ => Task.CompletedTask, default);

        RuntimeUsageCostReconciliationContract.ValidateReceipt(
            result, request, "tenant-1", "project-1", "run-1");
        Assert.Equal(2, result.AccountingRevision);
        Assert.Equal(2, result.CopilotTotals.Events);
        Assert.Equal(firstAck.SourceReceiptId, Assert.Single(result.RepresentedReceipts).SourceReceiptId);
        Assert.Equal(0.03144m, Assert.Single(result.CopilotTotals.Amounts).Amount);

        var downgradedAmounts = result.CopilotTotals.Amounts.Select(amount => amount with
        {
            Amount = 0m,
            PricedEvents = 0,
            UnpricedEvents = checked(amount.PricedEvents + amount.UnpricedEvents)
        }).ToImmutableArray();
        var downgradedAgents = result.CopilotTotals.Agents.Select(agent => agent with
        {
            IsFullyPriced = false,
            Amounts = agent.Amounts.Select(amount => amount with
            {
                Amount = 0m,
                PricedEvents = 0,
                UnpricedEvents = checked(amount.PricedEvents + amount.UnpricedEvents)
            }).ToImmutableArray()
        }).ToImmutableArray();
        var downgraded = result with
        {
            CopilotTotals = result.CopilotTotals with
            {
                IsFullyPriced = false,
                Amounts = downgradedAmounts,
                Agents = downgradedAgents
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostReconciliationContract.ValidateReceipt(
                downgraded, request, "tenant-1", "project-1", "run-1"));

        var agentMismatch = result with
        {
            CopilotTotals = result.CopilotTotals with
            {
                Agents = result.CopilotTotals.Agents.Select(agent => agent with { Amounts = [] })
                    .ToImmutableArray()
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostReconciliationContract.ValidateReceipt(
                agentMismatch, request, "tenant-1", "project-1", "run-1"));

        var secondSend = request with { CostTurnId = Guid.NewGuid() };
        var mismatch = await Assert.ThrowsAsync<NativeUsageReconciliationException>(() =>
            consumer.ReconcileAsync(
                "tenant-1", "project-1", "run-1", secondSend, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_usage_reconciliation_mismatch", mismatch.Code);
        Assert.Equal(firstAck.SourceReceiptId, Assert.Single(mismatch.SourceReceiptIds));
    }

    [Fact]
    public async Task NativeCostReconciliationKeepsUnpricedUsageVisibleOutsideThePricedSubtotal()
    {
        var consumer = NativeConsumer();
        var pricedSource = NativeReceipt();
        var priced = await consumer.AppendAsync(pricedSource, _ => Task.CompletedTask, default);
        var unpriced = await consumer.AppendAsync(
            NativeReceipt(sessionId: "session-2", modelId: "unknown-model"),
            _ => Task.CompletedTask, default);
        var request = ReconciliationRequest(pricedSource, priced, minimumAccountingRevision: 2);

        var result = await consumer.ReconcileAsync(
            "tenant-1", "project-1", "run-1", request, _ => Task.CompletedTask, default);

        RuntimeUsageCostReconciliationContract.ValidateReceipt(
            result, request, "tenant-1", "project-1", "run-1");
        Assert.Equal(2, result.CopilotTotals.Events);
        Assert.False(result.CopilotTotals.IsFullyPriced);
        var amount = Assert.Single(result.CopilotTotals.Amounts);
        Assert.Equal(priced.Accounting.Amount!.Value, amount.Amount);
        Assert.Equal(1, amount.PricedEvents);
        Assert.Equal(1, amount.UnpricedEvents);
        Assert.Equal(CostDisposition.Unpriced, unpriced.Accounting.Disposition);
        Assert.Equal("model-rate-unavailable", unpriced.Accounting.UnpricedReason);
    }

    [Fact]
    public async Task NativeCostReconciliationRejectsUsageWithoutA2AMessageBinding()
    {
        var consumer = NativeConsumer();
        var source = NativeReceipt(withoutA2AMessageId: true);
        var acknowledgment = await consumer.AppendAsync(source, _ => Task.CompletedTask, default);
        var request = ReconciliationRequest(source, acknowledgment);

        var error = await Assert.ThrowsAsync<NativeUsageReconciliationException>(() =>
            consumer.ReconcileAsync(
                "tenant-1", "project-1", "run-1", request, _ => Task.CompletedTask, default));

        Assert.Equal("runtime_usage_reconciliation_mismatch", error.Code);
        Assert.Equal(acknowledgment.SourceReceiptId, Assert.Single(error.SourceReceiptIds));
    }

    [Fact]
    public async Task NativeCostReconciliationKeepsMissingChangedAndStaleEvidenceHeld()
    {
        var consumer = NativeConsumer();
        var source = NativeReceipt();
        var acknowledgment = await consumer.AppendAsync(source, _ => Task.CompletedTask, default);
        var request = ReconciliationRequest(source, acknowledgment);

        var missingId = Guid.NewGuid();
        var missing = await Assert.ThrowsAsync<NativeUsageReconciliationException>(() =>
            consumer.ReconcileAsync("tenant-1", "project-1", "run-1", request with
            {
                RequiredReceipts =
                [
                    request.RequiredReceipts[0] with { SourceReceiptId = missingId }
                ]
            }, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_usage_reconciliation_incomplete", missing.Code);
        Assert.Equal(missingId, Assert.Single(missing.SourceReceiptIds));

        var changed = await Assert.ThrowsAsync<NativeUsageReconciliationException>(() =>
            consumer.ReconcileAsync("tenant-1", "project-1", "run-1", request with
            {
                RequiredReceipts =
                [
                    request.RequiredReceipts[0] with
                    {
                        Accounting = request.RequiredReceipts[0].Accounting with
                        {
                            Amount = request.RequiredReceipts[0].Accounting.Amount + 1m
                        }
                    }
                ]
            }, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_usage_reconciliation_mismatch", changed.Code);
        Assert.Equal(acknowledgment.SourceReceiptId, Assert.Single(changed.SourceReceiptIds));

        var stale = await Assert.ThrowsAsync<NativeUsageReconciliationException>(() =>
            consumer.ReconcileAsync("tenant-1", "project-1", "run-1", request with
            {
                MinimumAccountingRevision = 2
            }, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_usage_reconciliation_stale", stale.Code);
    }

    [Fact]
    public async Task NativeCostReconciliationPreservesUnpricedRequiredReceipts()
    {
        var source = NativeReceipt(missingMeasurements: true);
        var acknowledgment = await NativeConsumer().AppendAsync(source, _ => Task.CompletedTask, default);
        var request = ReconciliationRequest(source, acknowledgment);
        var receipt = await NativeConsumer().ReconcileAsync(
            "tenant-1", "project-1", "run-1", request,
            _ => Task.CompletedTask, default);

        RuntimeUsageCostReconciliationContract.ValidateReceipt(
            receipt, request, "tenant-1", "project-1", "run-1");
        Assert.Equal(acknowledgment.SourceReceiptId,
            Assert.Single(receipt.RepresentedReceipts).SourceReceiptId);
        Assert.Equal(CostDisposition.Unpriced,
            Assert.Single(receipt.RepresentedReceipts).Accounting.Disposition);
        Assert.False(receipt.CopilotTotals.IsFullyPriced);
    }

    [Fact]
    public async Task NativeCostPreflightReportsUnknownModelAsUnpriced()
    {
        var preflight = await NativeConsumer().PreflightAsync(
            "tenant-1", "project-1", "unpriced-preflight", SdkMeterSources.CopilotNanoAiu, "unpriced-model",
            _ => Task.CompletedTask, default);

        Assert.False(preflight.IsPriced);
        Assert.NotNull(preflight.Binding);
        Assert.Equal("model-rate-unavailable", preflight.UnpricedReason);
    }

    [Fact]
    public async Task NativeCostPreflightAuthorityFailureRollsBackNewRunPin()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeConsumer().PreflightAsync(
                "tenant-1", "project-1", "recheck-failure", SdkMeterSources.CopilotNanoAiu, "gpt-6.1-sol",
                _ => Task.FromException(new InvalidOperationException("selection changed")), default));

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".usage_run_cost_bindings", connection);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NativeCostPreflightDoesNotReplaceAnExistingUnpricedRunPin()
    {
        var options = new PostgresSessionsProviderOptions(
            "native-postgres", "test", 1, _schema, "native-options", 1);
        var unconfigured = new NativeUsageReceiptConsumer(_fixture.DataSource, options, _ledger);

        var first = await unconfigured.PreflightAsync(
            "tenant-1", "project-1", "unpriced-pin", SdkMeterSources.CopilotNanoAiu, "gpt-6.1-sol",
            _ => Task.CompletedTask, default);
        var second = await NativeConsumer().PreflightAsync(
            "tenant-1", "project-1", "unpriced-pin", SdkMeterSources.CopilotNanoAiu, "gpt-6.1-sol",
            _ => Task.CompletedTask, default);

        Assert.False(first.IsPriced);
        Assert.Null(first.Binding);
        Assert.Equal("cost-provider-unavailable", first.UnpricedReason);
        Assert.False(second.IsPriced);
        Assert.Null(second.Binding);
        Assert.Equal(first.UnpricedReason, second.UnpricedReason);
    }

    [Fact]
    public async Task SelectedByokTokensRemainDurableAndUnpricedWithoutAnAdmittedCostProvider()
    {
        var hosted = NativeReceipt();
        var registration = hosted.Registration with
        {
            Binding = hosted.Registration.Binding with
            {
                ModelSourceMode = ModelSourceMode.Byok,
                ModelBindingPin = new(1, hosted.Usage.SdkSource!.ModelSelectionReference,
                    hosted.Usage.SdkSource.ModelId, ModelSourceMode.Byok, "byok-bindings-v1", new string('a', 64))
                {
                    ProviderType = "azure"
                }
            }
        };
        var source = hosted.Usage.SdkSource! with
        {
            SourceMode = "byok", MeterSource = SdkMeterSources.ByokTokens, ModelMultiplier = null,
            ByokProvider = new("azure", hosted.Usage.SdkSource.ModelId, new string('a', 64)),
            ModelBindingPin = registration.Binding.ModelBindingPin,
            ResolvedModelBindingPin = registration.Binding.ModelBindingPin
        };
        var sdkEvent = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, sdkEvent),
            sdkEvent, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, null, 12.5m)
        {
            A2AMessageId = Guid.NewGuid()
        };
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        var receipt = new RuntimeUsageSourceReceipt(1, Guid.NewGuid(), registration, usage,
            RuntimeUsageSourceReceiptContract.Hash(registration, usage), DateTimeOffset.UtcNow);
        // A distinct SDK event keeps the uncorrelated usage from reusing the correlated usage event ID.
        var uncorrelatedEvent = Guid.NewGuid().ToString("D");
        var undispatched = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation with
        {
            EventId = SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, uncorrelatedEvent),
            SdkEventId = uncorrelatedEvent,
            A2AMessageId = null
        });
        var untrusted = Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageSourceReceiptContract.RequireTrustedAzureByok(receipt with
            {
                Usage = undispatched,
                CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(registration, undispatched)
            }));
        Assert.Equal("runtime_byok_azure_provenance_invalid", untrusted.Code);
        var uncorrelated = await NativeConsumer().AppendAsync(receipt with
        {
            ReceiptId = Guid.NewGuid(), Usage = undispatched,
            CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(registration, undispatched)
        }, _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Unpriced, uncorrelated.Accounting.Disposition);
        Assert.Null(uncorrelated.Accounting.Amount);
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);

        var first = await NativeConsumer().AppendAsync(receipt, _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Unpriced, first.Accounting.Disposition);
        Assert.Null(first.Accounting.Amount);
        Assert.Null(first.Accounting.RateCardId);
        Assert.False(string.IsNullOrWhiteSpace(first.Accounting.UnpricedReason));
        var replay = await NativeConsumer().AppendAsync(receipt, _ => Task.CompletedTask, default);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(first.Accounting, replay.Accounting);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.False(totals.IsFullyPriced);
        Assert.Empty(totals.Amounts);
        Assert.Equal(34, Assert.Single(totals.Agents).InputTokens);
    }

    [Fact]
    public async Task NativeConsumerAuthorityFailureAfterLedgerWaitRollsBackEveryAccountingArtifact()
    {
        var receipt = NativeReceipt();
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => NativeConsumer().AppendAsync(
            receipt, _ => throw new RuntimeAuthorizationException("runtime_usage_authority_denied"), default));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var counts = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".usage_ledger),
                   (SELECT count(*) FROM "{_schema}".usage_rate_cards),
                   (SELECT count(*) FROM "{_schema}".usage_run_cost_bindings),
                   (SELECT count(*) FROM "{_schema}".usage_source_receipts),
                   (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts
                    WHERE consumer_id = 'events.native-sdk-usage.v1')
            """, connection);
        await using (var reader = await counts.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.All(Enumerable.Range(0, 5), column => Assert.Equal(0L, reader.GetInt64(column)));
        }
        Assert.False((await NativeConsumer().AppendAsync(
            receipt, _ => Task.CompletedTask, default)).IsDuplicate);
    }

    [Fact]
    public async Task NativeConsumerKeepsMissingMeasurementsAndUnknownRatesUnpricedAndRejectsByok()
    {
        var receipt = NativeReceipt(missingMeasurements: true, modelId: "unknown-native-model");
        var accepted = await NativeConsumer().AppendAsync(receipt, _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Unpriced, accepted.Accounting.Disposition);
        Assert.Equal("model-rate-unavailable", accepted.Accounting.UnpricedReason);
        Assert.Null(accepted.Accounting.Amount);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        var agent = Assert.Single(totals.Agents);
        Assert.Null(agent.RequestCount);
        Assert.Null(agent.InputTokens);
        Assert.Null(agent.CacheWriteTokens);
        Assert.Null(agent.DurationMilliseconds);
        var byok = receipt.Usage with
        {
            SdkSource = receipt.Usage.SdkSource! with { SourceMode = "byok" }
        };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => NativeConsumer().AppendAsync(receipt with
        {
            Usage = byok, CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(receipt.Registration, byok)
        }, _ => Task.CompletedTask, default));
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedPricedUsageIsFullyPricedWithMissingOrPartialOptionalAccounting(bool hasMetadata)
    {
        var source = new SdkSessionFacts(Guid.NewGuid(), "native-session", "1.0.18", "1.0.97",
            "model/ref", "model-1", new string('a', 64), 1m, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, new string('b', 64), 1);
        var card = UsageContractTests.Card() with { MeterSource = source.MeterSource };
        var binding = UsageContractTests.Binding(card) with { MeterSource = source.MeterSource };
        var usage = UsageContractTests.Submission() with
        {
            SdkSource = source,
            ModelBinding = UsageContractTests.Submission().ModelBinding with { MeterSource = source.MeterSource },
            Measurement = UsageContractTests.Submission().Measurement with { ProviderUnit = "nano_aiu" },
            SdkAccounting = hasMetadata
                ? new(new(source.SdkSessionId, 4, "usage-4"), SdkAiCreditsStatus.Partial, true)
                : null
        };
        var price = UsageContractTests.Price(card);
        await _ledger.AppendAsync(usage, binding, price);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        var agent = Assert.Single(totals.Agents);
        Assert.True(totals.IsFullyPriced);
        Assert.True(agent.IsFullyPriced);
        Assert.Equal(price.Amount, Assert.Single(totals.Amounts).Amount);
        Assert.Equal(price.Amount, Assert.Single(agent.Amounts).Amount);
    }

    [Fact]
    public async Task HostedTokenPricedUsageIsFullyPricedWithoutNanoAiu()
    {
        var native = NativeReceipt();
        // Complete token measurements can use the published rates when the SDK omits Nano AIU.
        var usage = native.Usage with
        {
            Measurement = native.Usage.Measurement with { ProviderUnits = null, ProviderUnit = null },
            SdkAccounting = null
        };
        var receipt = native with
        {
            Usage = usage,
            CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(native.Registration, usage)
        };

        var acknowledgment = await NativeConsumer().AppendAsync(receipt, _ => Task.CompletedTask, default);

        Assert.Equal(CostDisposition.Estimate, acknowledgment.Accounting.Disposition);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, acknowledgment.Accounting.Unit);
        Assert.NotNull(acknowledgment.Accounting.Amount);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.True(totals.IsFullyPriced);
        var amount = Assert.Single(totals.Amounts);
        Assert.Equal(1, amount.PricedEvents);
        Assert.Equal(0, amount.UnpricedEvents);
        Assert.Equal(acknowledgment.Accounting.Amount, amount.Amount);
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

    [Fact]
    public async Task RunAdmissionAcceptsAndPreservesAnUnknownUnpricedModel()
    {
        var selection = RunAdmissionSelection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        using var services = AdmissionAuthentication(clock.GetUtcNow().AddMinutes(1));
        var context = AdmissionContext(services);
        var projects = new AdmissionProjectsClient(AdmissionAuthority());
        var owner = new AdmissionSelectionHandler(_ => selection);
        using var client = new HttpClient(owner);
        var receipt = await AdmissionService(client, projects, clock, modelId: "unknown-model").ReadAsync(
            context, "project-1", "run-1", request, default);
        var replay = await AdmissionService(client, projects, clock, modelId: "unknown-model").ReadAsync(
            context, "project-1", "run-1", request, default);
        Assert.Equal("unknown-model", receipt.ModelBindingPin.ModelId);
        Assert.Equal(CostDisposition.Unpriced, receipt.Quote.Disposition);
        Assert.Null(receipt.Quote.Amount);
        Assert.Equal("model-rate-unavailable", receipt.Quote.UnpricedReason);
        Assert.NotNull(receipt.Quote.RateCard);
        Assert.Empty(receipt.Quote.RateCard.ModelMultipliers);
        Assert.Equal(receipt.ModelBindingPin, replay.ModelBindingPin);
        var binding = Assert.IsType<CostBinding>(receipt.CostBinding);
        var replayBinding = Assert.IsType<CostBinding>(replay.CostBinding);
        Assert.True(binding.NegotiatedCapabilities.SetEquals(replayBinding.NegotiatedCapabilities));
        Assert.True(CopilotCostProvider.RateCardsEqual(
            binding.RateCard, replayBinding.RateCard));
        Assert.Equal(binding, replayBinding with
        {
            NegotiatedCapabilities = binding.NegotiatedCapabilities,
            RateCard = binding.RateCard
        });
        Assert.True(CopilotCostProvider.RateCardsEqual(
            receipt.Quote.RateCard, Assert.IsType<CostRateCard>(replay.Quote.RateCard)));
        Assert.Equal(receipt.Quote, replay.Quote with { RateCard = receipt.Quote.RateCard });
        Assert.Equal(4, owner.CallCount);
        Assert.Equal(4, projects.CallCount);
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            receipt, request, "tenant-1", "project-1", "run-1", selection));
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            replay, request, "tenant-1", "project-1", "run-1", selection));
        Assert.Equal(new long[] { 0, 0, 0, 1 }, await ReadPersistedCountsAsync());
        var accounting = await NativeConsumer().AppendAsync(
            NativeReceipt(modelId: "unknown-model"), _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Unpriced, accounting.Accounting.Disposition);
        Assert.Null(accounting.Accounting.Amount);
        Assert.Equal("model-rate-unavailable", accounting.Accounting.UnpricedReason);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.False(totals.IsFullyPriced);
        var amount = Assert.Single(totals.Amounts);
        Assert.Equal(1, amount.UnpricedEvents);
        Assert.Equal(0, amount.PricedEvents);
        Assert.Equal(0, amount.Amount);
        Assert.Equal(new long[] { 1, 1, 1, 1 }, await ReadPersistedCountsAsync());

        async Task<long[]> ReadPersistedCountsAsync()
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var counts = new NpgsqlCommand($"""
                SELECT (SELECT count(*) FROM "{_schema}".usage_ledger),
                       (SELECT count(*) FROM "{_schema}".usage_source_receipts),
                       (SELECT count(*) FROM "{_schema}".usage_rate_cards),
                       (SELECT count(*) FROM "{_schema}".usage_run_cost_bindings)
                """, connection);
            await using var reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return Enumerable.Range(0, 4).Select(column => reader.GetInt64(column)).ToArray();
        }
    }

    [Fact]
    public async Task RunAdmissionAcceptsUnpricedUsageWhenCostProviderIsUnavailable()
    {
        var consumer = NativeConsumerWithoutCostProvider();
        var recorded = await consumer.AppendAsync(
            NativeReceipt(), _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Unpriced, recorded.Accounting.Disposition);
        Assert.Null(recorded.Accounting.Amount);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, recorded.Accounting.Unit);
        Assert.Equal("cost-provider-unavailable", recorded.Accounting.UnpricedReason);

        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.False(totals.IsFullyPriced);
        var amount = Assert.Single(totals.Amounts);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, amount.Unit);
        Assert.Equal(0, amount.Amount);
        Assert.Equal(0, amount.PricedEvents);
        Assert.Equal(1, amount.UnpricedEvents);

        var selection = RunAdmissionSelection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        using var services = AdmissionAuthentication(clock.GetUtcNow().AddMinutes(1));
        var context = AdmissionContext(services);
        var projects = new AdmissionProjectsClient(AdmissionAuthority());
        using var client = new HttpClient(new AdmissionSelectionHandler(_ => selection));
        var admission = await AdmissionService(client, projects, clock, consumer: consumer).ReadAsync(
            context, "project-1", "run-1", request, default);

        Assert.Null(admission.CostBinding);
        Assert.Equal(CostDisposition.Unpriced, admission.Quote.Disposition);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, admission.Quote.Unit);
        Assert.Equal("cost-provider-unavailable", admission.Quote.UnpricedReason);
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            admission, request, "tenant-1", "project-1", "run-1", selection));
    }

    [Theory]
    [InlineData("gpt-6.1-sol", 0.01572)]
    [InlineData("gpt-6-luna", 0.0007895)]
    public async Task RunAdmissionAndNativeReceiptUseThePublishedTokenPrice(
        string modelId, decimal expectedAmount)
    {
        var selection = RunAdmissionSelection();
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        using var services = AdmissionAuthentication(clock.GetUtcNow().AddMinutes(1));
        var context = AdmissionContext(services);
        var projects = new AdmissionProjectsClient(AdmissionAuthority());
        var owner = new AdmissionSelectionHandler(_ => selection);
        using var client = new HttpClient(owner);

        var admission = await AdmissionService(client, projects, clock, modelId: modelId).ReadAsync(
            context, "project-1", "run-1", request, default);
        Assert.Equal(0, RuntimeRunAdmissionContract.ValidateReceipt(
            admission, request, "tenant-1", "project-1", "run-1", selection));
        Assert.Equal(CostDisposition.Estimate, admission.Quote.Disposition);
        Assert.Equal(0, admission.Quote.Amount);
        Assert.True(admission.CostBinding!.RateCard.ModelTokenRates.ContainsKey(modelId));

        var recorded = await NativeConsumer().AppendAsync(
            NativeReceipt(modelId: modelId), _ => Task.CompletedTask, default);
        Assert.Equal(CostDisposition.Estimate, recorded.Accounting.Disposition);
        Assert.Equal(expectedAmount, recorded.Accounting.Amount);
        Assert.Null(recorded.Accounting.UnpricedReason);
        var totals = await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1");
        Assert.True(totals.IsFullyPriced);
        var amount = Assert.Single(totals.Amounts);
        Assert.Equal(expectedAmount, amount.Amount);
        Assert.Equal(1, amount.PricedEvents);
        Assert.Equal(0, amount.UnpricedEvents);
    }

    [Theory]
    [InlineData("tenant", "runtime_run_admission_authority_changed", 2)]
    [InlineData("project", "runtime_run_admission_authority_changed", 2)]
    [InlineData("run", "runtime_run_admission_authority_changed", 2)]
    [InlineData("actor", "runtime_run_admission_authority_changed", 2)]
    [InlineData("accept-permission", "runtime_run_admission_authority_changed", 2)]
    [InlineData("read-permission", "runtime_usage_authority_denied", 2)]
    [InlineData("selection", "runtime_run_admission_selection_changed", 2)]
    [InlineData("expiry", "runtime_run_admission_authority_changed", 2)]
    [InlineData("initial-read", "runtime_usage_authority_denied", 0)]
    [InlineData("initial-accept", "runtime_run_admission_authority_denied", 0)]
    [InlineData("initial-scope", "runtime_run_admission_authority_denied", 0)]
    [InlineData("owner", "runtime_run_admission_owner_unavailable", 0)]
    [InlineData("actor-expired", "runtime_usage_actor_expired", 0)]
    [InlineData("model", "runtime_run_admission_model_mode_invalid", 1)]
    public async Task RunAdmissionServiceRejectsUnavailableOrChangedAuthorityWithoutPersistingCostBinding(
        string fault, string expectedCode, int expectedOwnerReads)
    {
        var selection = RunAdmissionSelection(includeConnection: fault != "model");
        var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(selection));
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        using var services = AdmissionAuthentication(
            clock.GetUtcNow().AddMinutes(fault == "actor-expired" ? -1 : 1));
        var context = AdmissionContext(services);
        var authority = AdmissionAuthority();
        var first = fault switch
        {
            "initial-read" => AdmissionAuthority("acceptRunSelection"),
            "initial-accept" => AdmissionAuthority("readRunSelection"),
            "initial-scope" => authority with { BoundRunId = "another-run" },
            _ => authority
        };
        var current = fault switch
        {
            "tenant" => authority with { TenantId = "another-tenant" },
            "project" => authority with
            {
                BoundProjectId = "another-project",
                EffectiveAuthority = [new("project", "another-project", authority.EffectiveAuthority[0].Permissions)]
            },
            "run" => authority with { BoundRunId = "another-run" },
            "actor" => authority with { ActorId = "another-actor" },
            "accept-permission" => AdmissionAuthority("readRunSelection"),
            "read-permission" => AdmissionAuthority("acceptRunSelection"),
            _ => authority
        };
        var projects = new AdmissionProjectsClient(first, current);
        var owner = new AdmissionSelectionHandler(call =>
        {
            if (call == 2 && fault == "expiry")
                clock.UtcNow = clock.GetUtcNow().AddMinutes(2);
            return call == 2 && fault == "selection"
                ? RunAdmissionSelection(contextRevision: "changed-selection")
                : selection;
        });
        using var client = new HttpClient(owner);
        var service = AdmissionService(client, projects, clock,
            fault == "owner" ? "not-an-absolute-address" : "https://projects.test/");
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            service.ReadAsync(context, "project-1", "run-1", request, default));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedOwnerReads, owner.CallCount);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".usage_run_cost_bindings", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    private RuntimeRunAdmissionService AdmissionService(
        HttpClient client, IProjectsAuthorizationContextClient projects, TimeProvider clock,
        string ownerAddress = "https://projects.test/", string modelId = "gpt-6.1-sol",
        NativeUsageReceiptConsumer? consumer = null) =>
        new(client, new(ownerAddress, "events-api", "https://identity.test/"), projects,
            consumer ?? NativeConsumer(),
            new RuntimeModelBindingsResolver("model-bindings-v1", new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-native-reference"] = new(modelId, ModelSourceMode.HostedCopilot)
            }), clock);

    private static ProjectsAuthorizationContextResponse AdmissionAuthority(params string[] permissions) =>
        new(1, "https://identity.test/", "actor-1", "tenant-1", 1, "project-1", "run-1",
            [new("project", "project-1",
                (permissions.Length == 0 ? ["readRunSelection", "acceptRunSelection"] : permissions)
                .Select(permission => new ProjectsAuthorizationPermissionGrant(permission, 1))
                .ToImmutableArray())]);

    private static ServiceProvider AdmissionAuthentication(DateTimeOffset expiry) =>
        new ServiceCollection()
            .AddSingleton<IAuthenticationService>(new AdmissionAuthenticationService(expiry))
            .BuildServiceProvider();

    private static HttpContext AdmissionContext(IServiceProvider services)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "actor-1")], "admission-test"))
        };
        context.Request.Headers.Authorization = "Bearer admission-original-bearer";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        return context;
    }

    private sealed class AdmissionClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class AdmissionProjectsClient(
        params ProjectsAuthorizationContextResponse[] responses) : IProjectsAuthorizationContextClient
    {
        public int CallCount { get; private set; }

        public Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
            HttpContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responses[Math.Min(CallCount++, responses.Length - 1)]);
        }
    }

    private sealed class AdmissionSelectionHandler(Func<int, JsonElement> selection) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://projects.test/api/projects/project-1/runs/run-1/selection",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("admission-original-bearer", request.Headers.Authorization?.Parameter);
            Assert.Equal(["tenant-1"], request.Headers.GetValues("X-Agentweaver-Tenant"));
            Assert.True(request.Headers.CacheControl?.NoStore);
            Assert.True(request.Headers.CacheControl?.NoCache);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
                Content = new StringContent(selection(++CallCount).GetRawText(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AdmissionAuthenticationService(DateTimeOffset expiry) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                context.User, new AuthenticationProperties { ExpiresUtc = expiry }, scheme ?? "admission-test")));

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }

    private static JsonElement RunAdmissionSelection(
        bool includeConnection = true, string contextRevision = "selection-v1") => JsonSerializer.SerializeToElement(new
    {
        projectId = "project-1", runId = "run-1", projectRevision = 1, projectConfigurationRevision = 1,
        platformRuntimeRevision = 1, contextRevision,
        projectConfiguration = new { },
        modelSelection = new
        {
            reference = "accepted-native-reference", sourceMode = "hostedCopilot",
            connectionId = includeConnection ? "11111111-1111-1111-1111-111111111111" : null
        }
    });

    [Fact]
    public async Task ZeroWorkCostSnapshotPinsPricingWithoutInventingUsageOrAccountingReceipts()
    {
        var native = NativeReceipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var snapshot = await NativeConsumer().ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
        Assert.Equal(0, snapshot.CopilotTotals.Events);
        Assert.Empty(snapshot.CopilotTotals.Amounts);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var counts = new NpgsqlCommand($"""
            SELECT (SELECT count(*) FROM "{_schema}".usage_ledger),
                   (SELECT count(*) FROM "{_schema}".usage_rate_cards),
                   (SELECT count(*) FROM "{_schema}".usage_source_receipts),
                   (SELECT count(*) FROM "{_schema}".consumer_inbox_receipts),
                   (SELECT count(*) FROM "{_schema}".usage_run_cost_bindings)
            """, connection);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.All(Enumerable.Range(0, 4), column => Assert.Equal(0L, reader.GetInt64(column)));
        Assert.Equal(1L, reader.GetInt64(4));
    }

    [Fact]
    public async Task CopilotCostSnapshotKeepsPricedObservedUsageSeparateFromUnpricedOtherMeters()
    {
        var native = NativeReceipt();
        var usage = native.Usage with
        {
            SdkAccounting = new(null, SdkAiCreditsStatus.Partial, true)
        };
        native = native with
        {
            Usage = usage,
            CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(native.Registration, usage)
        };
        var consumer = NativeConsumer();
        var acknowledgment = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var other = UsageContractTests.Submission();
        await _ledger.AppendAsync(other, null, new(null, null, CostDisposition.Unpriced, null, "other-meter-unavailable"));
        Assert.False((await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).IsFullyPriced);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var snapshot = await consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(acknowledgment.Accounting.Amount,
            RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
        Assert.Equal(1, snapshot.CopilotTotals.Events);
        Assert.True(snapshot.CopilotTotals.IsFullyPriced);
    }

    [Fact]
    public async Task CostSnapshotAuthorityFailureRollsBackItsRealPricingPin()
    {
        var native = NativeReceipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => NativeConsumer().ReadCostSnapshotAsync(
            request, _ => throw new RuntimeAuthorizationException("runtime_usage_authority_changed"), default));
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{_schema}\".usage_run_cost_bindings", connection);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservedSnapshotJoinsActualSourceAndLedgerReceiptsIncludingExplicitUnpriced(bool unpriced)
    {
        var native = NativeReceipt(missingMeasurements: unpriced);
        var dispatchId = Guid.NewGuid();
        var usage = native.Usage with { A2AMessageId = dispatchId };
        native = native with
        {
            Usage = usage, CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(native.Registration, usage)
        };
        var consumer = NativeConsumer();
        var acknowledged = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var reference = new RuntimeUsageCostReceiptReference(native.ReceiptId, acknowledged.Accounting);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = dispatchId, RequiredReceipts = [reference]
        };
        var snapshot = await consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default);
        RuntimeUsageCostSnapshotContract.ValidateObservedReceipt(snapshot, request);
        Assert.Equal(reference, Assert.Single(snapshot.RepresentedReceipts));
        Assert.Equal(1, snapshot.CopilotTotals.Events);
        if (unpriced)
        {
            Assert.Equal(CostDisposition.Unpriced, reference.Accounting.Disposition);
            Assert.Null(reference.Accounting.Amount);
            var amount = Assert.Single(snapshot.CopilotTotals.Amounts);
            Assert.Equal(0, amount.Amount);
            Assert.Equal(0, amount.PricedEvents);
            Assert.Equal(1, amount.UnpricedEvents);
            Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
        }
        else
            Assert.Equal(reference.Accounting.Amount,
                RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    [Theory]
    [InlineData("missing", "runtime_usage_accounting_pending")]
    [InlineData("event", "runtime_usage_accounting_pending")]
    [InlineData("dispatch", "runtime_usage_accounting_mismatch")]
    [InlineData("registration", "runtime_usage_accounting_mismatch")]
    [InlineData("source", "runtime_usage_accounting_mismatch")]
    [InlineData("hash", "runtime_usage_accounting_mismatch")]
    [InlineData("amount", "runtime_usage_accounting_mismatch")]
    public async Task ObservedSnapshotRejectsForeignOrForgedPersistedReceiptReferences(string fault, string expectedCode)
    {
        var native = NativeReceipt();
        var dispatchId = Guid.NewGuid();
        var usage = native.Usage with { A2AMessageId = dispatchId };
        native = native with
        {
            Usage = usage, CanonicalPayloadHash = RuntimeUsageSourceReceiptContract.Hash(native.Registration, usage)
        };
        var consumer = NativeConsumer();
        var acknowledged = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var reference = new RuntimeUsageCostReceiptReference(native.ReceiptId, acknowledged.Accounting);
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!)
        {
            DispatchId = dispatchId, RequiredReceipts = [reference]
        };
        request = fault switch
        {
            "missing" => request with { RequiredReceipts = [reference with { SourceReceiptId = Guid.NewGuid() }] },
            "event" => request with
            {
                RequiredReceipts = [reference with { Accounting = reference.Accounting with { EventId = Guid.NewGuid() } }]
            },
            "dispatch" => request with { DispatchId = Guid.NewGuid() },
            "registration" => request with
            {
                Registration = request.Registration with { ExpiresAt = request.Registration.ExpiresAt.AddMinutes(1) }
            },
            "source" => request with { Source = request.Source with { SdkVersion = "changed-sdk" } },
            "hash" => request with
            {
                RequiredReceipts = [reference with
                {
                    Accounting = reference.Accounting with { CanonicalPayloadHash = new string('f', 64) }
                }]
            },
            "amount" => request with
            {
                RequiredReceipts = [reference with { Accounting = reference.Accounting with { Amount = 999 } }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSnapshotJoinsUnassociatedUsageOnlyWithVerifiedExactOwnerEvidence(bool unpriced)
    {
        var native = NativeReceipt(missingMeasurements: unpriced, withoutA2AMessageId: true);
        var consumer = NativeConsumer();
        var acknowledged = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var reference = new RuntimeUsageCostReceiptReference(native.ReceiptId, acknowledged.Accounting);
        var recorded = NativeRecordedTurn(native);
        var request = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, [reference]);
        var denied = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_native_turn_evidence_required", denied.Code);
        var legacy = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            consumer.ReadCostSnapshotAsync(request with { NativeTurn = null }, _ => Task.CompletedTask, default));
        Assert.Equal("runtime_usage_accounting_mismatch", legacy.Code);
        var snapshot = await consumer.ReadCostSnapshotAsync(
            request, _ => Task.CompletedTask, default, recorded);
        Assert.Equal(reference, Assert.Single(snapshot.RepresentedReceipts));
        Assert.Equal(recorded.Admission.MessageId, snapshot.DispatchId);
        Assert.Equal(1, snapshot.CopilotTotals.Events);
        Assert.Equal(unpriced ? CostDisposition.Unpriced : CostDisposition.Estimate,
            snapshot.RepresentedReceipts[0].Accounting.Disposition);
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    [Theory]
    [InlineData("missing", "runtime_usage_accounting_pending")]
    [InlineData("wrong-raw-dispatch", "runtime_usage_accounting_mismatch")]
    [InlineData("late-event", "runtime_usage_accounting_mismatch")]
    [InlineData("hash", "runtime_usage_accounting_mismatch")]
    [InlineData("amount", "runtime_usage_accounting_mismatch")]
    [InlineData("foreign-record", "runtime_native_turn_accounting_invalid")]
    [InlineData("changed-revision", "runtime_native_turn_accounting_invalid")]
    [InlineData("event-set", "runtime_native_turn_accounting_invalid")]
    public async Task NativeSnapshotRejectsWrongLateOrForgedAccountingEvidence(string fault, string expectedCode)
    {
        var native = NativeReceipt(withoutA2AMessageId: fault != "wrong-raw-dispatch");
        var consumer = NativeConsumer();
        var acknowledged = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var reference = new RuntimeUsageCostReceiptReference(native.ReceiptId, acknowledged.Accounting);
        var recorded = NativeRecordedTurn(native);
        if (fault == "late-event")
        {
            var observation = recorded.Observation with { CompletedAt = native.Usage.OccurredAt.AddTicks(-1) };
            recorded = recorded with
            {
                Observation = observation, CanonicalPayloadHash = RuntimeNativeTurnContract.ObservationHash(observation)
            };
        }
        var request = RuntimeNativeTurnContract.AccountingSnapshotRequest(recorded, [reference]);
        var verified = recorded;
        switch (fault)
        {
            case "missing":
                request = request with { RequiredReceipts = [reference with { SourceReceiptId = Guid.NewGuid() }] };
                break;
            case "hash":
                request = request with
                {
                    RequiredReceipts = [reference with
                    {
                        Accounting = reference.Accounting with { CanonicalPayloadHash = new string('f', 64) }
                    }]
                };
                break;
            case "amount":
                request = request with
                {
                    RequiredReceipts = [reference with { Accounting = reference.Accounting with { Amount = 999 } }]
                };
                break;
            case "foreign-record":
                verified = verified with
                {
                    Admission = verified.Admission with { MessageId = Guid.NewGuid() }
                };
                break;
            case "changed-revision":
                verified = verified with { OwnerRevision = verified.OwnerRevision + 1 };
                break;
            case "event-set":
                request = request with { RequiredReceipts = [] };
                break;
        }
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default, verified));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    private static RuntimeNativeTurnRecordedReceipt NativeRecordedTurn(RuntimeUsageSourceReceipt native)
    {
        var admission = new RuntimeNativeTurnAdmissionReceipt(native.Registration, native.Usage.SdkSource!,
            Guid.NewGuid(), new string('c', 64), new string('d', 64), 2);
        var observation = new RuntimeNativeTurnObservation(admission.Source, Guid.NewGuid().ToString("D"),
            "native-turn", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), native.Usage.OccurredAt,
            "native-cursor", new string('e', 64), null)
        {
            UsageEventIds = [Guid.ParseExact(native.Usage.SdkEventId!, "D")]
        };
        return new(admission, observation, RuntimeNativeTurnContract.ObservationHash(observation), 3);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("generic")]
    [InlineData("revision")]
    [InlineData("foreign-dispatch")]
    [InlineData("event-set")]
    [InlineData("owner-denied")]
    [InlineData("actor")]
    [InlineData("expiry")]
    public async Task NativeSnapshotRechecksOwnerAndAuthorityAfterReleasingTheCostLock(string change)
    {
        var native = NativeReceipt(withoutA2AMessageId: change != "generic");
        var consumer = NativeConsumer();
        var acknowledged = await consumer.AppendAsync(native, _ => Task.CompletedTask, default);
        var recorded = NativeRecordedTurn(native);
        var request = RuntimeNativeTurnContract.AccountingSnapshotRequest(
            recorded, [new(native.ReceiptId, acknowledged.Accounting)]);
        if (change == "generic")
            request = request with { NativeTurn = null, DispatchId = native.Usage.A2AMessageId };
        var clock = new AdmissionClock(DateTimeOffset.UtcNow);
        using var authentication = AdmissionAuthentication(clock.GetUtcNow().AddMinutes(1));
        var context = AdmissionContext(authentication);
        var authority = AdmissionAuthority();
        var projects = new AdmissionProjectsClient(authority, authority,
            change == "actor" ? authority with { ActorId = "changed-actor" } : authority);
        var reads = 0;
        using var handler = new NativeEvidenceHandler(async (httpRequest, token) =>
        {
            Assert.Equal(HttpMethod.Post, httpRequest.Method);
            Assert.Equal("https://orchestrator.test/internal/runtime/turns/recorded",
                httpRequest.RequestUri?.AbsoluteUri);
            Assert.Equal("admission-original-bearer", httpRequest.Headers.Authorization?.Parameter);
            Assert.Equal(["tenant-1"], httpRequest.Headers.GetValues("X-Agentweaver-Tenant"));
            await using (var connection = await _fixture.DataSource.OpenConnectionAsync(token))
            await using (var transaction = await connection.BeginTransactionAsync(token))
            await using (var acquire = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtext('agentweaver.usage.cost'), hashtext(@run))",
                connection, transaction))
            {
                acquire.Parameters.AddWithValue("run", JsonSerializer.Serialize(new[]
                {
                    "tenant-1", "project-1", "run-1", SdkMeterSources.CopilotNanoAiu
                }));
                Assert.Equal(true, await acquire.ExecuteScalarAsync(token));
            }
            var response = recorded;
            if (++reads == 2)
            {
                if (change == "owner-denied")
                    return new(HttpStatusCode.Forbidden)
                    {
                        RequestMessage = httpRequest,
                        Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
                    };
                if (change == "expiry")
                    clock.UtcNow = clock.GetUtcNow().AddMinutes(2);
                if (change == "revision")
                    response = response with { OwnerRevision = response.OwnerRevision + 1 };
                if (change == "foreign-dispatch")
                    response = response with { Admission = response.Admission with { MessageId = Guid.NewGuid() } };
                if (change == "event-set")
                {
                    var observation = response.Observation with { UsageEventIds = [Guid.NewGuid()] };
                    response = response with
                    {
                        Observation = observation,
                        CanonicalPayloadHash = RuntimeNativeTurnContract.ObservationHash(observation)
                    };
                }
            }
            return new(HttpStatusCode.OK)
            {
                RequestMessage = httpRequest,
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
                Content = new StringContent(JsonSerializer.Serialize(response,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                    }), Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var service = new NativeUsageApplicationService(
            new(http, new("https://orchestrator.test/", "events", "https://identity.test/")),
            projects, consumer, _ledger, clock);
        if (change is "stable" or "generic")
        {
            var snapshot = await service.ReadCostSnapshotAsync(context, "project-1", "run-1", request, default);
            Assert.Equal(acknowledged.Accounting, Assert.Single(snapshot.RepresentedReceipts).Accounting);
        }
        else
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                service.ReadCostSnapshotAsync(context, "project-1", "run-1", request, default));
        Assert.Equal(change == "generic" ? 0 : 2, reads);
        Assert.Equal(1, (await _ledger.GetRunTotalsAsync("tenant-1", "project-1", "run-1")).Events);
    }

    private sealed class NativeEvidenceHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public async Task MissingCostProviderReturnsAnUnpricedSnapshotWithoutInventingACost()
    {
        var native = NativeReceipt();
        var request = new RuntimeUsageCostSnapshotRequest(1, native.Registration, native.Usage.SdkSource!);
        var consumer = new NativeUsageReceiptConsumer(_fixture.DataSource,
            new("native-postgres", "test", 1, _schema, "native-options", 1), _ledger);
        var snapshot = await consumer.ReadCostSnapshotAsync(request, _ => Task.CompletedTask, default);
        Assert.Null(snapshot.Binding);
        Assert.Null(snapshot.Quote.Amount);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, snapshot.Quote.Unit);
        Assert.Equal(CostDisposition.Unpriced, snapshot.Quote.Disposition);
        Assert.Equal("cost-provider-unavailable", snapshot.Quote.UnpricedReason);
        Assert.Equal(0, RuntimeUsageCostSnapshotContract.ValidateReceipt(snapshot, request));
    }

    private NativeUsageReceiptConsumer NativeConsumer()
    {
        var options = new PostgresSessionsProviderOptions(
            "native-postgres", "test", 1, _schema, "native-options", 1);
        var provider = new CopilotCostProvider(
            CopilotCostTests.CreateTestOptions() with
            {
                ResourceId = "native-cost",
                ResourceGeneration = 1,
                OptionsRevision = "cost-options-v1"
            });
        var catalog = ProviderCatalog.Create(
            [provider.CreateRegistration()], [], [],
            meterSourceSelections: [new(SdkMeterSources.CopilotNanoAiu, CopilotCostProvider.ProviderId)]);
        Assert.True(catalog.IsSuccess, catalog.Error?.Message);
        var binder = new CopilotCostProviderBinder(
            provider, new(catalog.Value!), NullLogger<CopilotCostProviderBinder>.Instance);
        return new(_fixture.DataSource, options, _ledger, provider, binder);
    }

    private NativeUsageReceiptConsumer NativeConsumerWithoutCostProvider() =>
        new(_fixture.DataSource,
            new PostgresSessionsProviderOptions("native-postgres", "test", 1, _schema, "native-options", 1),
            _ledger);

    private static RuntimeUsageCostReconciliationRequest ReconciliationRequest(
        RuntimeUsageSourceReceipt source,
        RuntimeUsageAccountingAcknowledgment acknowledgment,
        long minimumAccountingRevision = 0)
    {
        var binding = source.Registration.Binding;
        var accounting = acknowledgment.Accounting;
        return new(
            1,
            source.Usage.A2AMessageId ?? Guid.NewGuid(),
            source.Registration.RuntimeInstanceId,
            source.Registration.Revision,
            binding.ExecutionFence,
            binding.SessionId,
            binding.TurnId,
            binding.AcceptedSelectionHash,
            binding.ModelSelectionReference!,
            source.Usage.ModelBinding.ModelId,
            source.Usage.ModelBinding.MeterSource,
            accounting.RateCardId!,
            accounting.RateCardVersion!,
            accounting.Unit!,
            minimumAccountingRevision,
            [new(acknowledgment.SourceReceiptId, accounting)])
        {
            RateCardContentHash = accounting.RateCardContentHash
        };
    }

    private static RuntimeUsageSourceReceipt NativeReceipt(
        bool missingMeasurements = false, string modelId = "gpt-6.1-sol", string sessionId = "session-1",
        bool withoutA2AMessageId = false)
    {
        var registration = new RuntimeRegistration(
            Guid.NewGuid(), 1,
            new("https://broker.test/", Guid.NewGuid().ToString("D"), "tenant-1", "project-1", "run-1",
                sessionId, "agent-1", "native-turn", 1, 1, 1, "selection-1", new string('a', 64), 1,
                "environment", "placement", 1, "profile", new("https://runtime.test/configure"),
                new("https://orchestrator.test/internal/runtime/observations"))
            {
                ModelSelectionReference = "native-model-selection", PlacementProviderId = "sandbox",
                ModelSourceMode = ModelSourceMode.HostedCopilot,
                EnvironmentLifecycleGeneration = 1, EnvironmentLeaseRevision = 2,
                EnvironmentCurrentFencingGeneration = 3, EnvironmentProviderFencingGeneration = 3
            }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(1));
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.11", "runtime-v1", "native-model-selection", modelId, new string('b', 64),
            2.5m, "hosted-copilot", SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash, 1);
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, modelId,
            missingMeasurements ? null : 17, missingMeasurements ? null : 11,
            missingMeasurements ? null : 7, missingMeasurements ? null : 5,
            missingMeasurements ? null : 3, missingMeasurements ? null : 1234567.25m,
            missingMeasurements ? null : 12.5m)
        {
            A2AMessageId = withoutA2AMessageId ? null : Guid.NewGuid()
        };
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        return new(1, Guid.NewGuid(), registration, usage,
            RuntimeUsageSourceReceiptContract.Hash(registration, usage), DateTimeOffset.UtcNow);
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
