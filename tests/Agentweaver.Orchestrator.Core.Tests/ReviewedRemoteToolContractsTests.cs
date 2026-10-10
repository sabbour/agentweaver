using Agentweaver.Orchestrator.Core;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ReviewedRemoteToolContractsTests
{
    [Fact]
    public void SnapshotDigestBindsConnectionCatalogToolSchemaAndPermissionReview()
    {
        var baseline = Snapshot();
        var changedConnection = Snapshot(connectionConfigurationRevision: 5);
        var changedHeadRevision = Snapshot(connectionRowRevision: 7);
        var changedDiscovery = Snapshot(connectionDiscoveryRevision: null);
        var changedState = Snapshot(connectionState: "Disabled");
        var changedConfigurationDigest = Snapshot(configurationSha256: new string('f', 64));
        var changedEndpoint = Snapshot(endpointUri: "https://other-mcp.example.test/api");
        var changedResource = Snapshot(resourceUri: null);
        var changedAuthenticationMode = Snapshot(authenticationMode: "None");
        var changedIdentityBinding = Snapshot(identityBindingReference: "identity-binding-9");
        var changedRegistryPin = Snapshot(registryServerPin: new ReviewedRemoteToolRegistryServerPin(
            "registry.example.test", "1.2.3", new string('2', 64)));
        var changedSchema = Snapshot(toolSchemaJson: """{"type":"object","properties":{"query":{"type":"number"}}}""");
        var changedPermission = Snapshot(actionId: "tool.write");

        Assert.NotEqual(baseline.SnapshotDigest, changedConnection.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedHeadRevision.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedDiscovery.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedState.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedConfigurationDigest.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedEndpoint.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedResource.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedAuthenticationMode.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedIdentityBinding.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedRegistryPin.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedSchema.SnapshotDigest);
        Assert.NotEqual(baseline.SnapshotDigest, changedPermission.SnapshotDigest);
        Assert.Equal(baseline.Reference.SnapshotId, baseline.SnapshotId);
        Assert.Equal(baseline.Reference.SnapshotDigest, baseline.SnapshotDigest);
        Assert.Equal("agent-1", baseline.Reference.AgentId);
        Assert.Equal("node-1", baseline.Reference.NodeId);
    }

    [Fact]
    public void SameNativeCallIdKeepsOperationIdentityButChangedCanonicalInputChangesFingerprint()
    {
        var snapshot = Snapshot();
        var first = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"z":1,"a":{"y":true,"x":2}}""");
        var sameCallWithDifferentJsonFormatting = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{ "a" : { "x":2, "y":true }, "z":1 }""");
        var changedInput = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"a":{"x":3,"y":true},"z":1}""");

        Assert.Equal(first.EventId, sameCallWithDifferentJsonFormatting.EventId);
        Assert.Equal(first.InputHash, sameCallWithDifferentJsonFormatting.InputHash);
        Assert.Equal(first.EventId, changedInput.EventId);
        Assert.NotEqual(first.InputHash, changedInput.InputHash);
        Assert.NotEqual(first.ArgumentsDigest, changedInput.ArgumentsDigest);
    }

    [Fact]
    public void OperationIdentityChangesWithNativeCallIdOrRunScope()
    {
        var snapshot = Snapshot();
        var first = CreateCall(snapshot);
        var otherNativeCall = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-8",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");
        var otherRun = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(runId: "run-2"), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");

        Assert.NotEqual(first.EventId, otherNativeCall.EventId);
        Assert.NotEqual(first.EventId, otherRun.EventId);
    }

    [Fact]
    public void CallFingerprintBindsRegistrationRevisionAndAcceptedSelection()
    {
        var snapshot = Snapshot();
        var baseline = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");
        var changedRegistration = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(registrationRevision: 6), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");
        var changedSelection = ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(acceptedSelectionHash: new string('f', 64)), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");

        Assert.Equal(baseline.EventId, changedRegistration.EventId);
        Assert.NotEqual(baseline.InputHash, changedRegistration.InputHash);
        Assert.Equal(baseline.EventId, changedSelection.EventId);
        Assert.NotEqual(baseline.InputHash, changedSelection.InputHash);
    }

    [Fact]
    public void SnapshotAgentNodeAndToolAreAnExactIntersection()
    {
        var snapshot = Snapshot();

        Assert.Throws<ArgumentException>(() => ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "other-agent", "node-1", "remote.lookup", """{"query":"x"}"""));
        Assert.Throws<ArgumentException>(() => ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "other-node", "remote.lookup", """{"query":"x"}"""));
        Assert.Throws<ArgumentException>(() => ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.write", """{"query":"x"}"""));
    }

    [Fact]
    public void SnapshotRejectsUnsupportedAuthenticationAndTransportProfiles()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(authenticationMode: "Unknown"));
        Assert.Throws<ArgumentException>(() => Snapshot(transportProfile: "Sse"));
    }

    [Fact]
    public void SnapshotRejectsUnknownConnectionState()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(connectionState: "Connecting"));
    }

    [Theory]
    [InlineData("""{"query":"a","query":"b"}""")]
    [InlineData("""{"nested":{"value":1,"value":2}}""")]
    public void AmbiguousDuplicateJsonPropertiesAreRejected(string arguments)
    {
        Assert.Throws<ArgumentException>(() => ReviewedRemoteToolCall.Create(
            Snapshot(), ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", arguments));
    }

    [Fact]
    public void ToolResultEnvelopePreservesOpaqueContentAndOwnerBoundAuthority()
    {
        var call = CreateCall(Snapshot());
        var authority = Authority();
        var result = new object();
        var envelope = new RemoteToolResultEnvelope<object>(
            call, authority, RemoteToolResultContentClass.TextOnly, result);

        Assert.Equal(RemoteToolGuardrailStage.ToolResult, envelope.Stage);
        Assert.Equal(call.EventId, envelope.OperationId);
        Assert.Equal(call.Snapshot.SnapshotDigest, envelope.Snapshot.SnapshotDigest);
        Assert.Equal(call.ArgumentsDigest, envelope.ArgumentsDigest);
        Assert.Same(result, envelope.Content);
        Assert.Same(authority, envelope.Authority);
    }

    [Fact]
    public void ToolResultEnvelopeRejectsAuthorityForAnotherRunOrSelection()
    {
        var call = CreateCall(Snapshot());
        var binding = ExecutionBinding(runId: "other-run");

        Assert.Throws<ArgumentException>(() => new RemoteToolResultEnvelope<object>(
            call,
            Authority(binding),
            RemoteToolResultContentClass.Structured,
            new object()));

        Assert.Throws<ArgumentException>(() => new RemoteToolResultEnvelope<object>(
            call,
            Authority(acceptedSelectionHash: new string('f', 64)),
            RemoteToolResultContentClass.Structured,
            new object()));
        Assert.Throws<ArgumentException>(() => new RemoteToolResultEnvelope<object>(
            call,
            Authority(executionFence: 8),
            RemoteToolResultContentClass.Structured,
            new object()));
        Assert.Throws<ArgumentException>(() => new RemoteToolResultEnvelope<object>(
            call,
            Authority(registrationRevision: 8),
            RemoteToolResultContentClass.Structured,
            new object()));
    }

    private static ReviewedRemoteToolSnapshot Snapshot(
        long connectionConfigurationRevision = 4,
        long connectionRowRevision = 6,
        long? connectionDiscoveryRevision = 8,
        string connectionState = "Enabled",
        string? configurationSha256 = null,
        string? endpointUri = null,
        string? resourceUri = "https://mcp.example.test/resource",
        string authenticationMode = "DelegatedOAuth",
        string? identityBindingReference = null,
        string transportProfile = "StreamableHttp20250618",
        ReviewedRemoteToolRegistryServerPin? registryServerPin = null,
        string toolSchemaJson = """{"properties":{"query":{"type":"string"}},"type":"object"}""",
        string actionId = "tool.read") =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "project-1",
            "agent-1",
            "node-1",
            "connection-1",
            connectionRowRevision,
            connectionConfigurationRevision,
            connectionDiscoveryRevision,
            connectionState,
            configurationSha256 ?? new string('a', 64),
            endpointUri ?? "https://mcp.example.test/api",
            resourceUri,
            authenticationMode,
            identityBindingReference,
            transportProfile,
            registryServerPin,
            "catalog-r8",
            new string('b', 64),
            "remote.lookup",
            "tool-r2",
            new string('c', 64),
            "schema-r3",
            new string('d', 64),
            """{"description":"Lookup","name":"remote.lookup"}""",
            toolSchemaJson,
            new ReviewedRemoteToolPermissionMetadata(
                actionId,
                "runtime.execution",
                """{"source":"explicit-review","version":1}"""));

    private static ReviewedRemoteToolCall CreateCall(ReviewedRemoteToolSnapshot snapshot) =>
        ReviewedRemoteToolCall.Create(
            snapshot, ExecutionBinding(), "native-call-7",
            "agent-1", "node-1", "remote.lookup", """{"query":"x"}""");

    private static RemoteToolCallExecutionBinding ExecutionBinding(
        string runId = "run-1",
        string? acceptedSelectionHash = null,
        long executionFence = 7,
        long registrationRevision = 5) =>
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            registrationRevision,
            "https://identity.test/",
            "actor-1",
            "tenant-1",
            "project-1",
            runId,
            "session-1",
            "step-1",
            2,
            3,
            4,
            "context-5",
            acceptedSelectionHash ?? new string('e', 64),
            executionFence);

    private static RemoteToolCallAuthorityBinding Authority(
        RemoteToolCallExecutionBinding? binding = null,
        string? acceptedSelectionHash = null,
        long? executionFence = null,
        long? registrationRevision = null)
    {
        binding ??= ExecutionBinding();
        return new(
            binding.RuntimeInstanceId,
            registrationRevision ?? binding.RegistrationRevision,
            binding.ActorIssuer,
            binding.ActorId,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.StepId,
            binding.ProjectRevision,
            binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision,
            binding.ContextRevision,
            acceptedSelectionHash ?? binding.AcceptedSelectionHash,
            executionFence ?? binding.ExecutionFence,
            "grant-1",
            "revision-1");
    }
}
