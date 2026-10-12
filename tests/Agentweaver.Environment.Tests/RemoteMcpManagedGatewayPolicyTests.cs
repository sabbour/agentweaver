using Agentweaver.Abstractions;
using Agentweaver.Environment;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpManagedGatewayPolicyTests
{
    [Fact]
    public async Task AppliesVerifiesAndRevokesOnlyTheExactDescriptor()
    {
        var policy = new RemoteMcpManagedGatewayPolicy();
        var descriptor = CreateDescriptor(new string('a', 64));
        var other = CreateDescriptor(new string('b', 64));

        var applied = await policy.ApplyAsync(descriptor, CancellationToken.None);
        RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor, RemoteMcpManagedGatewayOperation.Apply, applied);
        var verified = await policy.VerifyAsync(descriptor, CancellationToken.None);
        RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor, RemoteMcpManagedGatewayOperation.Verify, verified);
        Assert.Equal(applied.HandlerInstanceId, verified.HandlerInstanceId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.VerifyAsync(other, CancellationToken.None));

        var revoked = await policy.RevokeAsync(descriptor, CancellationToken.None);
        RemoteMcpManagedGatewayContract.ValidateReceipt(
            descriptor, RemoteMcpManagedGatewayOperation.Revoke, revoked);
        Assert.Equal(applied.HandlerInstanceId, revoked.HandlerInstanceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.VerifyAsync(descriptor, CancellationToken.None));
    }

    [Fact]
    public async Task RevokeIsIdempotentAndDoesNotRemoveAnotherDescriptor()
    {
        var policy = new RemoteMcpManagedGatewayPolicy();
        var first = CreateDescriptor(new string('a', 64));
        var second = CreateDescriptor(new string('b', 64));
        _ = await policy.ApplyAsync(first, CancellationToken.None);
        _ = await policy.ApplyAsync(second, CancellationToken.None);

        _ = await policy.RevokeAsync(first, CancellationToken.None);
        _ = await policy.RevokeAsync(first, CancellationToken.None);
        _ = await policy.VerifyAsync(second, CancellationToken.None);
    }

    private static RemoteMcpManagedGatewayGateDescriptor CreateDescriptor(string catalogDigest)
    {
        const string endpoint = "https://mcp.example.test/mcp";
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var fence = new EnvironmentGenerationFence(
            new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a"),
            lifecycleGeneration: 3);
        var appliedPolicy = new RemoteMcpAppliedNetworkPolicyReference(
            "environment/policy-a", fence, 7, hash);
        var gatewayPolicy = new RemoteMcpCurrentGatewayPolicyBinding(
            appliedPolicy,
            new ProviderResourceRef(ProviderSeam.NetworkPolicy, "cilium", "namespace/policy-a", 7),
            "1.0.0",
            1,
            "options-revision-a",
            hash,
            hash,
            RemoteMcpDigest.Sha256(System.Text.Encoding.UTF8.GetBytes(endpoint)));
        var snapshotBinding = new RemoteToolResultSnapshotBinding(
            Guid.NewGuid().ToString("D"),
            11,
            5,
            2,
            "Enabled",
            hash,
            endpoint,
            endpoint,
            "None",
            null,
            "StreamableHttp20250618",
            null,
            null,
            null,
            "2",
            catalogDigest,
            "weather.forecast",
            hash,
            hash,
            "schema-a",
            hash,
            "tool.read",
            "runtime.execution",
            hash);
        return new(1, gatewayPolicy, snapshotBinding);
    }
}
