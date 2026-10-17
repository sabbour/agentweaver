using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
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
    private const string TestRateCardJson = """
        {
          "schemaVersion": 1,
          "id": "github-copilot-published",
          "version": "2026-10-17",
          "meterSource": "copilot.nano_aiu",
          "unit": "AIC",
          "nanoUnitsPerUnit": 1000000000,
          "providerSourceUrl": "https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing",
          "snapshotVersion": "2026-10-17",
          "retrievedOn": "2026-10-17",
          "tokenRateCurrency": "USD",
          "tokensPerRateUnit": 1000000,
          "creditsPerCurrencyUnit": 100,
          "modelTokenRates": {
            "gpt-5-mini": {
              "defaultContext": { "input": 0.25, "cachedInput": 0.025, "cacheWrite": null, "output": 2 },
              "longContext": null,
              "longContextThresholdTokens": null
            },
            "gpt-5.6-luna": {
              "defaultContext": { "input": 0.2, "cachedInput": 0.02, "cacheWrite": 0.25, "output": 1.2 },
              "longContext": { "input": 0.4, "cachedInput": 0.04, "cacheWrite": 0.5, "output": 1.8 },
              "longContextThresholdTokens": 200000
            },
            "gpt-6-luna": {
              "defaultContext": { "input": 0.1, "cachedInput": 0.01, "cacheWrite": 0.125, "output": 0.5 },
              "longContext": { "input": 0.2, "cachedInput": 0.02, "cacheWrite": 0.25, "output": 0.75 },
              "longContextThresholdTokens": 272000
            },
            "gpt-6.1-sol": {
              "defaultContext": { "input": 2, "cachedInput": 0.1, "cacheWrite": 2.5, "output": 10 },
              "longContext": { "input": 4, "cachedInput": 0.2, "cacheWrite": 5, "output": 15 },
              "longContextThresholdTokens": 272000
            },
            "claude-haiku-5.5": {
              "defaultContext": { "input": 0.1, "cachedInput": 0.01, "cacheWrite": 0.125, "output": 0.5 },
              "longContext": { "input": 0.5, "cachedInput": 0.05, "cacheWrite": 0.625, "output": 2.5 },
              "longContextThresholdTokens": 100000
            }
          }
        }
        """;

    private static CopilotCostProviderOptions Options(
        long generation = 7,
        string revision = "copilot-options-v1") =>
        new(
            "copilot-meter-resource",
            generation,
            revision,
            CopilotCostProvider.OptionsSchemaVersion)
        {
            RateCard = CopilotRateCardConfiguration.Parse(TestRateCardJson)
        };

    internal static CopilotCostProviderOptions CreateTestOptions() => Options();

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
        Assert.Equal(Options().ResourceId, options.ResourceId);
        Assert.Equal(Options().ResourceGeneration, options.ResourceGeneration);
        Assert.Equal(Options().OptionsRevision, options.OptionsRevision);
        Assert.Equal(Options().OptionsSchemaVersion, options.OptionsSchemaVersion);
        Assert.True(CostRateCardContract.Equals(
            CopilotRateCardConfiguration.Parse(TestRateCardJson), options.RateCard));
        Assert.Equal("github-copilot-published", options.RateCard.Id);
        Assert.Equal("2026-10-17", options.RateCard.Version);
        Assert.Empty(options.RateCard.ModelMultipliers);
        Assert.Equal(CopilotPublishedRateCard.SourceUrl, options.RateCard.ProviderSourceUrl);
        Assert.Equal("2026-10-17", options.RateCard.RetrievedOn!.Value.ToString("yyyy-MM-dd"));
        Assert.Equal(2.00m, options.RateCard.ModelTokenRates["gpt-6.1-sol"].DefaultContext!.Input);
        Assert.Equal(0.02m, options.RateCard.ModelTokenRates["gpt-6-luna"].LongContext!.CachedInput);
        Assert.Equal(64, options.RateCard.ContentHash!.Length);
        Assert.Null(options.RateCard.ModelTokenRates["gpt-5-mini"].LongContext);
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
    public void ConfiguredCostProviderRequiresItsRuntimeRateCard()
    {
        var values = ConfigurationValues();
        values.Remove("Cost:RateCard:Json");
        var section = new ConfigurationBuilder().AddInMemoryCollection(values)
            .Build().GetSection("Cost");

        Assert.Throws<ArgumentException>(() => CopilotCostProviderOptions.FromConfiguration(section));
    }

    [Fact]
    public void RejectsTokenRateProvenanceWithoutPublishedModelRates()
    {
        var card = Options().RateCard;
        var incompleteCard = card with
        {
            ModelTokenRates = ImmutableDictionary<string, CostModelTokenRates>.Empty
                .WithComparers(StringComparer.Ordinal),
            ContentHash = null
        };

        Assert.Throws<ArgumentException>(() => CostRateCardContract.Validate(incompleteCard));
    }

    [Fact]
    public void RuntimeRateCardSchemaIsStrictAndHashesExactDocumentText()
    {
        var first = CopilotRateCardConfiguration.Parse(TestRateCardJson);
        var withWhitespace = CopilotRateCardConfiguration.Parse(TestRateCardJson + "\n");

        Assert.NotEqual(first.ContentHash, withWhitespace.ContentHash);
        Assert.Throws<ArgumentException>(() =>
            CopilotRateCardConfiguration.Parse(TestRateCardJson.Replace(
                "\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() =>
            CopilotRateCardConfiguration.Parse(TestRateCardJson.Replace(
                "\"unit\": \"AIC\",",
                "\"unit\": \"AIC\", \"unexpected\": true,",
                StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() =>
            CopilotRateCardConfiguration.Parse(TestRateCardJson.Replace(
                "\"unit\": \"AIC\",",
                "\"unit\": \"AIC\", \"unit\": \"AIC\",",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void LoadsAllThirtyThreeModelsFromTheDeploymentRuntimeConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? configPath = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "deploy", "k8s", "runtime-configs", "events-and-sessions-runtime-config.json");
            if (File.Exists(candidate))
            {
                configPath = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(configPath);

        using var config = JsonDocument.Parse(File.ReadAllText(configPath!));
        var json = config.RootElement.GetProperty("data")
            .GetProperty("EventsAndSessions__Cost__Copilot__RateCard__Json").GetString();
        var card = CopilotRateCardConfiguration.Parse(json!);
        var configuredOptions = CopilotCostProviderOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cost:ResourceId"] = "deployment-cost-resource",
                ["Cost:ResourceGeneration"] = "1",
                ["Cost:OptionsRevision"] = "deployment-card-v1",
                ["Cost:OptionsSchemaVersion"] = CopilotCostProvider.OptionsSchemaVersion.ToString(),
                ["Cost:RateCard:Json"] = json
            }).Build().GetSection("Cost"));

        Assert.Equal(33, card.ModelTokenRates.Count);
        Assert.Equal(card.ContentHash, configuredOptions!.RateCard.ContentHash);
        Assert.Equal(0.025m, card.ModelTokenRates["gpt-5-mini"].DefaultContext!.CachedInput);
        Assert.Equal(200_000L, card.ModelTokenRates["gpt-5.6-luna"].LongContextThresholdTokens);
        Assert.Equal(100_000L, card.ModelTokenRates["claude-haiku-5.5"].LongContextThresholdTokens);
        Assert.Equal(200_000L, card.ModelTokenRates["grok-4.7"].LongContextThresholdTokens);
        Assert.Null(card.ModelTokenRates["claude-sonnet-4"].LongContext);
    }

    [Fact]
    public void AccountingReceiptAndLedgerCanonicalInputBindTheExactCardHash()
    {
        var (_, _, binding) = Create();
        var price = new CostPrice(0.1m, "AIC", CostDisposition.Estimate, binding.RateCard, null);
        var usage = new UsageSubmission(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            new("tenant", "project", "run", "session", "agent"),
            Model(), new(10, 5, 0, 0, 1, null, null, null) { CacheWriteTokens = 0 });
        var entry = new UsageLedgerEntry(
            usage, binding, price, DateTimeOffset.UtcNow, new string('a', 64));

        Assert.Equal(binding.RateCard.ContentHash, new UsageIngestionResult(entry, false).Receipt.RateCardContentHash);
        var canonical = UsageLedgerCanonicalizer.Serialize(usage, binding, price);
        Assert.Contains(binding.RateCard.ContentHash!, canonical, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ConfigurationValues() => new()
    {
        ["Cost:ResourceId"] = "copilot-meter-resource",
        ["Cost:ResourceGeneration"] = "7",
        ["Cost:OptionsRevision"] = "copilot-options-v1",
        ["Cost:OptionsSchemaVersion"] = CopilotCostProvider.OptionsSchemaVersion.ToString(),
        ["Cost:RateCard:Json"] = TestRateCardJson
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
    public void UsesPerModelLongContextThresholdsAndKeepsDefaultOnlyModelsOnDefaultRates()
    {
        var (provider, _, binding) = Create();
        var noOtherTokens = new UsageMeasurement(200_000, 0, 0, 0, 1, null, null, null)
        {
            CacheWriteTokens = 0
        };

        var atLunaThreshold = provider.Price(noOtherTokens, Model("gpt-5.6-luna"), binding);
        var pastLunaThreshold = provider.Price(
            noOtherTokens with { InputTokens = 200_001 }, Model("gpt-5.6-luna"), binding);
        var belowSolThreshold = provider.Price(
            noOtherTokens with { InputTokens = 200_001 }, Model("gpt-6.1-sol"), binding);
        var defaultOnlyLongInput = provider.Price(
            noOtherTokens with { InputTokens = 272_001 }, Model("gpt-5-mini"), binding);

        Assert.Equal(4m, atLunaThreshold.Amount);
        Assert.Equal(8.00004m, pastLunaThreshold.Amount);
        Assert.Equal(40.0002m, belowSolThreshold.Amount);
        Assert.Equal(6.800025m, defaultOnlyLongInput.Amount);
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
        AssertUnpriced(provider.Price(
                complete with { CacheWriteTokens = 1 }, Model("gpt-5-mini"), binding),
            "token-rate-unavailable-cache-write");
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
