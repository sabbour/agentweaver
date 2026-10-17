using System.Collections.Immutable;
using System.Diagnostics;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions.Cost;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class CopilotCostTests
{
    private static CopilotCostProviderOptions Options(
        long generation = 7,
        string revision = "copilot-options-v1") =>
        new(
            "copilot-meter-resource",
            generation,
            revision,
            CopilotCostProvider.OptionsSchemaVersion);

    private static (CopilotCostProvider Provider, CopilotCostProviderBinder Binder, CostBinding Binding)
        Create(
            string revision = "copilot-options-v1",
            long generation = 7,
            ILogger<CopilotCostProviderBinder>? logger = null)
    {
        var provider = new CopilotCostProvider(Options(generation, revision));
        var catalogResult = ProviderCatalog.Create(
            [provider.CreateRegistration()],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)
            ]);
        Assert.True(catalogResult.IsSuccess, catalogResult.Error?.Message);
        var binder = new CopilotCostProviderBinder(
            provider, new ProviderResolver(catalogResult.Value!), logger ?? new RecordingLogger<CopilotCostProviderBinder>());
        var result = binder.ResolveAndPin(CopilotCostProvider.MeterSource, "run-1");
        Assert.True(result.IsSuccess, result.Error?.Message);
        return (provider, binder, result.Value!);
    }

    private static UsageModelBinding Model(
        string id = "gpt-6.1-sol", string source = CopilotCostProvider.MeterSource) =>
        new("model-reference", id, source, "model-selection-r7");

    [Fact]
    public void UsesTheProviderPublishedRateCardRatherThanConfiguredMultipliers()
    {
        var values = ConfigurationValues();
        var section = new ConfigurationBuilder().AddInMemoryCollection(values)
            .Build().GetSection("Cost");
        var options = CopilotCostProviderOptions.FromConfiguration(section);
        Assert.NotNull(options);
        Assert.Equal(Options(), options);
        Assert.Same(CopilotPublishedRateCard.Current, options.RateCard);
        Assert.Equal("github-copilot-published", options.RateCard.Id);
        Assert.Equal("2026-10-17", options.RateCard.Version);
        Assert.Empty(options.RateCard.ModelMultipliers);
        Assert.Equal(CopilotPublishedRateCard.SourceUrl, options.RateCard.ProviderSourceUrl);
        Assert.Equal("2026-10-17", options.RateCard.RetrievedOn!.Value.ToString("yyyy-MM-dd"));
        Assert.Equal(2.00m, options.RateCard.ModelTokenRates["gpt-6.1-sol"].DefaultContext!.Input);
        Assert.Equal(0.02m, options.RateCard.ModelTokenRates["gpt-6-luna"].LongContext!.CachedInput);
        Assert.Null(CopilotCostProviderOptions.FromConfiguration(
            new ConfigurationBuilder().Build().GetSection("Cost")));
    }

    [Theory]
    [InlineData("Cost:ResourceGeneration", null)]
    [InlineData("Cost:OptionsSchemaVersion", "1")]
    [InlineData("Cost:RateCard:ModelMultipliers:unknown-model", "4")]
    public void RejectsInvalidProviderOptionsAndLegacyConfiguredMultipliers(string key, string? value)
    {
        var values = ConfigurationValues();
        values[key] = value;
        var section = new ConfigurationBuilder().AddInMemoryCollection(values)
            .Build().GetSection("Cost");
        Assert.Throws<ArgumentException>(() => CopilotCostProviderOptions.FromConfiguration(section));
    }

    [Fact]
    public void RejectsTokenRateProvenanceWithoutPublishedModelRates()
    {
        var incompleteCard = CopilotPublishedRateCard.Current with
        {
            ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                .WithComparers(StringComparer.Ordinal)
        };

        Assert.Throws<ArgumentException>(() => CostRateCardContract.Validate(incompleteCard));
    }

    private static Dictionary<string, string?> ConfigurationValues() => new()
    {
        ["Cost:ResourceId"] = "copilot-meter-resource",
        ["Cost:ResourceGeneration"] = "7",
        ["Cost:OptionsRevision"] = "copilot-options-v1",
        ["Cost:OptionsSchemaVersion"] = CopilotCostProvider.OptionsSchemaVersion.ToString()
    };

    [Fact]
    public void PricesPublishedTokenCategoriesAndContextTierInAiCredits()
    {
        var (provider, _, binding) = Create();
        var usage = new UsageMeasurement(100, 50, 10, 0, 1, null, null, 17.5m)
        {
            CacheWriteTokens = 20
        };

        var price = provider.Price(usage, Model(), binding);

        Assert.Equal(0.0751m, price.Amount);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, price.Unit);
        Assert.Equal(CostDisposition.Estimate, price.Disposition);
        Assert.Null(price.UnpricedReason);

        var longContext = provider.Price(
            usage with { InputTokens = 272_001, OutputTokens = 1, CachedTokens = 0, CacheWriteTokens = 0 },
            Model(), binding);
        Assert.Equal(108.8019m, longContext.Amount);

        var luna = provider.Price(usage, Model("gpt-6-luna"), binding);
        Assert.Equal(0.00376m, luna.Amount);
    }

    [Fact]
    public void QuotesProviderWeightedUnitsAndPublishedTokenCategoriesWithoutMultipliers()
    {
        var (provider, _, binding) = Create();
        var weighted = provider.Quote(
            new CostQuoteRequest("gpt-6.1-sol", 3_000_000_000m, CostQuoteBasis.ProviderWeightedNanoAiu), binding);
        var tokenUsage = new UsageMeasurement(100, 50, 10, 0, 1, null, null, null)
        {
            CacheWriteTokens = 20
        };
        var tokenQuote = provider.Quote(
            new CostQuoteRequest("gpt-6.1-sol", null, CostQuoteBasis.ProviderPublishedTokenRates)
            {
                TokenMeasurement = tokenUsage
            }, binding);

        Assert.Equal(3m, weighted.Amount);
        Assert.Equal(0.0751m, tokenQuote.Amount);
        Assert.Equal(CostDisposition.Estimate, weighted.Disposition);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, tokenQuote.Unit);
        AssertUnpriced(provider.Quote(
            new CostQuoteRequest("gpt-6.1-sol", null, CostQuoteBasis.ProviderPublishedTokenRates), binding),
            "token-measurement-missing");
    }

    [Fact]
    public void PreservesUnpricedUsageForMissingTokenInputsAndUnknownModels()
    {
        var (provider, _, binding) = Create();
        var complete = new UsageMeasurement(1, 2, 0, null, 1, 1m, CopilotCostProvider.NanoAiuUnit, null)
        {
            CacheWriteTokens = 0
        };
        var missingTokens = complete with { CachedTokens = null };

        AssertUnpriced(provider.Price(missingTokens, Model(), binding), "token-measurement-incomplete");
        AssertUnpriced(provider.Price(complete, Model("unknown"), binding), "model-rate-unavailable");
        AssertUnpriced(provider.Price(complete, Model(source: "other"), binding),
            "meter-source-mismatch");
        AssertUnpriced(provider.Quote(
            new CostQuoteRequest("unknown", 0m, CostQuoteBasis.ProviderWeightedNanoAiu), binding),
            "model-rate-unavailable");
        AssertUnpriced(provider.Quote(
            new CostQuoteRequest("gpt-6.1-sol", 2m, CostQuoteBasis.UnweightedAiCredits), binding),
            "quote-basis-unsupported");
    }

    [Fact]
    public void RejectsNegativeMeasurementsAndPlannedUnits()
    {
        var (provider, _, binding) = Create();
        var normal = new UsageMeasurement(0, 0, 0, 0, 0, 0m, CopilotCostProvider.NanoAiuUnit, 0m)
        {
            CacheWriteTokens = 0
        };
        var negativeMeasurements = new[]
        {
            normal with { InputTokens = -1 },
            normal with { OutputTokens = -1 },
            normal with { CachedTokens = -1 },
            normal with { ReasoningTokens = -1 },
            normal with { RequestCount = -1 },
            normal with { ProviderUnits = -1m },
            normal with { DurationMilliseconds = -1m }
        };

        foreach (var measurement in negativeMeasurements)
            Assert.Throws<ArgumentOutOfRangeException>(() => provider.Price(measurement, Model(), binding));
        Assert.Throws<ArgumentOutOfRangeException>(() => provider.Quote(
            new CostQuoteRequest("gpt-4o", -1m, CostQuoteBasis.ProviderWeightedNanoAiu), binding));
    }

    [Fact]
    public void ValidatesFixedNanoAiuConversionAndRejectsTamperedRateCards()
    {
        var (provider, _, binding) = Create();
        var tampered = binding with
        {
            RateCard = binding.RateCard with
            {
                ModelTokenRates = binding.RateCard.ModelTokenRates.SetItem(
                    "gpt-6.1-sol",
                    binding.RateCard.ModelTokenRates["gpt-6.1-sol"] with
                    {
                        DefaultContext = binding.RateCard.ModelTokenRates["gpt-6.1-sol"].DefaultContext! with
                        {
                            Input = 99m
                        }
                    })
            }
        };

        AssertUnpriced(
            provider.Price(
                new UsageMeasurement(1, 1, 0, null, 1, 1_000_000_000m,
                    CopilotCostProvider.NanoAiuUnit, null) { CacheWriteTokens = 0 },
                Model(),
                tampered),
            "binding-mismatch");
    }

    [Fact]
    public void PinsRealProviderNegotiationAndVerifiesExactOptionsResourceAndRateCard()
    {
        var (provider, binder, binding) = Create();
        var verified = binder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", binding);
        Assert.True(verified.IsSuccess, verified.Error?.Message);
        Assert.Equal(binding, verified.Value);

        var tampered = binding with
        {
            RateCard = binding.RateCard with
            {
                ModelTokenRates = binding.RateCard.ModelTokenRates.SetItem(
                    "gpt-6.1-sol",
                    binding.RateCard.ModelTokenRates["gpt-6.1-sol"] with
                    {
                        DefaultContext = binding.RateCard.ModelTokenRates["gpt-6.1-sol"].DefaultContext! with
                        {
                            Output = 99m
                        }
                    })
            }
        };
        var rejected = binder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", tampered);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch, rejected.Error!.Code);
        var noSourceCatalog = ProviderCatalog.Create([provider.CreateRegistration()], [], []);
        var noSourceBinder = new CopilotCostProviderBinder(
            provider, new ProviderResolver(noSourceCatalog.Value!), new RecordingLogger<CopilotCostProviderBinder>());
        Assert.Equal(ProviderErrorCode.ProviderNotFound,
            noSourceBinder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", binding).Error!.Code);

        var changedOptions = new CopilotCostProvider(Options(generation: 8));
        var changedCatalog = ProviderCatalog.Create(
            [changedOptions.CreateRegistration()],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)
            ]);
        var changedBinder = new CopilotCostProviderBinder(
            changedOptions, new ProviderResolver(changedCatalog.Value!), new RecordingLogger<CopilotCostProviderBinder>());
        var changedResource = changedBinder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", binding);
        Assert.False(changedResource.IsSuccess);
        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch, changedResource.Error!.Code);

        var changedRevisionRegistration = provider.CreateRegistration() with
        {
            OptionsRevision = "copilot-options-v2"
        };
        var changedRevisionCatalog = ProviderCatalog.Create(
            [changedRevisionRegistration],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)
            ]);
        var changedRevisionBinder = new CopilotCostProviderBinder(
            provider, new ProviderResolver(changedRevisionCatalog.Value!), new RecordingLogger<CopilotCostProviderBinder>());
        Assert.Equal(ProviderErrorCode.PinnedBindingMismatch,
            changedRevisionBinder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", binding).Error!.Code);

        var missingCapabilityRegistration = provider.CreateRegistration() with
        {
            Descriptor = provider.Descriptor with
            {
                AdvertisedCapabilities = ImmutableHashSet.Create(
                    StringComparer.Ordinal, CopilotCostCapabilities.PriceUsage)
            }
        };
        var missingCapabilityCatalog = ProviderCatalog.Create(
            [missingCapabilityRegistration],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)
            ]);
        var missingCapabilityBinder = new CopilotCostProviderBinder(
            provider, new ProviderResolver(missingCapabilityCatalog.Value!), new RecordingLogger<CopilotCostProviderBinder>());
        Assert.Equal(ProviderErrorCode.CapabilityUnavailable,
            missingCapabilityBinder.VerifyPinned(CopilotCostProvider.MeterSource, "run-1", binding).Error!.Code);
    }

    [Fact]
    public void BindingTraceHashesResourceAndContainsOnlySafeProviderDiagnostics()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var (_, _, binding) = Create();

        var activity = Assert.Single(activities);
        Assert.Equal("cost.provider.binding.pinned", activity.OperationName);
        Assert.Equal(CopilotCostProvider.ProviderId, activity.GetTagItem("cost.provider.id"));
        Assert.Equal("1.0.0", activity.GetTagItem("cost.provider.adapter_version"));
        Assert.Equal(CopilotCostProvider.OptionsSchemaVersion,
            activity.GetTagItem("cost.provider.options_schema_version"));
        Assert.Equal(binding.OptionsRevision, activity.GetTagItem("cost.provider.options_revision"));
        var resourceHash = Assert.IsType<string>(activity.GetTagItem("cost.provider.resource.id_hash"));
        Assert.Equal(24, resourceHash.Length);
        Assert.NotEqual(binding.ResourceId, resourceHash);
        Assert.DoesNotContain(activity.Tags, tag =>
            tag.Value?.ToString()?.Contains(binding.ResourceId, StringComparison.Ordinal) == true ||
            tag.Key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DiagnosticObserverFailureIsLoggedWithOnlyBoundedSafeMetadata()
    {
        var logger = new RecordingLogger<CopilotCostProviderBinder>();
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySignals.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                activities.Add(activity);
                throw new InvalidOperationException("sensitive-resource-token");
            }
        };
        ActivitySource.AddActivityListener(listener);

        var provider = new CopilotCostProvider(Options());
        var catalog = ProviderCatalog.Create(
            [provider.CreateRegistration()],
            [],
            [],
            meterSourceSelections:
            [
                new ProviderMeterSourceSelection(CopilotCostProvider.MeterSource, CopilotCostProvider.ProviderId)
            ]);
        var binder = new CopilotCostProviderBinder(
            provider, new ProviderResolver(catalog.Value!), logger);

        var result = binder.ResolveAndPin(CopilotCostProvider.MeterSource, "run-1");

        Assert.True(result.IsSuccess, result.Error?.Message);
        var failed = binder.ResolveAndPin("other.source", "run-1");
        Assert.False(failed.IsSuccess);
        Assert.Equal(ProviderErrorCode.MeterSourceMismatch, failed.Error!.Code);
        Assert.Collection(
            logger.Entries,
            warning => AssertSafeWarning(warning, "ok", provider.Options.ResourceId),
            warning => AssertSafeWarning(
                warning, nameof(ProviderErrorCode.MeterSourceMismatch), provider.Options.ResourceId));
    }

    private static void AssertUnpriced(CostPrice price, string reason)
    {
        Assert.Null(price.Amount);
        Assert.Equal(CostDisposition.Unpriced, price.Disposition);
        Assert.Equal(reason, price.UnpricedReason);
        Assert.Equal(CopilotCostProvider.AiCreditUnit, price.Unit);
        Assert.NotNull(price.RateCard);
    }

    private static void AssertSafeWarning(
        (LogLevel Level, string Message, Exception? Exception) warning,
        string resultCode,
        string resourceId)
    {
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(resultCode, warning.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-resource-token", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(resourceId, warning.Message, StringComparison.Ordinal);
        Assert.Null(warning.Exception);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
