using System.Text.Json;
using Agentweaver.Orchestrator;
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
    public void MalformedChildJoinPayloadFailsClosed()
    {
        Assert.True(IsHeld(
            """{"executionFence":2,"checkpointRecovery":{"boundaryKind":"childJoin","dispatchHeld":true}}""",
            """{"boundaryKind":"childJoin","dispatchHeld":true}"""));
    }

    private static bool IsHeld(string result, string progress)
    {
        using var resultDocument = JsonDocument.Parse(result);
        using var progressDocument = JsonDocument.Parse(progress);
        return CheckpointRecoveryHold.IsHeldPayload(
            resultDocument.RootElement, progressDocument.RootElement, fence: 2);
    }
}
