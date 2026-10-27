namespace Agentweaver.Abstractions;

/// <summary>Observed Pod/PVC configuration, not proof of a successful mount inside the Host.</summary>
public sealed record SandboxPodWorkspaceAttachment(
    string Namespace,
    string ClaimName,
    string ClaimUid,
    string MountPath,
    bool ReadOnly);

/// <summary>
/// Current Kubernetes Pod identity and configuration observed by the Sandbox provider.
/// This is neither an invocation ownership receipt nor Host filesystem/isolation evidence.
/// </summary>
public sealed record SandboxPodObservation(
    int ContractVersion,
    DateTimeOffset ObservedAt,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long FencingGeneration,
    Guid ProvisionOperationId,
    string ClusterIdentity,
    string Namespace,
    string ClaimUid,
    string SandboxUid,
    string PodName,
    string PodUid,
    string PodResourceVersion,
    long PodGeneration,
    string PodPhase,
    SandboxPodWorkspaceAttachment? WorkspaceAttachment,
    bool IsolationConfigurationVerified)
{
    /// <summary>Rejects missing, foreign, or inconsistent resource, owner, and configuration observations.</summary>
    public SandboxPodObservation ValidateFor(SandboxDescribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (ContractVersion != 1 || ObservedAt == default || ObservedAt < request.LeaseCreatedAt ||
            EnvironmentFence != request.Fence || Resource != request.Resource ||
            FencingGeneration != request.FencingGeneration || ProvisionOperationId == Guid.Empty ||
            ClusterIdentity is null || ClusterIdentity.Length != 64 ||
            ClusterIdentity.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            !Valid(Namespace) || !Valid(ClaimUid) || ClaimUid != Resource.ResourceId ||
            !Valid(SandboxUid) || !Valid(PodName) || !Valid(PodUid) ||
            !Valid(PodResourceVersion) || PodGeneration < 1 ||
            PodPhase is not ("Pending" or "Running" or "Succeeded" or "Failed" or "Unknown") ||
            WorkspaceAttachment is { } attachment &&
                (attachment.Namespace != Namespace || !Valid(attachment.ClaimName) ||
                 !Valid(attachment.ClaimUid) || !Valid(attachment.MountPath, 512) ||
                 !attachment.MountPath.StartsWith("/", StringComparison.Ordinal)))
            throw new ArgumentException("The Sandbox Pod observation does not match its exact pinned request.");
        return this;
    }

    private static bool Valid(string? value, int maximumLength = 256) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);
}
