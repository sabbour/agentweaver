using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
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
