using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class AzureSpendAccountingTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    // A monthly provisioned throughput charge in USD with 2 decimal places (cents).
    private static AzureProvisionedThroughputCharge Charge(decimal amount = 100.00m, int digits = 2) =>
        new("azure-retail-prices.ptu.eastus2", "2026-10-01", "USD", amount, digits, Start, Start.AddMonths(1));

    [Fact]
    public void ProvisionedThroughputSharesAddUpExactlyAndBreakTiesByEventId()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var third = Guid.Parse("00000000-0000-0000-0000-000000000003");

        // Three events with 1 token each (input or output), passed out of ID order.
        var allocation = AzureProvisionedThroughputAllocator.Allocate(Charge(),
            [new(third, 1, 0), new(first, 1, 0), new(second, 0, 1)]);

        // 10,000 cents split three ways is 3,333 each with 1 cent left over. All remainders are equal,
        // so the lowest event ID gets the extra cent, and the shares still add up to the full charge.
        Assert.Null(allocation.UnpricedReason);
        Assert.Equal(3, allocation.TotalTokens);
        Assert.Equal(10_000, allocation.ChargeMinorUnits);
        Assert.Equal(AzureProvisionedThroughputAllocator.RoundingMethod, allocation.RoundingMethod);
        // Shares are returned in event ID order.
        Assert.Equal([first, second, third], allocation.Shares.Select(share => share.EventId));
        // Each share is 3,333 cents (9,999 total). All remainders are equal, so the 1 leftover cent
        // goes to the lowest event ID.
        Assert.Equal([33.34m, 33.33m, 33.33m], allocation.Shares.Select(share => share.Amount));
        Assert.Equal([1L, 0L, 0L], allocation.Shares.Select(share => share.RemainderMinorUnits));
        // The shares add up to the exact charge.
        Assert.Equal(100.00m, allocation.Shares.Sum(share => share.Amount));
    }

    [Fact]
    public void ProvisionedThroughputLargestRemainderWinsBeforeEventOrder()
    {
        var small = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var large = Guid.Parse("00000000-0000-0000-0000-000000000002");

        // 0.10 USD = 10 cents over 3 tokens: 1 token gets 3.33 cents and 2 tokens get 6.67 cents.
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
        // An event without an output token count can't be weighted, so nothing is priced.
        var missing = AzureProvisionedThroughputAllocator.Allocate(Charge(),
            [new(Guid.NewGuid(), 10, null)]);
        Assert.Equal("token-measurements-missing", missing.UnpricedReason);
        Assert.Empty(missing.Shares);

        // Zero tokens in total leaves nothing to split the charge over.
        var idle = AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(Guid.NewGuid(), 0, 0)]);
        Assert.Equal("provisioned-throughput-usage-missing", idle.UnpricedReason);
        // No events produce no shares.
        Assert.Empty(AzureProvisionedThroughputAllocator.Allocate(Charge(), []).Shares);
    }

    [Fact]
    public void ProvisionedThroughputRejectsInexactChargesAndMalformedUsage()
    {
        // 1.005 has more decimal places than the 2 the charge allows.
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(1.005m), [new(Guid.NewGuid(), 1, 1)]));
        // 7 decimal places is outside the supported range of 0 to 6.
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(digits: 7), [new(Guid.NewGuid(), 1, 1)]));
        // The currency code must be three uppercase letters.
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge() with { Unit = "usd" }, [new(Guid.NewGuid(), 1, 1)]));
        // The same event ID twice is rejected.
        var duplicate = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(duplicate, 1, 1), new(duplicate, 2, 2)]));
        // Negative token counts are rejected.
        Assert.Throws<ArgumentException>(() =>
            AzureProvisionedThroughputAllocator.Allocate(Charge(), [new(Guid.NewGuid(), -1, 1)]));
    }

    [Fact]
    public void SpendEvidenceRequestsRequireCanonicalUniqueDispatchesAndOrderedPeriods()
    {
        var dispatch = Guid.NewGuid().ToString("D");
        // Valid: one lowercase dispatch GUID.
        UsageSpendEvidenceContract.ValidateRequest(Request([dispatch]));
        // Rejected in order: a duplicate dispatch, an uppercase GUID, the empty GUID, a period that ends
        // where it starts, a blank unit, and more dispatches than the maximum.
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
        // A valid witness for the requested dispatch: one receipt, a matching digest, and status Partial,
        // at the same accounting cursor (4) as the evidence.
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
        // Events can't claim a dispatch is source complete; spend evidence doesn't prove that a source finished.
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { Status = UsageDispatchAccountingStatus.SourceComplete }] },
            request, "tenant-1", "project-1", "run-1"));
        // The witness watermark (3) must match the evidence cursor (4).
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { AccountingWatermark = 3 }] },
            request, "tenant-1", "project-1", "run-1"));
        // The receipt digest must match the listed receipts.
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Dispatches = [witness with { ReceiptDigest = new string('b', 64) }] },
            request, "tenant-1", "project-1", "run-1"));
        // The evidence must match the caller's run and the requested period, and totals can't be negative.
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence, request, "tenant-1", "project-1", "run-2"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { ProjectPeriodId = "2026-11" }, request, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Run = new(-1m, 1, 0) }, request, "tenant-1", "project-1", "run-1"));

        var agentRequest = request with { AgentId = "agent-1" };
        // An agent request needs an agent total in the response, and a run request must not get one.
        UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Agent = new(1.25m, 1, 0) }, agentRequest, "tenant-1", "project-1", "run-1");
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence, agentRequest, "tenant-1", "project-1", "run-1"));
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateEvidence(
            evidence with { Agent = new(1.25m, 1, 0) }, request, "tenant-1", "project-1", "run-1"));
        // A blank agent ID is not a valid selector.
        Assert.Throws<ArgumentException>(() => UsageSpendEvidenceContract.ValidateRequest(
            request with { AgentId = " " }));
    }

    [Fact]
    public void LedgerDispatchCorrelationMatchesTheSerializedUsagePayloadKey()
    {
        // Web JSON serialization writes A2AMessageId as "a2AMessageId".
        var json = JsonSerializer.SerializeToElement(
            new { A2AMessageId = Guid.NewGuid() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // The SQL reads the dispatch ID by its JSON key, so it must match the key the serializer writes.
        Assert.True(json.TryGetProperty("a2AMessageId", out _));
        // The ledger SQL that finds a usage event's dispatch reads that same key.
        Assert.Contains("'a2AMessageId'", PostgresUsageLedger.DispatchIdSql);
    }

    [Fact]
    public void ChangedAzureRateCardFailsVerificationOfAnEarlierRunPin()
    {
        // Pin run-1 to the rate card with rate 1.25; the same binder verifies the pin.
        var (original, originalBinder) = Binder(1.25m);
        var pinned = originalBinder.ResolveAndPin(AzureCostProvider.MeterSource, "run-1");
        Assert.True(pinned.IsSuccess, pinned.Error?.Message);
        Assert.True(originalBinder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", pinned.Value!).IsSuccess);
        Assert.Equal(original.Options.RateCard, pinned.Value!.RateCard);

        // A new rate card with a different rate must not validate the pin the run already holds.
        var (_, changedBinder) = Binder(9.00m);
        Assert.False(changedBinder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", pinned.Value!).IsSuccess);
    }

    // Builds an Azure cost provider whose rate card uses one rate for input and output tokens,
    // and a binder that selects it for the BYOK token meter.
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

    // A BYOK USD evidence request for the October period and the given dispatch IDs.
    private static UsageSpendEvidenceRequest Request(ImmutableArray<string> dispatches) =>
        new(UsageSpendEvidenceContract.CurrentVersion, SdkMeterSources.ByokTokens, "USD",
            new("2026-10", Start, Start.AddMonths(1)), dispatches);
}
