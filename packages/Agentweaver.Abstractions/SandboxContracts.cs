using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

public static class SandboxCapabilities
{
    public const string VmIsolation = "sandbox.isolation.vm";
    public const string WorkspacePersistentVolumeClaim = "sandbox.workspace.pvc";
    public const string VerifiedNetworkPolicy = "sandbox.network-policy.verified";
}

public static class SandboxResourceIdentity
{
    public static ProviderResourceRef CreatePlannedReference(
        string providerId,
        string optionsRevision,
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        long fencingGeneration,
        Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (string.IsNullOrWhiteSpace(providerId) ||
            providerId.Length > 128 ||
            providerId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(optionsRevision) ||
            optionsRevision.Length > 128 ||
            optionsRevision.Any(char.IsControl) ||
            resourceGeneration < 1 ||
            fencingGeneration < 1 ||
            operationId == Guid.Empty)
            throw new ArgumentException("The planned sandbox resource identity is invalid.");

        var owner = fence.Owner;
        var identity = string.Join(
            "\0",
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            fence.LifecycleGeneration,
            resourceGeneration,
            fencingGeneration,
            operationId.ToString("N"),
            providerId,
            optionsRevision);
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return new ProviderResourceRef(
            ProviderSeam.Sandbox,
            providerId,
            $"aw-claim-{digest[..40]}",
            resourceGeneration);
    }
}

public enum SandboxLeaseState
{
    Provisioning,
    Active,
    Releasing,
    Released,
    ReconciliationRequired,
    Failed
}

public enum SandboxOperationState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Failed,
    Stale
}

public enum SandboxProviderOperation
{
    Provision,
    Release
}

public enum SandboxObservedState
{
    Pending,
    Ready,
    Failed,
    Finished,
    Absent
}

public enum SandboxStartupPhase
{
    Scheduled,
    ImageReady,
    Started,
    Configured,
    Ready
}

public sealed record SandboxStartupPhaseObservation
{
    private static readonly Regex Sha256Digest = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SandboxStartupPhaseObservation(
        SandboxStartupPhase phase,
        int contractVersion,
        DateTimeOffset observedAt,
        string? imageDigest = null,
        long? compressedPullBytes = null)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (contractVersion != 1)
            throw new ArgumentOutOfRangeException(nameof(contractVersion));
        if (observedAt <= DateTimeOffset.MinValue)
            throw new ArgumentOutOfRangeException(nameof(observedAt));
        if (phase == SandboxStartupPhase.ImageReady)
        {
            if (imageDigest is null || !Sha256Digest.IsMatch(imageDigest) || compressedPullBytes is not > 0)
                throw new ArgumentException("Image-ready evidence requires the selected OCI digest and compressed pull bytes.");
        }
        else if (imageDigest is not null || compressedPullBytes is not null)
        {
            throw new ArgumentException("OCI image evidence is valid only for the image-ready phase.");
        }
        Phase = phase;
        ContractVersion = contractVersion;
        ObservedAt = observedAt;
        ImageDigest = imageDigest;
        CompressedPullBytes = compressedPullBytes;
    }

    public SandboxStartupPhase Phase { get; }
    public int ContractVersion { get; }
    public DateTimeOffset ObservedAt { get; }
    public string? ImageDigest { get; }
    public long? CompressedPullBytes { get; }
}

public sealed record SandboxOperationReference(Guid Value)
{
    public SandboxOperationReference Validate()
    {
        if (Value == Guid.Empty)
            throw new ArgumentException("A non-empty opaque sandbox operation reference is required.", nameof(Value));
        return this;
    }
}

public sealed record SandboxEndpointReference(Guid Value)
{
    public SandboxEndpointReference Validate()
    {
        if (Value == Guid.Empty)
            throw new ArgumentException("A non-empty opaque sandbox endpoint reference is required.", nameof(Value));
        return this;
    }
}

public sealed record SandboxPlacementReference(string Value)
{
    public SandboxPlacementReference Validate()
    {
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > 128 ||
            Value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException("A bounded opaque sandbox placement reference is required.", nameof(Value));
        return this;
    }
}

public sealed record SandboxProviderBindingSnapshot(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    JsonElement OptionsSnapshot,
    JsonElement ReleaseDescriptor)
{
    public SandboxProviderBindingSnapshot ValidateFor(ProviderResourceRef resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(resource.ProviderId) ||
            !string.Equals(ProviderId, resource.ProviderId, StringComparison.Ordinal) ||
            !Version.TryParse(AdapterVersion, out var version) ||
            version.Major < 1 ||
            !string.Equals(version.ToString(), AdapterVersion, StringComparison.Ordinal) ||
            OptionsSchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(OptionsRevision) ||
            OptionsRevision.Length > 128 ||
            OptionsRevision.Any(char.IsControl) ||
            OptionsSnapshot.ValueKind != JsonValueKind.Object ||
            ReleaseDescriptor.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The sandbox provider binding is invalid or does not match its resource.");
        return this with
        {
            OptionsSnapshot = OptionsSnapshot.Clone(),
            ReleaseDescriptor = ReleaseDescriptor.Clone()
        };
    }
}

public sealed record SandboxWorkspaceAttachment(
    WorkspaceVolumeAttachmentNegotiation Negotiation,
    JsonElement ProviderAttachmentDescriptor)
{
    public SandboxWorkspaceAttachment Validate()
    {
        ArgumentNullException.ThrowIfNull(Negotiation);
        Negotiation.Validate();
        if (ProviderAttachmentDescriptor.ValueKind != JsonValueKind.Object ||
            ProviderAttachmentDescriptor.GetRawText().Length > 8192)
            throw new ArgumentException("A bounded provider-owned workspace attachment descriptor is required.",
                nameof(ProviderAttachmentDescriptor));
        return this with { ProviderAttachmentDescriptor = ProviderAttachmentDescriptor.Clone() };
    }
}

public sealed record SandboxProvisionRequest(
    EnvironmentGenerationFence Fence,
    ProviderCandidate Candidate,
    long ResourceGeneration,
    long FencingGeneration,
    Guid OperationId,
    ImmutableDictionary<string, string> EgressSelectorLabels,
    SandboxWorkspaceAttachment Workspace)
{
    public SandboxProvisionRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(Candidate);
        ArgumentNullException.ThrowIfNull(EgressSelectorLabels);
        ArgumentNullException.ThrowIfNull(Workspace);
        Workspace.Validate();
        if (Candidate.Seam != ProviderSeam.Sandbox ||
            ResourceGeneration < 1 ||
            FencingGeneration < 1 ||
            OperationId == Guid.Empty ||
            EgressSelectorLabels.Count == 0 ||
            EgressSelectorLabels.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value) ||
                pair.Key.Length > 253 ||
                pair.Value.Length > 63) ||
            !string.Equals(Workspace.Negotiation.ProjectId, Fence.Owner.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(Workspace.Negotiation.RunId, Fence.Owner.RunId, StringComparison.Ordinal) ||
            !string.Equals(Workspace.Negotiation.EnvironmentId, Fence.Owner.EnvironmentId, StringComparison.Ordinal) ||
            Workspace.Negotiation.EnvironmentFence != Fence ||
            Workspace.Negotiation.SandboxResource != SandboxResourceIdentity.CreatePlannedReference(
                Candidate.ProviderId,
                Candidate.OptionsRevision,
                Fence,
                ResourceGeneration,
                FencingGeneration,
                OperationId))
            throw new ArgumentException("The sandbox provision request is invalid.");
        return this with
        {
            EgressSelectorLabels = EgressSelectorLabels.ToImmutableDictionary(StringComparer.Ordinal)
        };
    }
}

public sealed record SandboxDescribeRequest(
    EnvironmentGenerationFence Fence,
    ProviderResourceRef Resource,
    long FencingGeneration,
    SandboxProviderBindingSnapshot ProviderBinding,
    DateTimeOffset LeaseCreatedAt)
{
    public SandboxDescribeRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(ProviderBinding);
        if (FencingGeneration < 1 || LeaseCreatedAt <= DateTimeOffset.MinValue ||
            Resource.Seam != ProviderSeam.Sandbox ||
            Resource.Generation < 1)
            throw new ArgumentOutOfRangeException(nameof(FencingGeneration));
        _ = ProviderBinding.ValidateFor(Resource);
        return this;
    }
}

public sealed record SandboxListOwnedRequest(
    EnvironmentGenerationFence Fence,
    long MinimumFencingGeneration,
    SandboxLeaseProvisionIntent ProvisionIntent,
    DateTimeOffset LeaseCreatedAt)
{
    public SandboxListOwnedRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(ProvisionIntent);
        _ = ProvisionIntent.Validate();
        if (MinimumFencingGeneration < 1 || LeaseCreatedAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("A valid fencing generation and persisted lease creation time are required.");
        return this;
    }
}

public sealed record SandboxReleaseRequest(
    EnvironmentGenerationFence Fence,
    ProviderResourceRef Resource,
    long FencingGeneration,
    SandboxProviderBindingSnapshot ProviderBinding,
    string IdempotencyKey)
{
    public SandboxReleaseRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(ProviderBinding);
        if (FencingGeneration < 1 || Resource.Seam != ProviderSeam.Sandbox ||
            Resource.Generation < 1 ||
            string.IsNullOrWhiteSpace(IdempotencyKey) ||
            IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("The sandbox release request is invalid.");
        _ = ProviderBinding.ValidateFor(Resource);
        return this;
    }
}

public sealed record SandboxProvisionedResource(
    ProviderResourceRef Resource,
    SandboxEndpointReference Endpoint,
    SandboxPlacementReference Placement,
    ImmutableHashSet<string> NegotiatedCapabilities,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxProviderBindingSnapshot ProviderBinding)
{
    public SandboxProvisionedResource Validate()
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentNullException.ThrowIfNull(Placement);
        ArgumentNullException.ThrowIfNull(NegotiatedCapabilities);
        ArgumentNullException.ThrowIfNull(ProviderBinding);
        Endpoint.Validate();
        Placement.Validate();
        if (Resource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation < 1 ||
            NegotiatedCapabilities.Count == 0 ||
            NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            StartupPhases.IsDefault ||
            StartupPhases.Any(phase => phase is null || phase.ContractVersion != 1))
            throw new ArgumentException("The provisioned sandbox resource is invalid.");
        _ = ProviderBinding.ValidateFor(Resource);
        return this with
        {
            NegotiatedCapabilities = NegotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal),
            StartupPhases = StartupPhases.ToImmutableArray()
        };
    }
}

public sealed record SandboxObservation(
    ProviderResourceRef Resource,
    SandboxObservedState State,
    long FencingGeneration,
    bool VmIsolationVerified,
    bool WorkspaceAttachmentVerified,
    long? VerifiedNetworkGeneration,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxTerminalEvidence? TerminalEvidence = null,
    Guid? ProvisionOperationId = null,
    SandboxProvisionedResource? ProvisionedResource = null,
    SandboxStartupBudgetFailure? StartupFailure = null)
{
    public SandboxObservation ValidateFor(SandboxDescribeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (Resource != request.Resource ||
            !Enum.IsDefined(State) ||
            FencingGeneration != request.FencingGeneration ||
            StartupPhases.IsDefault ||
            StartupPhases.Any(phase => phase is null || phase.ContractVersion != 1) ||
            (VerifiedNetworkGeneration is < 1) ||
            (State == SandboxObservedState.Finished) != (TerminalEvidence is not null) ||
            TerminalEvidence is not null &&
                (TerminalEvidence.ClaimUid != request.Resource.ResourceId ||
                 TerminalEvidence.FencingGeneration != request.FencingGeneration) ||
            StartupFailure is not null &&
                (State != SandboxObservedState.Failed || StartupFailure.Validate() != StartupFailure) ||
            ProvisionOperationId is { } operationId && operationId == Guid.Empty ||
            ProvisionedResource is not null &&
                (ProvisionedResource.Resource != request.Resource ||
                 !SameProviderBinding(ProvisionedResource.ProviderBinding, request.ProviderBinding)) ||
            State == SandboxObservedState.Ready &&
                (!VmIsolationVerified ||
                 !WorkspaceAttachmentVerified ||
                 VerifiedNetworkGeneration is null ||
                 !StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Configured) ||
                 !StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Ready)))
            throw new ArgumentException("The sandbox observation does not match its exact pinned request.");
        return this with { StartupPhases = StartupPhases.ToImmutableArray() };
    }

    private static bool SameProviderBinding(
        SandboxProviderBindingSnapshot left,
        SandboxProviderBindingSnapshot right) =>
        left.ProviderId == right.ProviderId &&
        left.AdapterVersion == right.AdapterVersion &&
        left.OptionsSchemaVersion == right.OptionsSchemaVersion &&
        left.OptionsRevision == right.OptionsRevision &&
        JsonNode.DeepEquals(
            JsonNode.Parse(left.OptionsSnapshot.GetRawText()),
            JsonNode.Parse(right.OptionsSnapshot.GetRawText())) &&
        JsonNode.DeepEquals(
            JsonNode.Parse(left.ReleaseDescriptor.GetRawText()),
            JsonNode.Parse(right.ReleaseDescriptor.GetRawText()));
}

public enum SandboxStartupBudgetFailureKind
{
    PhaseExceeded,
    TotalExceeded
}

public sealed record SandboxStartupBudgetFailure(
    SandboxStartupBudgetFailureKind Kind,
    SandboxStartupPhase Phase,
    int BudgetSeconds,
    DateTimeOffset Deadline,
    DateTimeOffset ObservedAt)
{
    public SandboxStartupBudgetFailure Validate()
    {
        if (!Enum.IsDefined(Kind) ||
            !Enum.IsDefined(Phase) ||
            BudgetSeconds <= 0 ||
            Deadline <= DateTimeOffset.MinValue ||
            ObservedAt <= Deadline)
            throw new ArgumentException("The Sandbox startup budget failure is invalid.");
        return this;
    }
}

public enum SandboxTerminalReason
{
    PodSucceeded,
    PodFailed
}

public sealed record SandboxTerminalEvidence(
    int ContractVersion,
    string ClaimUid,
    string SandboxUid,
    long FencingGeneration,
    long SandboxObservedGeneration,
    SandboxTerminalReason Reason,
    DateTimeOffset ObservedAt)
{
    public SandboxTerminalEvidence Validate()
    {
        if (ContractVersion != 1 ||
            string.IsNullOrWhiteSpace(ClaimUid) ||
            string.IsNullOrWhiteSpace(SandboxUid) ||
            FencingGeneration < 1 ||
            SandboxObservedGeneration < 1 ||
            !Enum.IsDefined(Reason) ||
            ObservedAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("The sandbox terminal evidence is invalid.");
        return this;
    }
}

public enum SandboxReleaseDisposition
{
    Released,
    KnownOwnedAbsent
}

public sealed record SandboxReleaseReceipt(
    ProviderResourceRef Resource,
    string IdempotencyKey,
    SandboxReleaseDisposition Disposition)
{
    public SandboxReleaseReceipt ValidateFor(SandboxReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (Resource != request.Resource ||
            !string.Equals(IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !Enum.IsDefined(Disposition))
            throw new ArgumentException(
                "A sandbox release receipt must identify the exact resource, operation and outcome.");
        return this;
    }
}

public sealed record SandboxPartialReleaseRequest(
    EnvironmentGenerationFence Fence,
    SandboxLeaseSnapshot Lease)
{
    public SandboxPartialReleaseRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentNullException.ThrowIfNull(Lease);
        Lease.Validate();
        if (Lease.Fence != Fence ||
            !Lease.IsCurrent ||
            Lease.State is not (SandboxLeaseState.Releasing or SandboxLeaseState.ReconciliationRequired) ||
            Lease.RetirementReason != SandboxRetirementReason.AuthorizedAbandon ||
            Lease.ProvisionedResource is not null ||
            Lease.ProviderRequestFingerprint is null ||
            Lease.ReleaseIdempotencyKey is null ||
            Lease.CurrentFencingGeneration <= Lease.ProviderFencingGeneration)
            throw new ArgumentException(
                "A partial Sandbox release requires the exact current authorized-retirement lease.",
                nameof(Lease));
        return this;
    }
}

public sealed record SandboxPartialReleaseReceipt(
    Guid OperationId,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    long CurrentFencingGeneration,
    long LeaseRevision,
    string IdempotencyKey,
    string KubernetesNamespace,
    string ClaimName,
    string TemplateName,
    string? TemplateUid,
    string WarmPoolName,
    string? WarmPoolUid,
    bool ClaimAbsent,
    bool SandboxesAbsent,
    bool PodsAbsent,
    SandboxReleaseDisposition Disposition)
{
    public SandboxPartialReleaseReceipt ValidateFor(SandboxPartialReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var lease = request.Lease;
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            lease.ProvisionIntent.ProviderId,
            lease.ProvisionIntent.OptionsRevision,
            lease.Fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId);
        var suffix = planned.ResourceId["aw-claim-".Length..];
        if (OperationId != lease.OperationId ||
            ResourceGeneration != lease.ResourceGeneration ||
            ProviderFencingGeneration != lease.ProviderFencingGeneration ||
            CurrentFencingGeneration != lease.CurrentFencingGeneration ||
            LeaseRevision != lease.LeaseRevision ||
            !string.Equals(IdempotencyKey, lease.ReleaseIdempotencyKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(KubernetesNamespace) ||
            KubernetesNamespace.Length > 63 ||
            KubernetesNamespace.Any(char.IsControl) ||
            ClaimName != planned.ResourceId ||
            TemplateName != $"aw-template-{suffix}" ||
            WarmPoolName != $"aw-pool-{suffix}" ||
            !IsValidUid(TemplateUid) ||
            !IsValidUid(WarmPoolUid) ||
            !ClaimAbsent ||
            !SandboxesAbsent ||
            !PodsAbsent ||
            Disposition != (TemplateUid is null && WarmPoolUid is null
                ? SandboxReleaseDisposition.KnownOwnedAbsent
                : SandboxReleaseDisposition.Released))
            throw new ArgumentException(
                "The partial Sandbox release receipt does not prove exact owner-fenced resource retirement.",
                nameof(request));
        return this;
    }

    private static bool IsValidUid(string? value) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(char.IsControl);
}

public interface ISandboxProvider
{
    Task<SandboxProvisionedResource> ProvisionAsync(
        SandboxProvisionRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxObservation> DescribeAsync(
        SandboxDescribeRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
        SandboxListOwnedRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxReleaseReceipt> ReleaseAsync(
        SandboxReleaseRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
        SandboxPartialReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public enum SandboxRetirementReason
{
    Finished,
    AuthorizedAbandon
}

public sealed record SandboxLeaseProvisionIntent(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    JsonElement OptionsSnapshot,
    JsonElement SelectionSnapshot,
    JsonElement ProviderRequest)
{
    public SandboxLeaseProvisionIntent Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 128 ||
            ProviderId.Any(char.IsControl) ||
            !Version.TryParse(AdapterVersion, out var adapterVersion) ||
            adapterVersion.Major < 1 ||
            !string.Equals(adapterVersion.ToString(), AdapterVersion, StringComparison.Ordinal) ||
            OptionsSchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(OptionsRevision) || OptionsRevision.Length > 128 ||
            OptionsRevision.Any(char.IsControl) ||
            !IsBoundedObject(OptionsSnapshot, 16_384) ||
            !IsBoundedObject(SelectionSnapshot, 65_536) ||
            !IsBoundedObject(ProviderRequest, 32_768))
            throw new ArgumentException("The sandbox lease provision intent is invalid.");
        return this with
        {
            OptionsSnapshot = OptionsSnapshot.Clone(),
            SelectionSnapshot = SelectionSnapshot.Clone(),
            ProviderRequest = ProviderRequest.Clone()
        };
    }

    private static bool IsBoundedObject(JsonElement value, int maxLength) =>
        value.ValueKind == JsonValueKind.Object && value.GetRawText().Length <= maxLength;
}

public sealed record SandboxLeaseSnapshot(
    EnvironmentGenerationFence Fence,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    long CurrentFencingGeneration,
    Guid OperationId,
    SandboxLeaseState State,
    SandboxLeaseProvisionIntent ProvisionIntent,
    SandboxProvisionedResource? ProvisionedResource,
    SandboxRetirementReason? RetirementReason,
    SandboxTerminalEvidence? TerminalEvidence,
    string? RetiringActorId,
    long? RetiringMembershipRevision,
    string? ReleaseIdempotencyKey,
    bool IsCurrent,
    DateTimeOffset UpdatedAt)
{
    public string? RetirementFingerprint { get; init; }
    public string? RetiringIssuer { get; init; }
    public string? ProviderRequestFingerprint { get; init; }
    public long LeaseRevision { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public SandboxPartialReleaseReceipt? PartialReleaseReceipt { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    public SandboxLeaseSnapshot Validate()
    {
        var terminal = State is SandboxLeaseState.Released or SandboxLeaseState.Failed;
        if (LeaseRevision < 1 ||
            (terminal && LeaseExpiresAt is not null) ||
            (!terminal && LeaseExpiresAt is null) ||
            LeaseExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.MinValue ||
            PartialReleaseReceipt is not null &&
                State is not (SandboxLeaseState.Released or SandboxLeaseState.ReconciliationRequired))
            throw new ArgumentException("The Sandbox lease revision or expiry is invalid.");
        return this;
    }
}

public sealed record SandboxLateResourceCleanupClaim(
    SandboxLeaseSnapshot Lease,
    SandboxProvisionedResource ProvisionedResource,
    string IdempotencyKey,
    Guid ClaimToken,
    DateTimeOffset ClaimExpiresAt)
{
    public SandboxLateResourceCleanupClaim Validate()
    {
        ArgumentNullException.ThrowIfNull(Lease);
        ArgumentNullException.ThrowIfNull(ProvisionedResource);
        _ = Lease.Validate();
        _ = ProvisionedResource.Validate();
        if (Lease.IsCurrent ||
            Lease.State is not (SandboxLeaseState.Released or SandboxLeaseState.Failed) ||
            ProvisionedResource.Resource.Generation != Lease.ResourceGeneration ||
            string.IsNullOrWhiteSpace(IdempotencyKey) ||
            IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(char.IsControl) ||
            ClaimToken == Guid.Empty ||
            ClaimExpiresAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("The late Sandbox resource cleanup claim is invalid.");
        return this;
    }
}

public sealed record SandboxPartialReleaseCompletionRequest(
    EnvironmentGenerationFence Fence,
    SandboxPartialReleaseReceipt Receipt);

public sealed record SandboxLeaseReservation(SandboxLeaseSnapshot Lease, bool Replayed);

public sealed record SandboxRetirementAuthorization(
    string Issuer,
    string ActorId,
    long MembershipRevision)
{
    public SandboxRetirementAuthorization Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || Issuer.Length > 512 || Issuer.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(ActorId) || ActorId.Length > 256 || ActorId.Any(char.IsControl) ||
            MembershipRevision < 1)
            throw new ArgumentException("Current Projects authorization evidence is invalid.");
        return this;
    }
}

public interface ISandboxLeaseStore
{
    Task<SandboxLeaseReservation> ReserveProvisionAsync(
        EnvironmentGenerationFence fence,
        string idempotencyKey,
        SandboxLeaseProvisionIntent intent,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot?> GetCurrentAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs an owner-scoped read while retaining the active Environment owner lock.
    /// The callback must not recursively call this reader or perform provider effects.
    /// </summary>
    Task<TResult> GetCurrentAsync<TResult>(
        EnvironmentGenerationFence fence,
        Func<SandboxLeaseSnapshot?, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot?> GetAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        JsonElement providerRequest,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource? provisionedResource,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot> BeginRetirementAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        long providerFencingGeneration,
        SandboxRetirementReason reason,
        string idempotencyKey,
        SandboxRetirementAuthorization? authorization,
        SandboxTerminalEvidence? terminalEvidence,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot> CompleteReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        long providerFencingGeneration,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken);

    Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
        SandboxPartialReleaseCompletionRequest request,
        CancellationToken cancellationToken);

    Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    Task CompleteLateResourceCleanupAsync(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim claim,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken);
}

public sealed class SandboxProviderException(
    string code,
    string message,
    bool effectMayHaveApplied,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("A safe sandbox provider error code is required.", nameof(code))
        : code;

    public bool EffectMayHaveApplied { get; } = effectMayHaveApplied;
}
