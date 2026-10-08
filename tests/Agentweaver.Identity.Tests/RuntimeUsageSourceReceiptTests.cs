using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeUsageSourceReceiptTests
{
    [Fact]
    public void NativeReceiptBindsAllCurrentOwnerAndSdkFieldsWithoutInventingMeasurements()
    {
        var receipt = Receipt();
        RuntimeUsageSourceReceiptContract.Validate(receipt);
        Assert.Null(receipt.Usage.Measurement.RequestCount);
        Assert.Equal(5, receipt.Usage.Measurement.CacheWriteTokens);
        Assert.Equal(1234567.25m, receipt.Usage.Measurement.ProviderUnits);
        Assert.Equal(2.5m, receipt.Usage.SdkSource!.ModelMultiplier);
        foreach (var usage in new[]
        {
            receipt.Usage with { Attribution = receipt.Usage.Attribution with { TurnId = "foreign-turn" } },
            receipt.Usage with { EventId = Guid.NewGuid() },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SourceMode = "byok" } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { AcceptedSelectionHash = new string('b', 64) } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { RegistrationRevision = 2 } },
            receipt.Usage with { SdkSource = receipt.Usage.SdkSource! with { SdkSessionId = "foreign-session" } },
            receipt.Usage with { Measurement = receipt.Usage.Measurement with { RequestCount = 1 } }
        })
            Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageSourceReceiptContract.ValidateUsage(
                receipt.Registration, usage));
        var changed = receipt with
        {
            Usage = receipt.Usage with
            {
                Measurement = receipt.Usage.Measurement with { CacheWriteTokens = 6 }
            }
        };
        Assert.Throws<RuntimeAuthorizationException>(() => RuntimeUsageSourceReceiptContract.Validate(changed));
        Assert.NotEqual(receipt.CanonicalPayloadHash,
            RuntimeUsageSourceReceiptContract.Hash(changed.Registration, changed.Usage));
    }

    [Fact]
    public void MissingNativeMeasurementsRemainNullAndHistoricalReceiptDoesNotRequireALiveLease()
    {
        var receipt = Receipt();
        var missing = receipt.Usage with
        {
            Measurement = new(null, null, null, null, null, null, "nano_aiu", null)
        };
        var expired = receipt.Registration with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var history = new RuntimeUsageSourceReceipt(
            1, receipt.ReceiptId, expired, missing,
            RuntimeUsageSourceReceiptContract.Hash(expired, missing), receipt.RecordedAt);
        RuntimeUsageSourceReceiptContract.Validate(history);
        Assert.Null(history.Usage.Measurement.InputTokens);
        Assert.Null(history.Usage.Measurement.CacheWriteTokens);
        Assert.Null(history.Usage.Measurement.ProviderUnits);
        Assert.Null(history.Usage.Measurement.DurationMilliseconds);
        Assert.Null(history.Usage.Measurement.RequestCount);
    }

    private static RuntimeUsageSourceReceipt Receipt()
    {
        var registration = new RuntimeRegistration(
            Guid.NewGuid(), 1,
            new("https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
                "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
                "environment", "placement", 1, "profile", new("https://runtime.test/configure"),
                new("https://orchestrator.test/internal/runtime/observations"))
            {
                ModelSelectionReference = "accepted-model",
                ModelSourceMode = ModelSourceMode.HostedCopilot,
                PlacementProviderId = "sandbox-platform",
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 2,
                EnvironmentCurrentFencingGeneration = 3,
                EnvironmentProviderFencingGeneration = 3
            },
            RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(1));
        var source = new SdkSessionFacts(
            registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(registration.Binding),
            "1.0.11+source", "runtime-v1", "accepted-model", "native-model", new string('b', 64),
            2.5m, "hosted-copilot", SdkMeterSources.CopilotNanoAiu, registration.Binding.AcceptedSelectionHash, 1);
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, 1234567.25m, 12.5m);
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        return new(1, Guid.NewGuid(), registration, usage,
            RuntimeUsageSourceReceiptContract.Hash(registration, usage), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ByokTokenObservationsCannotAcquireCopilotUnitsOrHostedSourceFacts()
    {
        var hosted = Receipt();
        var registration = hosted.Registration with
        {
            Binding = hosted.Registration.Binding with { ModelSourceMode = ModelSourceMode.Byok }
        };
        var source = hosted.Usage.SdkSource! with
        {
            SourceMode = "byok", MeterSource = SdkMeterSources.ByokTokens, ModelMultiplier = null
        };
        var eventId = Guid.NewGuid().ToString("D");
        var observation = new SdkUsageObservation(
            SdkUsageIdentity.Create(source.RuntimeInstanceId, source.SdkSessionId, eventId),
            eventId, source.SdkSessionId, DateTimeOffset.UtcNow, source.ModelId,
            17, 11, 7, 5, 3, null, 12.5m);
        var usage = RuntimeUsageSourceReceiptContract.CreateUsage(registration, source, observation);
        Assert.Equal("tokens", usage.Measurement.ProviderUnit);
        Assert.Null(usage.Measurement.ProviderUnits);
        Assert.Equal(17, usage.Measurement.InputTokens);
        foreach (var invalid in new[]
        {
            usage with { Measurement = usage.Measurement with { ProviderUnits = 0 } },
            usage with { Measurement = usage.Measurement with { ProviderUnit = "nano_aiu" } },
            usage with { SdkSource = source with { SourceMode = "hosted-copilot" } },
            usage with { SdkSource = source with { ModelMultiplier = 0 } }
        })
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceReceiptContract.ValidateUsage(registration, invalid));
    }
}
