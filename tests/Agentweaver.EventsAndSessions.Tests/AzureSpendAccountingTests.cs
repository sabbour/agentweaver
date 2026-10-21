using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class AzureSpendAccountingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static AzureProvisionedThroughputCharge Charge(decimal amount = 100.00m, int digits = 2) =>
        new("azure-retail-prices.ptu.eastus2", "2026-10-01", "USD", amount, digits, Start, Start.AddMonths(1));

    [Fact]
    public void ProvisionedThroughputSharesAddUpExactlyAndBreakTiesByEventId()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = Guid.Parse("00000000-0000-0000-0000-000000000003");

        var allocation = AzureProvisionedThroughputAllocator.Allocate(Charge(),
            [new(third, 1, 0), new(first, 1, 0), new(second, 0, 1)]);

        // 10,000 cents split three ways is 3,333 each with 1 cent left over. All remainders are equal,
        // so the lowest event ID gets the extra cent, and the shares still add up to the full charge.
        Assert.Null(allocation.UnpricedReason);
        Assert.Equal(3, allocation.TotalTokens);
        Assert.Equal(10_000, allocation.ChargeMinorUnits);
        Assert.Equal(AzureProvisionedThroughputAllocator.RoundingMethod, allocation.RoundingMethod);
        Assert.Equal([first, second, third], allocation.Shares.Select(share => share.EventId));
        Assert.Equal([33.34m, 33.33m, 33.33m], allocation.Shares.Select(share => share.Amount));
        Assert.Equal([1L, 0L, 0L], allocation.Shares.Select(share => share.RemainderMinorUnits));
        Assert.Equal(100.00m, allocation.Shares.Sum(share => share.Amount));
    }

    [Fact]
    public void ProvisionedThroughputLargestRemainderWinsBeforeEventOrder()
    {
        var small = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var large = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var allocation = AzureProvisionedThroughputAllocator.Allocate(Charge(0.10m),
            [new(small, 1, 0), new(large, 2, 0)]);

        // Exact shares are 3.33 and 6.67 cents. The larger remainder (0.67) gets the leftover cent,
        // even though the other event has the lower ID.
        Assert.Equal([0.03m, 0.07m], allocation.Shares.Select(share => share.Amount));
        Assert.Equal(0.10m, allocation.Shares.Sum(share => share.Amount));
    }

    [Fact]
    public void ProvisionedThroughputStaysUnpricedWithoutCompleteTokenUsage()
    {
        var missing = AzureProvisionedThroughputAllocator.Allocate(Charge(),
            [new(Guid.NewGuid(), 10, null)]);
        Assert.Equal("token-measurements-missing", missing.UnpricedReason);
        Assert.Empty(missing.Shares);

        var idle = AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(Guid.NewGuid(), 0, 0)]);
        Assert.Equal("provisioned-throughput-usage-missing", idle.UnpricedReason);
        Assert.Empty(AzureProvisionedThroughputAllocator.Allocate(Charge(), []).Shares);
    }

    [Fact]
    public void ProvisionedThroughputRejectsInexactChargesAndMalformedUsage()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(1.005m), [new(Guid.NewGuid(), 1, 1)]));
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(digits: 7), [new(Guid.NewGuid(), 1, 1)]));
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge() with { Unit = "usd" }, [new(Guid.NewGuid(), 1, 1)]));
        var duplicate = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(duplicate, 1, 1), new(duplicate, 2, 2)]));
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(Guid.NewGuid(), -1, 1)]));
    }

    [Fact]
    public void SpendEvidenceRequestsRequireCanonicalUniqueDispatchesAndOrderedPeriods()
    {
        var dispatch = Guid.NewGuid().ToString("D");
        UsageSpendEvidenceContract.ValidateRequest(Request([dispatch]));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(Request([dispatch, dispatch])));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            Request([dispatch.ToUpperInvariant()])));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            Request([Guid.Empty.ToString("D")])));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            Request([dispatch]) with { ProjectPeriod = new("2026-10", Start, Start) }));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            Request([dispatch]) with { Unit = " " }));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            Request([.. Enumerable.Range(0, UsageSpendEvidenceContract.MaximumDispatches + 1)
                .Select(_ => Guid.NewGuid().ToString("D"))])));
    }

    [Fact]
    public void SpendEvidenceValidationRejectsSourceCompleteClaimsAndMismatchedWitnesses()
    {
        var dispatch = Guid.NewGuid().ToString("D");
        var request = Request([dispatch]);
        var scope = new UsageDispatchAccountingScope(
            "tenant-1", "project-1", "run-1", SdkMeterSources.ByokTokens, "USD", "2026-10");
        ImmutableArray<UsageDispatchSourceReceiptIdentity> receipts =
            [new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64))];
        var witness = new UsageDispatchAccountingWitness(
            dispatch, scope, 4, receipts,
            UsageDispatchAccountingWitnessContract.ComputeReceiptDigest(dispatch, scope, receipts),
            1.25m, 1, 0, UsageDispatchAccountingStatus.Partial, null, null, null);
        var evidence = new UsageSpendEvidence(
            UsageSpendEvidenceContract.CurrentVersion, "tenant-1", "project-1", "run-1",
            SdkMeterSources.ByokTokens, "USD", 4, new(1.25m, 1, 0), new(1.25m, 1, 0), "2026-10", [witness]);

        UsageSpendEvidenceContract.ValidateEvidence(evidence, request, "tenant-1", "project-1", "run-1");
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { Status = UsageDispatchAccountingStatus.SourceComplete }] },
            request, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { AccountingWatermark = 3 }] },
            request, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { ReceiptDigest = new string('b', 64) }] },
            request, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence, request, "tenant-1", "project-1", "run-2"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { ProjectPeriodId = "2026-11" }, request, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Run = new(-1m, 1, 0) }, request, "tenant-1", "project-1", "run-1"));
    }

    [Fact]
    public void LedgerDispatchCorrelationMatchesTheSerializedUsagePayloadKey()
    {
        var json = JsonSerializer.SerializeToElement(
            new { A2AMessageId = Guid.NewGuid() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // The SQL reads the dispatch ID by its JSON key, so it must match the key the serializer writes.
        Assert.True(json.TryGetProperty("a2AMessageId", out _));
        Assert.Contains("'a2AMessageId'", PostgresUsageLedger.DispatchIdSql);
    }

    [Fact]
    public void ChangedAzureRateCardFailsVerificationOfAnEarlierRunPin()
    {
        var (original, originalBinder) = Binder(1.25m);
        var pinned = originalBinder.ResolveAndPin(AzureCostProvider.MeterSource, "run-1");
        Assert.True(pinned.IsSuccess, pinned.Error?.Message);
        Assert.True(originalBinder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", pinned.Value!).IsSuccess);
        Assert.Equal(original.Options.RateCard, pinned.Value!.RateCard);

        // A new rate card with a different rate must not validate the pin the run already holds.
        var (_, changedBinder) = Binder(9.00m);
        Assert.False(changedBinder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", pinned.Value!).IsSuccess);
    }

    private static (AzureCostProvider Provider, AzureCostProviderBinder Binder) Binder(decimal rate)
    {
        var provider = new AzureCostProvider(new AzureCostProviderOptions(
            "azure-openai-deployment-1", 1, "azure-cost-options-v1",
            AzureCostProviderOptions.CurrentOptionsSchemaVersion, AzureCostPricingModel.StandardTokens,
            new CostRateCard("azure-retail-prices.standard.eastus2", $"2026-10-01.{rate}",
                AzureCostProvider.MeterSource, "USD", AzureCostProviderOptions.TokensPerRateUnit,
                ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
                {
                    KeyValuePair.Create(AzureCostProviderOptions.InputRateKey("gpt-6.1-sol"), rate),
                    KeyValuePair.Create(AzureCostProviderOptions.OutputRateKey("gpt-6.1-sol"), rate)
                }))));
        var catalog = Agentweaver.Providers.ProviderCatalog.Create(
            [provider.CreateRegistration()], [], [],
            meterSourceSelections: [new(SdkMeterSources.ByokTokens, AzureCostProvider.ProviderId)]);
        Assert.True(catalog.IsSuccess, catalog.Error?.Message);
        return (provider, new AzureCostProviderBinder(provider, new(catalog.Value!)));
    }

    private static UsageSpendEvidenceRequest Request(ImmutableArray<string> dispatches) =>
        new(UsageSpendEvidenceContract.CurrentVersion, SdkMeterSources.ByokTokens, "USD",
            new("2026-10", Start, Start.AddMonths(1)), dispatches);
}
