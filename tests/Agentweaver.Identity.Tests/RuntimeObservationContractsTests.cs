using System.Text.Json;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeObservationContractsTests
{
    private static readonly string ValidHash = new('a', 64);

    [Fact]
    public void SectionsRequireValuesOnlyWhenAvailable()
    {
        new RuntimeObservationSection<RuntimeObservationRegistration>(
            RuntimeObservationSectionState.Available,
            new RuntimeObservationRegistration(
                Guid.NewGuid(), 1, RuntimeObservationRegistrationState.Active, DateTimeOffset.UtcNow))
            .Validate();
        new RuntimeObservationSection<RuntimeObservationRegistration>(
            RuntimeObservationSectionState.Unknown, null).Validate();
        new RuntimeObservationSection<RuntimeObservationRegistration>(
            RuntimeObservationSectionState.Unavailable, null).Validate();

        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeObservationSection<RuntimeObservationRegistration>(
                RuntimeObservationSectionState.Available, null).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeObservationSection<RuntimeObservationRegistration>(
                RuntimeObservationSectionState.Unknown,
                new RuntimeObservationRegistration(
                    Guid.NewGuid(), 1, RuntimeObservationRegistrationState.Active, DateTimeOffset.UtcNow))
                .Validate());
    }

    [Fact]
    public void V1ValidatesEveryAvailableSectionAndCapsStoredEvidence()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var response = new RuntimeObservationV1(
            1,
            new(RuntimeObservationSectionState.Available,
                new RuntimeObservationRegistration(
                    Guid.NewGuid(), 1, RuntimeObservationRegistrationState.Active, timestamp)),
            new(RuntimeObservationSectionState.Unknown, null),
            new(RuntimeObservationSectionState.Available,
                new RuntimeObservationCreditGuard(0, 0, ValidHash, 1, false, false, timestamp)),
            new(RuntimeObservationSectionState.Available,
                new RuntimeObservationDispatchCollection([], false)),
            new(RuntimeObservationSectionState.Available,
                new RuntimeObservationToolReservationCollection([], false)));

        Assert.Same(response, response.Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (response with
            {
                CreditGuard = new(RuntimeObservationSectionState.Available,
                    new RuntimeObservationCreditGuard(-1, 0, ValidHash, 1, false, false, timestamp))
            }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (response with { ContractVersion = 2 }).Validate());
    }

    [Fact]
    public void EvidenceCollectionsRejectOversizedOrDuplicatedRows()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var reservation = new RuntimeObservationToolReservation(id, "session-a", 1, timestamp);

        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeObservationToolReservationCollection(
                [reservation, reservation], false).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeObservationToolReservationCollection(
                Enumerable.Range(0, RuntimeObservationToolReservationCollection.MaximumItems + 1)
                    .Select(index => new RuntimeObservationToolReservation(
                        Guid.NewGuid(), $"session-{index}", 1, timestamp))
                    .ToArray(),
                false).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            new RuntimeObservationToolReservationCollection(
                [reservation], true).Validate());
    }

    [Fact]
    public void PublicProjectionDoesNotSerializeRegistrationSecretsOrPayloads()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var response = new RuntimeObservationV1(
            1,
            new(RuntimeObservationSectionState.Unknown, null),
            new(RuntimeObservationSectionState.Unknown, null),
            new(RuntimeObservationSectionState.Unknown, null),
            new(RuntimeObservationSectionState.Unknown, null),
            new(RuntimeObservationSectionState.Unknown, null)).Validate();

        var json = JsonSerializer.Serialize(response);

        Assert.Contains("Unknown", json);
        Assert.DoesNotContain("ModelCredentialReference", json);
        Assert.DoesNotContain("ConfigureEndpoint", json);
        Assert.DoesNotContain("ObservationEndpoint", json);
        Assert.DoesNotContain("PromptHash", json);
        Assert.DoesNotContain("CostIdentity", json);
        Assert.DoesNotContain("RequestHash", json);
        Assert.DoesNotContain(timestamp.ToString("O"), json);
    }

    [Fact]
    public void ExecutionProjectionRetainsEvidenceButOmitsThePendingPromptHash()
    {
        var messageId = Guid.NewGuid();
        var source = new RuntimeExecutionObservationV2(
            2,
            new RuntimeExecutionObservation(
                1, "root", "child", 1, "plan", "association",
                1, "checkpoint", 1, 1, RuntimeExecutionTaskStatus.Running)
            {
                PendingDispatch = new RuntimeExecutionPendingDispatch(messageId, ValidHash)
            },
            new RuntimeExecutionCheckpointParent(true, null));

        var projected = RuntimeObservationExecutionV2.From(source);
        var json = JsonSerializer.Serialize(projected);

        Assert.Equal(messageId, projected.Observation.PendingDispatch?.MessageId);
        Assert.Equal(source.CheckpointParent, projected.CheckpointParent);
        Assert.DoesNotContain("PromptSha256", json);
        Assert.DoesNotContain(ValidHash, json);
    }
}
