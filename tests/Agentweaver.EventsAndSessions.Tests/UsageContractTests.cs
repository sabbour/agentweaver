using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class UsageContractTests
{
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

    [Fact]
    public void OptionalDispatchIdPreservesLegacySerializationAndBindsNewPayloads()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var legacy = Submission(eventId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        const string dispatchId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var withDispatch = legacy with
        {
            Attribution = legacy.Attribution with { DispatchId = dispatchId }
        };
        var canonicalPayload = UsageLedgerCanonicalizer.Serialize(legacy, Binding(), Price());

        Assert.DoesNotContain("dispatchId", JsonSerializer.Serialize(legacy.Attribution, options));
        Assert.DoesNotContain("accountingRevision", JsonSerializer.Serialize(
            new UsageLedgerEntry(legacy, Binding(), Price(), legacy.OccurredAt, new string('a', 64)),
            options));
        Assert.Equal(
            "{\"eventId\":\"11111111-1111-1111-1111-111111111111\",\"occurredAt\":\"2026-10-06T12:34:56.1234567\\u002B00:00\",\"attribution\":{\"tenantId\":\"tenant-1\",\"projectId\":\"project-1\",\"runId\":\"run-1\",\"sessionId\":\"session-1\",\"agentId\":\"agent-1\"},\"modelBinding\":{\"modelReference\":\"model/ref\",\"modelId\":\"model-1\",\"meterSource\":\"meter-a\",\"selectionRevision\":\"selection-1\"},\"measurement\":{\"inputTokens\":10,\"outputTokens\":20,\"cachedTokens\":3,\"reasoningTokens\":2,\"requestCount\":1,\"providerUnits\":25,\"providerUnit\":\"provider-unit\",\"durationMilliseconds\":12.5},\"costBinding\":{\"meterSource\":\"meter-a\",\"providerId\":\"provider-a\",\"adapterVersion\":\"adapter-v1\",\"optionsSchemaVersion\":1,\"optionsRevision\":\"options-v1\",\"resourceId\":\"resource-1\",\"resourceGeneration\":1,\"negotiatedCapabilities\":[\"metering\"],\"rateCard\":{\"id\":\"rate-card-1\",\"version\":\"v1\",\"meterSource\":\"meter-a\",\"unit\":\"USD\",\"nanoUnitsPerUnit\":1000000000,\"modelMultipliers\":{\"model-1\":1}}},\"price\":{\"amount\":1.25,\"unit\":\"USD\",\"disposition\":\"Estimate\",\"rateCard\":{\"id\":\"rate-card-1\",\"version\":\"v1\",\"meterSource\":\"meter-a\",\"unit\":\"USD\",\"nanoUnitsPerUnit\":1000000000,\"modelMultipliers\":{\"model-1\":1}},\"unpricedReason\":null}}",
            canonicalPayload);
        Assert.Equal(
            "dc76e2895c672c0321542b8b1a1c9cce9f4a219f02458042f4ce789419b3b048",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))));
        UsageLedgerValidation.Validate(withDispatch, Binding(), Price());
        Assert.Contains($"\"dispatchId\":\"{dispatchId}\"",
            UsageLedgerCanonicalizer.Serialize(withDispatch, Binding(), Price()));
        Assert.NotEqual(
            canonicalPayload,
            UsageLedgerCanonicalizer.Serialize(withDispatch, Binding(), Price()));
    }

    [Fact]
    public void OptionalSdkAccountingBindsCanonicalPayloadWithoutChangingLegacyNullPayloads()
    {
        var legacy = Submission(eventId: Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var completeAccounting = new SdkUsageAccountingObservation(
            new("sdk-session-1", 4, "usage-4"),
            SdkAiCreditsStatus.Complete,
            true);
        var complete = legacy with { SdkAccounting = completeAccounting };
        var partial = legacy with
        {
            SdkAccounting = completeAccounting with { AiCreditsStatus = SdkAiCreditsStatus.Partial }
        };
        var legacyPayload = UsageLedgerCanonicalizer.Serialize(legacy, Binding(), Price());
        var completePayload = UsageLedgerCanonicalizer.Serialize(complete, Binding(), Price());
        var messageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var a2aPayload = UsageLedgerCanonicalizer.Serialize(
            legacy with { A2AMessageId = messageId }, Binding(), Price());

        Assert.DoesNotContain("\"sdkAccounting\"", legacyPayload);
        Assert.DoesNotContain("\"a2AMessageId\"", legacyPayload);
        Assert.Contains("\"sdkAccounting\":", completePayload);
        Assert.Contains($"\"a2AMessageId\":\"{messageId:D}\"", a2aPayload);
        Assert.NotEqual(legacyPayload, a2aPayload);
        Assert.NotEqual(legacyPayload, completePayload);
        Assert.NotEqual(
            completePayload,
            UsageLedgerCanonicalizer.Serialize(partial, Binding(), Price()));
        Assert.NotEqual(
            completePayload,
            UsageLedgerCanonicalizer.Serialize(
                complete with
                {
                    SdkAccounting = completeAccounting with
                    {
                        Identity = new("sdk-session-1", 5, "usage-5")
                    }
                },
                Binding(),
                Price()));
    }

    [Fact]
    public void PricedHostedNanoAiuDoesNotRequireOptionalAccountingStatus()
    {
        var source = NativeCopilotSource();
        var binding = NativeCopilotBinding();
        var model = Submission().ModelBinding with { MeterSource = source.MeterSource };
        var measurement = Submission().Measurement with { ProviderUnit = "nano_aiu" };
        var complete = Submission(model: model, measurement: measurement) with
        {
            SdkSource = source,
            SdkAccounting = new(
                new(source.SdkSessionId, 4, "usage-4"),
                SdkAiCreditsStatus.Complete,
                true)
        };
        var partial = complete with
        {
            SdkAccounting = complete.SdkAccounting! with
            {
                AiCreditsStatus = SdkAiCreditsStatus.Partial
            }
        };
        var unavailable = complete with
        {
            SdkAccounting = complete.SdkAccounting! with
            {
                AiCreditsStatus = SdkAiCreditsStatus.Unavailable,
                AiCreditsStatusReported = false
            }
        };
        var missingAccounting = complete with { SdkAccounting = null };

        Assert.True(UsageLedgerValidation.HasCompleteSdkAccounting(complete));
        var priced = Price(binding.RateCard);
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(complete, priced.Disposition));
        Assert.True(UsageLedgerValidation.HasCompleteSdkAccounting(partial));
        Assert.True(UsageLedgerValidation.HasCompleteSdkAccounting(unavailable));
        Assert.True(UsageLedgerValidation.HasCompleteSdkAccounting(missingAccounting));
        foreach (var identity in new[]
                 {
                     new SdkUsageAccountingIdentity("other-session", 4, "usage-4"),
                     new SdkUsageAccountingIdentity(source.SdkSessionId, 0, "usage-4"),
                     new SdkUsageAccountingIdentity(source.SdkSessionId, 4, " ")
                 })
        {
            Assert.False(UsageLedgerValidation.HasCompleteSdkAccounting(complete with
            {
                SdkAccounting = complete.SdkAccounting! with { Identity = identity }
            }));
        }
        UsageLedgerValidation.Validate(partial, binding, priced);
        Assert.Equal(1.25m, priced.Amount);
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(partial, priced.Disposition));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(unavailable, priced.Disposition));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(missingAccounting, priced.Disposition));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(complete, CostDisposition.Unpriced));
        Assert.False(UsageLedgerValidation.HasCompleteSdkAccounting(complete with
        {
            SdkSource = source with { SourceMode = "byok" }
        }));
        Assert.False(UsageLedgerValidation.HasCompleteSdkAccounting(complete with
        {
            ModelBinding = model with { MeterSource = SdkMeterSources.ByokTokens }
        }));

        var byokSource = source with { SourceMode = "byok", MeterSource = SdkMeterSources.ByokTokens };
        var byok = partial with
        {
            SdkSource = byokSource,
            ModelBinding = partial.ModelBinding with { MeterSource = SdkMeterSources.ByokTokens },
            Measurement = partial.Measurement with
            {
                ProviderUnits = null,
                ProviderUnit = "tokens",
                CachedTokens = 7,
                CacheWriteTokens = 5
            }
        };
        Assert.True(UsageLedgerValidation.HasCompleteSdkAccounting(byok));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(byok, priced.Disposition));
        Assert.False(UsageLedgerValidation.HasCompleteSdkAccounting(byok with
        {
            Measurement = byok.Measurement with { ProviderUnit = null }
        }));
        Assert.False(UsageLedgerValidation.IsFinanciallyComplete(
            byok with { Measurement = byok.Measurement with { InputTokens = null } },
            priced.Disposition));
        Assert.True(UsageLedgerValidation.IsFinanciallyComplete(Submission(), priced.Disposition));
    }

    [Fact]
    public void DispatchIdMustBeANonEmptyCanonicalGuid()
    {
        var submission = Submission();
        foreach (var dispatchId in new[]
                 {
                     "",
                     Guid.Empty.ToString("D"),
                     "not-a-guid",
                     "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
                 })
        {
            AssertInvalid(
                submission with
                {
                    Attribution = submission.Attribution with { DispatchId = dispatchId },
                },
                Binding(),
                Price());
        }
    }

    [Fact]
    public void SourceCompletionManifestDigestIsCanonicalAndRejectsTampering()
    {
        var manifest = SourceCompletionManifest();
        var reversed = manifest with { SourceReceipts = manifest.SourceReceipts.Reverse().ToImmutableArray() };
        var digest = UsageDispatchSourceCompletionManifestContract.ComputeDigest(manifest);

        Assert.Equal(digest, UsageDispatchSourceCompletionManifestContract.ComputeDigest(reversed));
        UsageDispatchSourceCompletionManifestContract.Validate(manifest with { ReceiptDigest = digest });
        Assert.Throws<ArgumentException>(() =>
            UsageDispatchSourceCompletionManifestContract.Validate(manifest with
            {
                ReceiptDigest = digest,
                SourceReceipts = [.. manifest.SourceReceipts, manifest.SourceReceipts[0]]
            }));
        Assert.Throws<ArgumentException>(() =>
            UsageDispatchSourceCompletionManifestContract.Validate(manifest with
            {
                ReceiptDigest = digest,
                RunId = "different-run"
            }));
        Assert.Throws<ArgumentException>(() =>
            UsageDispatchSourceCompletionManifestContract.Validate(manifest with
            {
                DispatchId = Guid.Empty.ToString("D"),
                ReceiptDigest = digest
            }));
        Assert.Throws<ArgumentException>(() =>
            UsageDispatchSourceCompletionManifestContract.Validate(manifest with
            {
                SourceReceipts = [null!],
                ReceiptDigest = digest
            }));
    }

    [Fact]
    public void EmptyManifestIsAnExplicitCanonicalReceiptSet()
    {
        var manifest = SourceCompletionManifest() with { SourceReceipts = [] };
        var digest = UsageDispatchSourceCompletionManifestContract.ComputeDigest(manifest);

        UsageDispatchSourceCompletionManifestContract.Validate(manifest with { ReceiptDigest = digest });
    }

    [Fact]
    public void DispatchWitnessOmitsLegacyMissingTotalsAndCarriesScopedTotalsWhenPresent()
    {
        var witness = new UsageDispatchAccountingWitness(
            Guid.NewGuid().ToString("D"),
            new("tenant-1", "project-1", "run-1", "meter-a", "USD", null),
            1,
            [],
            new string('a', 64),
            0m,
            0,
            0,
            UsageDispatchAccountingStatus.SourceComplete,
            Guid.NewGuid(),
            1,
            new string('b', 64));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.DoesNotContain("\"runTotals\"", JsonSerializer.Serialize(witness, options));

        var totals = new UsageRunTotals("tenant-1", "project-1", "run-1", 0, true, [], []);
        Assert.Contains(
            "\"runTotals\"",
            JsonSerializer.Serialize(witness with { RunTotals = totals }, options));
    }

    [Fact]
    public void OptionalA2AMessageIdsStayOmittedForLegacyPayloads()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var observation = new SdkUsageObservation(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), "sdk-session",
            DateTimeOffset.Parse("2026-10-06T12:34:56.1234567+00:00"), "model-1",
            10, 20, 3, null, 2, null, 12.5m);
        var usage = Submission();

        Assert.DoesNotContain("\"a2AMessageId\"", JsonSerializer.Serialize(observation, options));
        Assert.DoesNotContain("\"a2AMessageId\"", JsonSerializer.Serialize(usage, options));
        var messageId = Guid.NewGuid();
        Assert.Contains(
            $"\"a2AMessageId\":\"{messageId:D}\"",
            JsonSerializer.Serialize(observation with { A2AMessageId = messageId }, options));
        Assert.Contains(
            $"\"a2AMessageId\":\"{messageId:D}\"",
            JsonSerializer.Serialize(usage with { A2AMessageId = messageId }, options));
    }

    [Fact]
    public void PreflightContractAllowsExplicitUnpricedAndRejectsInconsistentQuotes()
    {
        var receipt = new RuntimeUsageCostPreflightReceipt(
            1,
            Guid.NewGuid(),
            1,
            1,
            "model.ref",
            "model-1",
            "meter-a",
            new string('a', 64),
            new string('b', 64),
            Binding(),
            true,
            null);

        RuntimeUsageCostPreflightContract.Validate(receipt);
        RuntimeUsageCostPreflightContract.Validate(receipt with
        {
            IsPriced = false,
            UnpricedReason = "quote-unavailable"
        });
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageCostPreflightContract.Validate(receipt with
            {
                IsPriced = false,
                UnpricedReason = null
            }));
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

    private static SdkSessionFacts NativeCopilotSource() =>
        new(
            Guid.NewGuid(),
            "sdk-session-1",
            "sdk-v1",
            "runtime-v1",
            "model.ref",
            "model-1",
            new string('c', 64),
            null,
            "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu,
            new string('d', 64),
            1);

    private static CostBinding NativeCopilotBinding()
    {
        var card = Card() with { MeterSource = SdkMeterSources.CopilotNanoAiu };
        return Binding(card) with { MeterSource = SdkMeterSources.CopilotNanoAiu };
    }

    private static UsageDispatchSourceCompletionManifest SourceCompletionManifest() =>
        new(
            Guid.NewGuid().ToString("D"),
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            Guid.NewGuid(),
            2,
            3,
            [
                new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64)),
                new(Guid.NewGuid(), Guid.NewGuid(), new string('b', 64))
            ],
            string.Empty,
            Guid.NewGuid(),
            4);

    private static void AssertInvalid(
        UsageSubmission submission,
        CostBinding? binding,
        CostPrice price) =>
        Assert.Throws<ArgumentException>(() =>
            UsageLedgerValidation.Validate(submission, binding, price));
}
