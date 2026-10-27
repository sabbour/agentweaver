using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

internal sealed record EnvironmentP1CreationRecord(
    EnvironmentGenerationFence Fence,
    P1InvocationBinding Binding,
    string State,
    EnvironmentP1CreationReceipt Receipt);

/// <summary>Reads retained P1 creation identities only under current runtime owner authority.</summary>
public sealed partial class EnvironmentP1CreationReader
{
    internal static EnvironmentP1CreationBinding Join(
        P1InvocationBinding binding,
        EnvironmentGenerationFence fence,
        string volumeId,
        ProviderResourceRef storage,
        long workspaceTransitionRevision,
        ProviderResourceRef sandbox,
        long providerFence,
        long leaseRevision,
        SandboxPodObservation pod,
        IEnumerable<EnvironmentP1CreationRecord> retained,
        DateTimeOffset now)
    {
        var records = retained.ToArray();
        var environment = Require("Environment", fence.Owner.EnvironmentId);
        var metadata = Require("WorkspaceVolumeMetadata", volumeId);
        var volume = Require("WorkspaceVolume", volumeId, storage.Generation);
        var provision = Require("Sandbox", pod.ProvisionOperationId.ToString("D"), sandbox.Generation);
        if (binding.ContractVersion != 1 || binding.TenantId != fence.Owner.TenantId ||
            binding.ProjectId != fence.Owner.ProjectId || binding.RunId != fence.Owner.RunId ||
            pod.EnvironmentFence != fence || pod.Resource != sandbox ||
            pod.FencingGeneration != providerFence || pod.ProvisionOperationId != provision.OperationId ||
            volume.ProviderResource != storage || provision.ProviderResource != sandbox ||
            provision.ProviderFencingGeneration != providerFence ||
            provision.LeaseRevision is not > 0 || provision.LeaseRevision > leaseRevision ||
            volume.TransitionRevision is not > 0 || volume.TransitionRevision > workspaceTransitionRevision ||
            metadata.ResourceGeneration != 0 || metadata.TransitionRevision is not > 0 ||
            metadata.TransitionRevision >= volume.TransitionRevision ||
            environment.CreatedAt > metadata.CreatedAt || metadata.CreatedAt > volume.CreatedAt ||
            volume.CompletedAt > provision.CreatedAt)
            throw Denied();
        return new(1, binding, fence, environment, metadata, volume, provision, pod);

        EnvironmentP1CreationReceipt Require(string kind, string resourceId, long? generation = null)
        {
            var matches = records.Where(record =>
                record.Fence == fence && SameBinding(record.Binding, binding) &&
                record.Receipt.ResourceKind == kind && record.Receipt.ResourceId == resourceId &&
                (generation is null || record.Receipt.ResourceGeneration == generation)).ToArray();
            if (matches.Length != 1 || matches[0].State != "Completed")
                throw Denied();
            var receipt = matches[0].Receipt;
            if (receipt.OperationId == Guid.Empty || receipt.CreatedAt.UtcTicks / 10 < binding.AcceptedAt.UtcTicks / 10 ||
                receipt.CompletedAt < receipt.CreatedAt || receipt.CompletedAt > now)
                throw Denied();
            return receipt;
        }
    }

    internal static bool SameBinding(P1InvocationBinding left, P1InvocationBinding right) =>
        left with { AcceptedAt = default } == right with { AcceptedAt = default } &&
        left.AcceptedAt.UtcTicks / 10 == right.AcceptedAt.UtcTicks / 10;

    private static RuntimeAuthorizationException Denied() => new("p1_creation_link_not_retained");
}
