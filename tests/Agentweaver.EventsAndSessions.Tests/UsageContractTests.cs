using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.EventsAndSessions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class UsageContractTests
{
    [Fact]
    public void EnabledNativeConsumerRequiresAndResolvesTheSharedVersionedAgentHostMap()
    {
        var values = new Dictionary<string, string?>
        {
            ["EventsAndSessions:RuntimeUsage:Enabled"] = "true",
            ["AgentHost:ModelBindingsRevision"] = "model-bindings-v1",
            ["AgentHost:ModelBindings:accepted-reference:ModelId"] = "concrete-model",
            ["AgentHost:ModelBindings:accepted-reference:SourceMode"] = "HostedCopilot"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        Assert.True(services.AddNativeUsageConsumer(configuration));
        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<RuntimeModelBindingsResolver>();
        Assert.Equal("concrete-model", resolver.Pin("accepted-reference", ModelSourceMode.HostedCopilot).ModelId);
        values.Remove("AgentHost:ModelBindingsRevision");
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddNativeUsageConsumer(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    [Fact]
    public void OptionalNativeMetadataPreservesTheHistoricalCanonicalHash()
    {
        var legacy = Submission(eventId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var canonical = UsageLedgerCanonicalizer.Serialize(legacy, Binding(), Price());
        Assert.Equal("dc76e2895c672c0321542b8b1a1c9cce9f4a219f02458042f4ce789419b3b048",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        Assert.DoesNotContain("sdkAccounting", canonical);
        Assert.DoesNotContain("a2AMessageId", canonical);
        var accounting = new SdkUsageAccountingObservation(
            new("native-session", 4, "usage-4"), SdkAiCreditsStatus.Complete, true);
        var complete = legacy with { SdkAccounting = accounting };
        var completePayload = UsageLedgerCanonicalizer.Serialize(complete, Binding(), Price());
        Assert.NotEqual(canonical, completePayload);
        Assert.NotEqual(completePayload, UsageLedgerCanonicalizer.Serialize(
            complete with { SdkAccounting = accounting with { AiCreditsStatus = SdkAiCreditsStatus.Partial } },
            Binding(), Price()));
        Assert.NotEqual(completePayload, UsageLedgerCanonicalizer.Serialize(
            complete with { SdkAccounting = accounting with { Identity = accounting.Identity! with { Sequence = 5 } } },
            Binding(), Price()));
        var messageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var addressed = UsageLedgerCanonicalizer.Serialize(legacy with { A2AMessageId = messageId }, Binding(), Price());
        Assert.Contains($"\"a2AMessageId\":\"{messageId:D}\"", addressed);
        Assert.NotEqual(canonical, addressed);
    }

    [Fact]
    public void PricedHostedNanoAiuDoesNotRequireOptionalAccountingIdentityOrStatus()
    {
        var source = new SdkSessionFacts(Guid.NewGuid(), "native-session", "1.0.18", "1.0.79",
            "model/ref", "model-1", new string('a', 64), 1m, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, new string('b', 64), 1);
        var complete = Submission() with
        {
            SdkSource = source,
            ModelBinding = Submission().ModelBinding with { MeterSource = source.MeterSource },
            Measurement = Submission().Measurement with { ProviderUnit = "nano_aiu" },
            SdkAccounting = new(new(source.SdkSessionId, 4, "usage-4"), SdkAiCreditsStatus.Complete, true)
        };
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(complete, CostDisposition.Estimate));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(complete, CostDisposition.Unpriced));
        foreach (var accounting in new SdkUsageAccountingObservation?[]
        {
            null,
            complete.SdkAccounting! with { AiCreditsStatus = SdkAiCreditsStatus.Partial },
            complete.SdkAccounting! with { AiCreditsStatus = SdkAiCreditsStatus.Unavailable },
            complete.SdkAccounting! with { AiCreditsStatusReported = false },
            complete.SdkAccounting! with { Identity = null }
        })
            Assert.True(UsageLedgerValidation.IsFinanciallyComplete(
                complete with { SdkAccounting = accounting }, CostDisposition.Estimate));
        foreach (var accounting in new[]
        {
            complete.SdkAccounting! with { Identity = new("another-source", 4, "usage-4") },
            complete.SdkAccounting! with { Identity = new(source.SdkSessionId, 0, "usage-4") },
            complete.SdkAccounting! with { Identity = new(source.SdkSessionId, 4, " ") },
            complete.SdkAccounting! with { Identity = new(source.SdkSessionId, 4, "usage\t4") },
            complete.SdkAccounting! with { Identity = new(source.SdkSessionId, 4, new string('x', 513)) }
        })
            Assert.False(UsageLedgerValidation.IsFinanciallyComplete(
                complete with { SdkAccounting = accounting }, CostDisposition.Estimate));
        foreach (var altered in new[]
        {
            complete with { SdkSource = source with { SourceMode = "unknown" } },
            complete with { ModelBinding = complete.ModelBinding with { MeterSource = SdkMeterSources.ByokTokens } },
            complete with { Measurement = complete.Measurement with { ProviderUnits = null } },
            complete with { Measurement = complete.Measurement with { ProviderUnit = "tokens" } }
        })
            Assert.False(UsageLedgerValidation.IsFinanciallyComplete(altered, CostDisposition.Estimate));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(complete with
        {
            SdkAccounting = complete.SdkAccounting! with
            {
                Identity = new(source.SdkSessionId, 4, new string('x', 512))
            }
        }, CostDisposition.Estimate));
    }

    [Fact]
    public void ByokTokenPricingUsesTheRuntimeMeasurementShapeNotHostedCreditStatus()
    {
        var source = new SdkSessionFacts(Guid.NewGuid(), "native-session", "1.0.18", "1.0.79",
            "model/ref", "model-1", new string('a', 64), null, "byok",
            SdkMeterSources.ByokTokens, new string('b', 64), 1);
        var byok = Submission() with
        {
            SdkSource = source,
            ModelBinding = Submission().ModelBinding with { MeterSource = source.MeterSource },
            Measurement = Submission().Measurement with { ProviderUnits = null, ProviderUnit = "tokens", CacheWriteTokens = 4 },
            SdkAccounting = new(new(source.SdkSessionId, 4, "usage-4"), SdkAiCreditsStatus.Partial, true)
        };
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(byok, CostDisposition.Estimate));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(byok with { SdkAccounting = null }, CostDisposition.Estimate));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(byok, CostDisposition.Unpriced));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(
            byok with { Measurement = byok.Measurement with { ProviderUnit = null } }, CostDisposition.Estimate));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(
            byok with { Measurement = byok.Measurement with { InputTokens = null } }, CostDisposition.Estimate));
    }

    [Fact]
    public void UnknownSourceAndMissingSdkFieldsCanBeRecordedOnlyAsUnpriced()
    {
        var usage = Submission(
            new UsageAttribution("tenant-1", "project-1", "run-1", "session-1", "agent-1"),
            new UsageModelBinding("unknown/model", "model-1", "new-meter", "selection-1"),
            new UsageMeasurement(null, null, null, null, 0, null, null, null));
        var price = new CostPrice(null, null, CostDisposition.Unpriced, null, "No pinned meter binding.");

        UsageLedgerValidation.Validate(usage, binding: null, price);
    }

    [Fact]
    public void NegativeMeasurementsAndMalformedIdentifiersAreRejected()
    {
        var usage = Submission() with
        {
            Measurement = new UsageMeasurement(-1, 0, 0, 0, 0, null, null, null),
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UsageLedgerValidation.Validate(usage, Binding(), Price()));

        usage = Submission() with
        {
            Attribution = new UsageAttribution("tenant 1", "project-1", "run-1", "session-1", "agent-1"),
        };
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(usage, Binding(), Price()));

        usage = Submission() with { EventId = Guid.Empty };
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(usage, Binding(), Price()));
    }

    [Fact]
    public void SourceAndPricingSnapshotsMustMatchThePinnedBinding()
    {
        var usage = Submission() with
        {
            ModelBinding = new UsageModelBinding("model/ref", "model-1", "different-meter", "selection-1"),
        };
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(usage, Binding(), Price()));

        var mismatch = Price() with { Unit = "EUR" };
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(Submission(), Binding(), mismatch));

        var unsupportedBinding = Binding() with { OptionsSchemaVersion = 0 };
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(Submission(), unsupportedBinding, Price()));
    }

    [Fact]
    public void IdentifiersCannotExceedTheirPersistedColumnWidths()
    {
        var tooLong256 = new string('x', 257);
        var tooLong128 = new string('x', 129);
        var tooLong64 = new string('x', 65);

        AssertInvalid(Submission() with
        {
            Attribution = Submission().Attribution with { TenantId = tooLong256 },
        }, Binding(), Price());
        AssertInvalid(Submission() with
        {
            Attribution = Submission().Attribution with { AgentId = tooLong256 },
        }, Binding(), Price());
        AssertInvalid(Submission() with
        {
            ModelBinding = Submission().ModelBinding with { MeterSource = tooLong256 },
        }, Binding(), Price());
        AssertInvalid(Submission() with
        {
            ModelBinding = Submission().ModelBinding with { SelectionRevision = tooLong256 },
        }, Binding(), Price());
        AssertInvalid(Submission(), Binding() with { ProviderId = tooLong256 }, Price());
        AssertInvalid(Submission(), Binding() with { AdapterVersion = tooLong64 }, Price());
        AssertInvalid(Submission(), Binding() with { OptionsRevision = tooLong128 }, Price());
        AssertInvalid(Submission(), Binding() with { ResourceId = tooLong256 }, Price());
        AssertInvalid(Submission(), Binding() with
        {
            RateCard = Card() with { Id = tooLong256 },
        }, Price());
        AssertInvalid(Submission(), Binding() with
        {
            RateCard = Card() with { Version = tooLong128 },
        }, Price());
        AssertInvalid(Submission(), Binding() with
        {
            RateCard = Card() with { Unit = tooLong128 },
        }, Price());
        AssertInvalid(Submission(), Binding(), Price() with { Unit = tooLong128 });
        AssertInvalid(Submission() with
        {
            Measurement = new UsageMeasurement(0, 0, 0, 0, 0, 0m, tooLong128, 0m),
        }, Binding(), Price());
    }

    [Fact]
    public void CanonicalRateCardIdentityIgnoresCollectionEnumerationOrder()
    {
        var firstCard = Card() with
        {
            ModelMultipliers = ImmutableDictionary<string, decimal>.Empty
                .Add("model-b", 2m).Add("model-a", 1m),
        };
        var secondCard = Card() with
        {
            ModelMultipliers = ImmutableDictionary<string, decimal>.Empty
                .Add("model-a", 1m).Add("model-b", 2m),
        };
        var firstBinding = Binding() with
        {
            NegotiatedCapabilities = ImmutableHashSet<string>.Empty
                .Add("metering-b").Add("metering-a"),
            RateCard = firstCard,
        };
        var secondBinding = Binding() with
        {
            NegotiatedCapabilities = ImmutableHashSet<string>.Empty
                .Add("metering-a").Add("metering-b"),
            RateCard = secondCard,
        };
        var eventId = Guid.NewGuid();

        Assert.Equal(
            UsageLedgerCanonicalizer.Serialize(Submission(eventId: eventId), firstBinding, Price(firstCard)),
            UsageLedgerCanonicalizer.Serialize(Submission(eventId: eventId), secondBinding, Price(secondCard)));
    }

    internal static UsageSubmission Submission(
        UsageAttribution? attribution = null,
        UsageModelBinding? model = null,
        UsageMeasurement? measurement = null,
        Guid? eventId = null) =>
        new(
            eventId ?? Guid.NewGuid(),
            DateTimeOffset.Parse("2026-10-06T12:34:56.1234567+00:00"),
            attribution ?? new UsageAttribution(
                "tenant-1", "project-1", "run-1", "session-1", "agent-1"),
            model ?? new UsageModelBinding(
                "model/ref", "model-1", "meter-a", "selection-1"),
            measurement ?? new UsageMeasurement(10, 20, 3, 2, 1, 25m, "provider-unit", 12.5m));

    internal static CostRateCard Card() =>
        new(
            "rate-card-1",
            "v1",
            "meter-a",
            "USD",
            1_000_000_000m,
            ImmutableDictionary<string, decimal>.Empty.Add("model-1", 1m));

    internal static CostBinding Binding(CostRateCard? card = null) =>
        new(
            "meter-a",
            "provider-a",
            "adapter-v1",
            1,
            "options-v1",
            "resource-1",
            1,
            ImmutableHashSet<string>.Empty.Add("metering"),
            card ?? Card());

    internal static CostPrice Price(CostRateCard? card = null) =>
        new(1.25m, "USD", CostDisposition.Estimate, card ?? Card(), null);

    private static void AssertInvalid(
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price) =>
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(submission, binding, price));
}
