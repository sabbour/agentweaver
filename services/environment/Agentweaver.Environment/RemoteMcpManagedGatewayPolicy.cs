using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class RemoteMcpManagedGatewayPolicy :
    IRemoteMcpManagedGatewayPolicy, IRemoteMcpManagedGatewayStateReader
{
    private sealed record ActiveState(
        RemoteMcpManagedGatewayGateDescriptor Descriptor,
        RemoteMcpManagedGatewayPolicyReceipt AppliedReceipt);

    private readonly object _sync = new();
    private readonly Dictionary<string, ActiveState> _active = new(StringComparer.Ordinal);
    private readonly Guid _handlerInstanceId = Guid.NewGuid();

    public Task<RemoteMcpManagedGatewayPolicyReceipt> ApplyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = RemoteMcpManagedGatewayContract.DescriptorHash(descriptor);
        lock (_sync)
        {
            if (_active.TryGetValue(hash, out var existing))
            {
                if (existing.Descriptor != descriptor)
                    throw new InvalidOperationException("A different Remote MCP gate descriptor has the same digest.");
                RemoteMcpManagedGatewayContract.ValidateReceipt(
                    descriptor, RemoteMcpManagedGatewayOperation.Apply, existing.AppliedReceipt);
                return Task.FromResult(existing.AppliedReceipt);
            }

            var receipt = Receipt(RemoteMcpManagedGatewayOperation.Apply, hash, RemoteMcpManagedGatewayGateState.Active);
            _active.Add(hash, new(descriptor, receipt));
            if (!_active.TryGetValue(hash, out existing) || existing.Descriptor != descriptor ||
                existing.AppliedReceipt != receipt)
                throw new InvalidOperationException("The Remote MCP gate descriptor did not become active.");
            return Task.FromResult(receipt);
        }
    }

    public Task<RemoteMcpManagedGatewayPolicyReceipt> VerifyAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = RemoteMcpManagedGatewayContract.DescriptorHash(descriptor);
        lock (_sync)
        {
            if (!_active.TryGetValue(hash, out var active) || active.Descriptor != descriptor)
                throw new InvalidOperationException("The exact Remote MCP gate descriptor is not active.");
            RemoteMcpManagedGatewayContract.ValidateReceipt(
                descriptor, RemoteMcpManagedGatewayOperation.Apply, active.AppliedReceipt);
            var receipt = Receipt(RemoteMcpManagedGatewayOperation.Verify, hash, RemoteMcpManagedGatewayGateState.Active);
            if (receipt.HandlerInstanceId != active.AppliedReceipt.HandlerInstanceId)
                throw new InvalidOperationException("The active Remote MCP gate handler instance changed.");
            return Task.FromResult(receipt);
        }
    }

    public Task<RemoteMcpManagedGatewayPolicyReceipt> RevokeAsync(
        RemoteMcpManagedGatewayGateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = RemoteMcpManagedGatewayContract.DescriptorHash(descriptor);
        lock (_sync)
        {
            if (_active.TryGetValue(hash, out var active) && active.Descriptor != descriptor)
                throw new InvalidOperationException("The exact Remote MCP gate descriptor changed before revocation.");
            _active.Remove(hash);
            if (_active.ContainsKey(hash))
                throw new InvalidOperationException("The Remote MCP gate descriptor remains active after revocation.");
            return Task.FromResult(Receipt(
                RemoteMcpManagedGatewayOperation.Revoke,
                hash,
                RemoteMcpManagedGatewayGateState.Revoked,
                active?.AppliedReceipt.HandlerInstanceId ?? _handlerInstanceId));
        }
    }

    public async Task<RemoteMcpManagedGatewayActiveState> ReadCurrentAsync(
        RemoteMcpNativeCallContext currentCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentCall);
        currentCall.RequireUsable();
        var proof = currentCall.Proof;
        var binding = currentCall.Registration.Binding;
        var expectedFence = new EnvironmentGenerationFence(
            new(proof.TenantId, proof.ProjectId, proof.RunId, binding.EnvironmentId),
            binding.EnvironmentLifecycleGeneration);
        ActiveState active;
        string descriptorHash;
        lock (_sync)
        {
            var matches = _active
                .Where(entry => entry.Value.Descriptor.SnapshotBinding == proof.SnapshotBinding &&
                    entry.Value.Descriptor.GatewayPolicy.AppliedPolicy.EnvironmentFence == expectedFence)
                .Select(entry => entry.Value)
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
                throw new RuntimeAuthorizationException("remote_mcp_managed_handler_unavailable");
            if (matches.Length != 1)
                throw new RuntimeAuthorizationException("remote_mcp_managed_handler_ambiguous");
            active = matches[0];
            descriptorHash = RemoteMcpManagedGatewayContract.DescriptorHash(active.Descriptor);
            RemoteMcpManagedGatewayContract.ValidateReceipt(
                active.Descriptor, RemoteMcpManagedGatewayOperation.Apply, active.AppliedReceipt);
        }

        var receipt = await VerifyAsync(active.Descriptor, cancellationToken).ConfigureAwait(false);
        currentCall.RequireUsable();
        lock (_sync)
        {
            if (!_active.TryGetValue(descriptorHash, out var current) || current != active ||
                current.AppliedReceipt.HandlerInstanceId != receipt.HandlerInstanceId)
                throw new RuntimeAuthorizationException("remote_mcp_managed_handler_changed");
        }

        return new(active.Descriptor, receipt);
    }

    private RemoteMcpManagedGatewayPolicyReceipt Receipt(
        RemoteMcpManagedGatewayOperation operation,
        string descriptorHash,
        RemoteMcpManagedGatewayGateState state,
        Guid? handlerInstanceId = null) =>
        new(1, operation, descriptorHash, handlerInstanceId ?? _handlerInstanceId, state);
}
