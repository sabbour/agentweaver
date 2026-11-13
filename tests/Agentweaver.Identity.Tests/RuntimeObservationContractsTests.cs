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
    public void V2KeepsMissingProofAndLegacyCoverageExplicit()
    {
        var binding = new RuntimeObservationV2Binding(
            "project", "run", "root", "root", true, ValidHash, 1, 1,
            null, null, DateTimeOffset.UtcNow);
        var unknown = new RuntimeObservationCount(RuntimeObservationSectionState.Unknown, null,
            "legacy_or_uninstrumented_history");
        var unavailable = new RuntimeObservationCount(RuntimeObservationSectionState.Unavailable, null,
            "native_send_proof_not_complete");
        var response = new RuntimeObservationV2(
            2,
            binding,
            new(null, null, null, null, null, "nanoAiu", "copilotRun"),
            new(unknown, unavailable, unavailable, unavailable, unknown, unavailable),
            new(RuntimeObservationSectionState.Unknown, null),
            new(Guid.NewGuid(), 0, false, null, "legacy_or_uninstrumented_history",
                false, [], null, null)).Validate();

        Assert.Equal("legacy_or_uninstrumented_history", response.History.IncompleteReason);
        Assert.Equal(RuntimeObservationSectionState.Unknown, response.Totals.ModelSlotsReserved.State);
        Assert.Equal("native_send_proof_not_complete", response.Totals.NativeSendsKnown.Reason);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (response with
            {
                History = response.History with { ExactAttempt = true }
            }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (response with
            {
                Binding = binding with { RuntimeInstanceId = Guid.NewGuid() }
            }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (response with
            {
                Binding = binding with { RegistrationRevision = 1 }
            }).Validate());
    }

    [Fact]
    public void NativeToolRejectionContractsRequireTheKnownLimitDecisionAndMatchingAcknowledgement()
    {
        var request = new RuntimeNativeToolPermissionRejectionRequest(
            1, Guid.NewGuid(), 1, 1, Guid.NewGuid(), Guid.NewGuid(), ValidHash,
            RuntimeNativeToolPermissionRejectionContract.NumericToolLimitReasonCode);

        Assert.Same(request, request.Validate());
        var acknowledgement = new RuntimeNativeToolPermissionRejectionAcknowledgement(
            1, Guid.NewGuid(), ValidHash);
        Assert.Same(acknowledgement, acknowledgement.Validate(request));

        Assert.Throws<RuntimeAuthorizationException>(() =>
            (request with { DispatchId = Guid.Empty }).Validate());
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (request with { ReasonCode = "runtime_action_denied" }).Validate());
        var changedRequest = request with { RequestHash = new string('b', 64) };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            acknowledgement.Validate(changedRequest));
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
    public void ReboundDispatchProjectionKeepsOriginAndEffectiveBindingsSeparate()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var originRuntime = Guid.NewGuid();
        var effectiveRuntime = Guid.NewGuid();
        var projection = new RuntimeObservationNativeRebind(
            new(originRuntime, 1, 1, "origin-checkpoint", 1, ValidHash),
            new(effectiveRuntime, 2, 2, "recovery-checkpoint", 3, new string('b', 64)),
            Guid.NewGuid(), new string('c', 64), Guid.NewGuid(), new string('d', 64));
        var dispatch = new RuntimeObservationDispatch(
            Guid.NewGuid(), "root", "child", "plan", "association",
            "origin-checkpoint", 1, 1, 1, "prepared", null, null, null, null,
            timestamp, timestamp)
        {
            NativeRebind = projection
        }.Validate();

        var json = JsonSerializer.Serialize(dispatch, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(originRuntime, dispatch.NativeRebind!.Origin.RuntimeInstanceId);
        Assert.Equal(effectiveRuntime, dispatch.NativeRebind.Effective.RuntimeInstanceId);
        Assert.Contains("\"nativeRebind\"", json, StringComparison.Ordinal);
        Assert.Contains("\"origin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"effective\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("receiptPayload", json, StringComparison.Ordinal);
        Assert.Throws<RuntimeAuthorizationException>(() =>
            (dispatch with
            {
                NativeRebind = projection with { EffectiveRequestHash = "not-a-hash" }
            }).Validate());
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
