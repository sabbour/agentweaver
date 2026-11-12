using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class CheckpointRecoveryHoldTests
{
    [Fact]
    public void GateProgressPayloadIsNotHeld()
    {
        Assert.False(IsHeld(
            """{"executionFence":2,"checkpointRecovery":{"phase":"pending","gateRequestId":"gate"}}""",
            """{"phase":"blocked","dispatchHeld":false}"""));
    }

    [Fact]
    public void LegacyDispatchHeldWithoutBoundaryKindIsNotHeld()
    {
        Assert.False(IsHeld(
            """{"executionFence":2,"checkpointRecovery":{"phase":"pending"}}""",
            """{"phase":"pending","dispatchHeld":true}"""));
    }

    [Fact]
    public void ExplicitChildJoinHoldIsHeld()
    {
        Assert.True(IsHeld(
            """{"executionFence":2,"checkpointRecovery":{"boundaryKind":"childJoin","dispatchHeld":true}}""",
            """{"boundaryKind":"childJoin","dispatchHeld":true,"holdFence":2,"childManifest":"{}","childManifestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""));
    }

    [Fact]
    public void CheckpointRecoveryHoldFailsClosedForMalformedManifestData()
    {
        Assert.True(IsHeld(
            """{"executionFence":2,"checkpointRecovery":{"boundaryKind":"childJoin","dispatchHeld":true}}""",
            """{"boundaryKind":"childJoin","dispatchHeld":true}"""));
    }

    [Fact]
    public void CheckpointRecoveryHoldRemainsUntilEveryManifestMemberIsReady()
    {
        var manifest = ChildCheckpointRecoveryManifestTests.CreateManifest();
        var encoded = ChildCheckpointRecoveryManifestCodec.Encode(manifest);
        var status = new CheckpointRecoveryStatus(
            "ready",
            manifest.SourceCheckpointId,
            manifest.SourceCheckpointRevision,
            manifest.SourceExecutionFence,
            manifest.HoldFence,
            manifest.RootRuntimeInstanceId,
            manifest.RootRegistrationRevision)
        {
            CurrentStep = "ready",
            BoundaryKind = "childJoin",
            DispatchHeld = false,
            ChildManifestSha256 = encoded.Sha256,
            SourceCheckpointHash = manifest.SourceCheckpointHash,
            RecoveryCheckpointId = "recovery",
            RecoveryCheckpointRevision = manifest.SourceCheckpointRevision + 1,
            DecisionStateVersion = manifest.SourceDecisionStateVersion
        };
        var result = new RunExecutionTransitionResult(
            manifest.OperationId,
            manifest.Root,
            "active",
            "active",
            manifest.SourceExecutionFence,
            manifest.HoldFence,
            0,
            1,
            2,
            1,
            2,
            null,
            null,
            null,
            null,
            ImmutableArray<SessionExecutionFenceChange>.Empty)
        {
            CheckpointRecovery = status
        };
        var resultJson = JsonSerializer.SerializeToElement(
            result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(CheckpointRecoveryHold.IsHeldPayload(
            resultJson,
            Progress(encoded, allMembersReady: false),
            manifest.HoldFence));
        Assert.False(CheckpointRecoveryHold.IsHeldPayload(
            resultJson,
            Progress(encoded, allMembersReady: true),
            manifest.HoldFence));
    }

    private static JsonElement Progress(EncodedChildRecoveryManifest manifest, bool allMembersReady) =>
        JsonSerializer.SerializeToElement(new
        {
            boundaryKind = "childJoin",
            dispatchHeld = false,
            holdFence = 2,
            childManifest = manifest.Json,
            childManifestSha256 = manifest.Sha256,
            phase = "ready",
            allMembersReady
        });

    private static bool IsHeld(string result, string progress)
    {
        using var resultDocument = JsonDocument.Parse(result);
        using var progressDocument = JsonDocument.Parse(progress);
        return CheckpointRecoveryHold.IsHeldPayload(
            resultDocument.RootElement, progressDocument.RootElement, fence: 2);
    }
}
