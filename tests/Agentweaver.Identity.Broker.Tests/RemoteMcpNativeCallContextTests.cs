using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpNativeCallContextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T00:00:00Z");

    [Fact]
    public void VerifiedContextIsNotSerializableAsCallerAuthority()
    {
        var context = Create();

        context.RequireUsable();
        Assert.Equal("{}", JsonSerializer.Serialize(context));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<RemoteMcpNativeCallContext>("{}"));
        Assert.Equal(Proof().NativeCallId, context.Proof.NativeCallId);
        Assert.Equal(Proof().ActorId, context.Registration.Binding.ActorId);
    }

    [Theory]
    [InlineData("anonymous", "remote_mcp_native_actor_invalid")]
    [InlineData("subject", "remote_mcp_native_actor_invalid")]
    [InlineData("expiry", "remote_mcp_native_actor_expired")]
    [InlineData("revoked", "remote_mcp_native_call_binding_invalid")]
    [InlineData("registration", "remote_mcp_native_call_binding_invalid")]
    [InlineData("run", "remote_mcp_native_call_binding_invalid")]
    [InlineData("verification", "remote_mcp_native_call_binding_invalid")]
    public void UnverifiedOrDifferentActorRegistrationAndReceiptCannotCreateContext(string failure, string code)
    {
        var error = Assert.Throws<RuntimeAuthorizationException>(() => Create(failure));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void ContextCannotSurviveActorOrRegistrationExpiry()
    {
        var clock = new Clock(Now);
        var context = Create(clock: clock);
        clock.Now = Now.AddMinutes(3);

        Assert.Equal("remote_mcp_native_actor_expired",
            Assert.Throws<RuntimeAuthorizationException>(context.RequireUsable).Code);
    }

    [Theory]
    [InlineData(RemoteMcpManagedGatewayOperation.Apply)]
    [InlineData(RemoteMcpManagedGatewayOperation.Verify)]
    [InlineData(RemoteMcpManagedGatewayOperation.Revoke)]
    public void GateReceiptBindsAllDescriptorPinsAndExactHandlerOperation(RemoteMcpManagedGatewayOperation operation)
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var descriptor = RemoteMcpManagedGatewayContract.CreateDescriptor(request,
            RemoteMcpCurrentCallVerificationClientTests.Acknowledgment(request));
        var receipt = new RemoteMcpManagedGatewayPolicyReceipt(1, operation,
            RemoteMcpManagedGatewayContract.DescriptorHash(descriptor), Guid.NewGuid(),
            operation == RemoteMcpManagedGatewayOperation.Revoke
                ? RemoteMcpManagedGatewayGateState.Revoked : RemoteMcpManagedGatewayGateState.Active);

        RemoteMcpManagedGatewayContract.ValidateReceipt(descriptor, operation, receipt);
        Assert.Throws<ArgumentException>(() => RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor, operation, receipt with { HandlerInstanceId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { ConnectionRowRevision = 99 } },
            operation, receipt));
        Assert.Throws<ArgumentException>(() => RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { CatalogDigest = Hash("changed") } },
            operation, receipt));
        Assert.Throws<ArgumentException>(() => RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor with
            {
                GatewayPolicy = descriptor.GatewayPolicy with
                {
                    PolicyResource = descriptor.GatewayPolicy.PolicyResource with { Generation = 99 }
                }
            }, operation, receipt));
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("configuration")]
    [InlineData("catalog")]
    [InlineData("target")]
    public void GateDescriptorRequiresCompleteCurrentPins(string failure)
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var descriptor = RemoteMcpManagedGatewayContract.CreateDescriptor(request,
            RemoteMcpCurrentCallVerificationClientTests.Acknowledgment(request));
        descriptor = failure switch
        {
            "discovery" => descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { ConnectionDiscoveryRevision = null } },
            "configuration" => descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { ConfigurationSha256 = "" } },
            "catalog" => descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { CatalogRevision = "" } },
            _ => descriptor with { GatewayPolicy = descriptor.GatewayPolicy with { TargetEndpointSha256 = Hash("another") } }
        };

        Assert.Throws<ArgumentException>(() => RemoteMcpManagedGatewayContract.Validate(descriptor));
    }

    private static RemoteMcpNativeCallContext Create(string? failure = null, Clock? clock = null)
    {
        var proof = Proof();
        var identity = new ClaimsIdentity(
            [new("sub", failure == "subject" ? Guid.NewGuid().ToString("D") : proof.ActorId),
             new("exp", (failure == "expiry" ? Now.AddSeconds(-1) : Now.AddMinutes(2)).ToUnixTimeSeconds().ToString())],
            failure == "anonymous" ? null : "validated-host");
        var registration = Registration(proof);
        if (failure == "revoked")
            registration = registration with { State = RuntimeRegistrationState.Revoked };
        else if (failure == "registration")
            registration = registration with { Revision = 99 };
        else if (failure == "run")
            registration = registration with { Binding = registration.Binding with { RunId = "another-run" } };
        var receipt = new RemoteToolResultVerificationReceipt(1,
            SessionMaterialValidation.RemoteToolResultEventId(proof),
            failure == "verification" ? Hash("another") : SessionMaterialValidation.RemoteToolResultProofHash(proof));
        var time = clock ?? new Clock(Now);
        return RemoteMcpNativeCallContext.CreateVerified(new(identity), proof.ActorIssuer, proof, registration, receipt,
            new(new SecretCredential("original-actor-token", Now.AddMinutes(2), time), proof.TenantId), time);
    }

    private static RuntimeRegistration Registration(RemoteToolResultProof proof) =>
        new(proof.RuntimeInstanceId, proof.RegistrationRevision,
            new(proof.ActorIssuer, proof.ActorId, proof.TenantId, proof.ProjectId, proof.RunId,
                proof.SessionId, proof.Snapshot.AgentId, "turn-1", proof.ProjectRevision,
                proof.ProjectConfigurationRevision, proof.PlatformRuntimeRevision, proof.ContextRevision,
                proof.AcceptedSelectionHash, proof.ExecutionFence, "environment-1", "placement-1", 1, "profile-1",
                new("https://sandbox.test/configure"), new("https://sandbox.test/observe"))
            { WorkflowStepId = proof.StepId },
            RuntimeRegistrationState.Active, Now.AddMinutes(2));

    private static RemoteToolResultProof Proof() => RemoteMcpCurrentCallVerificationClientTests.Proof();
    private static string Hash(string text) => RemoteMcpCurrentCallVerificationClientTests.Hash(text);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
