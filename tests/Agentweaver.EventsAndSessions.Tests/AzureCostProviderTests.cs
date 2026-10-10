using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Providers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class AzureCostProviderTests
{
    private static AzureCostProviderOptions Options(
        AzureCostPricingModel pricingModel = AzureCostPricingModel.StandardTokens,
        decimal inputRate = 1.25m,
        decimal outputRate = 5.75m)
    {
        var rates = pricingModel == AzureCostPricingModel.StandardTokens
            ? ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, decimal>("gpt-4.1|input", inputRate),
                new KeyValuePair<string, decimal>("gpt-4.1|output", outputRate)
            })
            : ImmutableDictionary<string, decimal>.Empty.WithComparers(StringComparer.Ordinal);
        return new AzureCostProviderOptions(
            "azure-openai-deployment-1",
            3,
            "azure-cost-options-v1",
            AzureCostProviderOptions.CurrentOptionsSchemaVersion,
            pricingModel,
            new CostRateCard(
                pricingModel == AzureCostPricingModel.StandardTokens
                    ? "azure-retail-prices.standard.eastus2"
                    : "azure-retail-prices.ptu.eastus2",
                "2026-10-01",
                AzureCostProvider.MeterSource,
                "USD",
                AzureCostProviderOptions.TokensPerRateUnit,
                rates));
    }

    private static (AzureCostProvider Provider, CostBinding Binding) Create(
        AzureCostPricingModel pricingModel = AzureCostPricingModel.StandardTokens,
        decimal inputRate = 1.25m,
        decimal outputRate = 5.75m)
    {
        var provider = new AzureCostProvider(Options(pricingModel, inputRate, outputRate));
        return (provider, new CostBinding(
            AzureCostProvider.MeterSource,
            AzureCostProvider.ProviderId,
            AzureCostProvider.AdapterVersion.ToString(),
            AzureCostProvider.OptionsSchemaVersion,
            provider.Options.OptionsRevision,
            provider.Options.ResourceId,
            provider.Options.ResourceGeneration,
            AzureCostCapabilities.All,
            provider.Options.RateCard));
    }

    private static UsageModelBinding Model(
        string modelId = "gpt-4.1",
        string meterSource = AzureCostProvider.MeterSource) =>
        new("azure-model-selection", modelId, meterSource, "selection-v1");

    private static UsageMeasurement Measurement(
        long? inputTokens = 1_234_567,
        long? outputTokens = 234_567,
        long? cachedTokens = 0,
        long? cacheWriteTokens = 0) =>
        new UsageMeasurement(inputTokens, outputTokens, cachedTokens, null, null, null, null, null)
        {
            CacheWriteTokens = cacheWriteTokens
        };

    [Fact]
    public void PricesVersionedModelScopedTokenRatesExactlyWithoutCurrencyRounding()
    {
        var (provider, binding) = Create();

        var price = provider.Price(Measurement(), Model(), binding);

        Assert.Equal(2.891969m, price.Amount);
        Assert.Equal("USD", price.Unit);
        Assert.Equal(CostDisposition.Estimate, price.Disposition);
        Assert.Null(price.UnpricedReason);
        Assert.Equal("azure-retail-prices.standard.eastus2", price.RateCard!.Id);
        Assert.Equal("2026-10-01", price.RateCard.Version);
        Assert.Equal(AzureCostProvider.MeterSource, price.RateCard.MeterSource);
    }

    [Fact]
    public void PreservesUnpricedForMissingMeasurementsAndUnsupportedTokenCategories()
    {
        var (provider, binding) = Create();

        AssertUnpriced(provider.Price(Measurement(inputTokens: null), Model(), binding),
            "token-measurements-missing");
        AssertUnpriced(provider.Price(Measurement(outputTokens: null), Model(), binding),
            "token-measurements-missing");
        AssertUnpriced(provider.Price(Measurement(cachedTokens: null), Model(), binding),
            "token-cache-measurements-missing");
        AssertUnpriced(provider.Price(Measurement(cacheWriteTokens: null), Model(), binding),
            "token-cache-measurements-missing");
        AssertUnpriced(provider.Price(Measurement(cachedTokens: 1), Model(), binding),
            "token-cache-pricing-unavailable");
        AssertUnpriced(provider.Price(Measurement(cacheWriteTokens: 1), Model(), binding),
            "token-cache-pricing-unavailable");
    }

    [Fact]
    public void PreservesUnpricedForUnknownModelSourceOrProviderUnits()
    {
        var (provider, binding) = Create();

        AssertUnpriced(provider.Price(Measurement(), Model("unknown"), binding),
            "model-rate-unavailable");
        AssertUnpriced(provider.Price(Measurement(), Model(meterSource: "other"), binding),
            "meter-source-mismatch");
        AssertUnpriced(provider.Price(
            Measurement() with { ProviderUnits = 1m, ProviderUnit = "nano_aiu" }, Model(), binding),
            "provider-units-unsupported");
    }

    [Fact]
    public void RejectsChangedBindingAndLeavesProvisionedThroughputUnpricedWithoutShareEvidence()
    {
        var (provider, binding) = Create();
        var changed = binding with { RateCard = binding.RateCard with { Version = "2026-10-02" } };
        AssertUnpriced(provider.Price(Measurement(), Model(), changed), "binding-mismatch");

        var (ptuProvider, ptuBinding) = Create(AzureCostPricingModel.ProvisionedThroughput);
        AssertUnpriced(
            ptuProvider.Price(Measurement(), Model(), ptuBinding),
            "provisioned-throughput-share-unavailable");
    }

    [Fact]
    public void DoesNotInventAByokTokenQuoteOrCopilotAiCredits()
    {
        var (provider, binding) = Create();

        var quote = provider.Quote(
            new CostQuoteRequest("gpt-4.1", 100m, CostQuoteBasis.UnweightedAiCredits), binding);

        AssertUnpriced(quote, "byok-token-quotes-unsupported");
        Assert.Equal("USD", quote.Unit);
    }

    [Fact]
    public void RequiresAnExplicitPricingModelInConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Cost:ResourceId"] = "azure-openai-deployment-1",
            ["Cost:ResourceGeneration"] = "3",
            ["Cost:OptionsRevision"] = "azure-cost-options-v1",
            ["Cost:OptionsSchemaVersion"] = "1",
            ["Cost:RateCard:Id"] = "azure-retail-prices.standard.eastus2",
            ["Cost:RateCard:Version"] = "2026-10-01",
            ["Cost:RateCard:MeterSource"] = AzureCostProvider.MeterSource,
            ["Cost:RateCard:Unit"] = "USD",
            ["Cost:RateCard:NanoUnitsPerUnit"] = "1000000",
            ["Cost:RateCard:ModelMultipliers:gpt-4.1|input"] = "1.25",
            ["Cost:RateCard:ModelMultipliers:gpt-4.1|output"] = "5.75"
        };
        var section = new ConfigurationBuilder().AddInMemoryCollection(values)
            .Build().GetSection("Cost");

        Assert.Throws<ArgumentException>(() => AzureCostProviderOptions.FromConfiguration(section));
    }

    [Fact]
    public void AzureBinderUsesTheExistingMeterResolverAndRejectsChangedRateCardPins()
    {
        var provider = new AzureCostProvider(Options());
        var catalog = ProviderCatalog.Create(
            [provider.CreateRegistration()],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(
                    AzureCostProvider.MeterSource, AzureCostProvider.ProviderId)
            ]);
        Assert.True(catalog.IsSuccess, catalog.Error?.Message);
        var binder = new AzureCostProviderBinder(provider, new ProviderResolver(catalog.Value!));

        var resolved = binder.ResolveAndPin(AzureCostProvider.MeterSource, "run-1");

        Assert.True(resolved.IsSuccess, resolved.Error?.Message);
        var verified = binder.VerifyPinned(
            AzureCostProvider.MeterSource, "run-1", resolved.Value!);
        Assert.True(verified.IsSuccess, verified.Error?.Message);
        Assert.Equal(resolved.Value, verified.Value);

        var changed = resolved.Value! with
        {
            RateCard = resolved.Value.RateCard with { Version = "2026-10-02" }
        };
        var rejected = binder.VerifyPinned(AzureCostProvider.MeterSource, "run-1", changed);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch, rejected.Error!.Code);
    }

    [Fact]
    public void RejectsInvalidPriceSourceCurrencyModelRatesAndArithmeticOverflow()
    {
        Assert.Throws<ArgumentException>(() => new AzureCostProvider(Options() with
        {
            RateCard = Options().RateCard with { MeterSource = "copilot.nano_aiu" }
        }));
        Assert.Throws<ArgumentException>(() => new AzureCostProvider(Options() with
        {
            RateCard = Options().RateCard with { Unit = "USD/M" }
        }));
        Assert.Throws<ArgumentException>(() => new AzureCostProvider(Options() with
        {
            RateCard = Options().RateCard with
            {
                ModelMultipliers = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
                {
                    new KeyValuePair<string, decimal>("gpt-4.1|input", 1m)
                })
            }
        }));
        Assert.Throws<ArgumentException>(() => new AzureCostProvider(Options(inputRate: 0m)));

        var (provider, binding) = Create(inputRate: decimal.MaxValue);
        Assert.Throws<OverflowException>(() => provider.Price(
            Measurement(inputTokens: long.MaxValue, outputTokens: 0),
            Model(),
            binding));
    }

    private static void AssertUnpriced(CostPrice price, string reason)
    {
        Assert.Null(price.Amount);
        Assert.Equal(CostDisposition.Unpriced, price.Disposition);
        Assert.Equal(reason, price.UnpricedReason);
    }
}
