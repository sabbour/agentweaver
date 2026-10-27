using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentweaver.Abstractions;

/// <summary>Capabilities required by Sandbox provider selection and runtime validation.</summary>
public static class SandboxCapabilities
{
    /// <summary>Provider guarantees VM-level isolation.</summary>
    public const string VmIsolation = "sandbox.isolation.vm";
    /// <summary>Provider can attach a persistent workspace volume.</summary>
    public const string WorkspacePersistentVolumeClaim = "sandbox.workspace.pvc";
    /// <summary>Provider confirms the requested Network Policy generation was applied.</summary>
    public const string VerifiedNetworkPolicy = "sandbox.network-policy.verified";
    /// <summary>Provider can run a gated Build/Test command pod.</summary>
    public const string BuildTestCommandPod = "sandbox.command.build-test-pod";
}

/// <summary>Deterministically derives a planned Sandbox resource identity from owner and operation pins.</summary>
public static class SandboxResourceIdentity
{
    /// <summary>
    /// Creates a stable opaque claim reference for one provider, options revision, Environment
    /// lifecycle, resource/fencing generation, and operation.
    /// </summary>
    /// <exception cref="ArgumentException">An identity, generation, or operation ID is invalid.</exception>
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

/// <summary>Persisted lifecycle state of a Sandbox resource lease.</summary>
public enum SandboxLeaseState
{
    Provisioning,
    Active,
    Releasing,
    Released,
    ReconciliationRequired,
    Failed
}

/// <summary>Result state of a reserved Sandbox owner operation.</summary>
public enum SandboxOperationState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Failed,
    Stale
}

/// <summary>Provider effect requested for a Sandbox resource.</summary>
public enum SandboxProviderOperation
{
    Provision,
    Release
}

/// <summary>Provider-observed runtime state for a Sandbox resource.</summary>
public enum SandboxObservedState
{
    Pending,
    Ready,
    Failed,
    Finished,
    Absent
}

/// <summary>Ordered milestones observed while a Sandbox starts.</summary>
public enum SandboxStartupPhase
{
    Scheduled,
    ImageReady,
    Started,
    Configured,
    Ready
}

/// <summary>Immutable image pin including measured compressed bytes for startup budgets.</summary>
public sealed record SandboxImageIdentity(string Digest, string Platform, long CompressedPullBytes)
{
    /// <summary>Requires a lowercase SHA-256 linux/amd64 image and positive measured compressed size.</summary>
    /// <exception cref="ArgumentException">The image digest, platform, or size is invalid.</exception>
    public SandboxImageIdentity Validate()
    {
        if (Digest is not { Length: 71 } ||
            !Digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            Digest[7..].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            Platform != "linux/amd64" || CompressedPullBytes <= 0)
            throw new ArgumentException("An exact platform image digest and measured compressed pull size are required.");
        return this;
    }
}

/// <summary>
/// Provider observation for one startup phase. The image-ready phase alone carries the
/// selected image digest and measured pull bytes used for capacity accounting.
/// </summary>
public sealed record SandboxStartupPhaseObservation
{
    private static readonly Regex Sha256Digest = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Creates a versioned timestamped phase observation with phase-specific image evidence.</summary>
    /// <exception cref="ArgumentException">Image evidence appears on the wrong phase or is incomplete.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The phase, contract version, or time is invalid.</exception>
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

/// <summary>Opaque operation identity for a Sandbox provider effect.</summary>
public sealed record SandboxOperationReference(Guid Value)
{
    /// <summary>Requires a nonempty operation UUID.</summary>
    public SandboxOperationReference Validate()
    {
        if (Value == Guid.Empty)
            throw new ArgumentException("A non-empty opaque sandbox operation reference is required.", nameof(Value));
        return this;
    }
}

/// <summary>Opaque reference to a provider-owned Sandbox endpoint.</summary>
public sealed record SandboxEndpointReference(Guid Value)
{
    /// <summary>Requires a nonempty endpoint UUID.</summary>
    public SandboxEndpointReference Validate()
    {
        if (Value == Guid.Empty)
            throw new ArgumentException("A non-empty opaque sandbox endpoint reference is required.", nameof(Value));
        return this;
    }
}

/// <summary>Opaque placement identity used to correlate a Sandbox with its provider location.</summary>
public sealed record SandboxPlacementReference(string Value)
{
    /// <summary>Requires a bounded identifier using the supported placement characters.</summary>
    public SandboxPlacementReference Validate()
    {
        if (string.IsNullOrWhiteSpace(Value) || Value.Length > 128 ||
            Value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            throw new ArgumentException("A bounded opaque sandbox placement reference is required.", nameof(Value));
        return this;
    }
}

/// <summary>Non-secret provider options and release metadata pinned to one Sandbox resource.</summary>
public sealed record SandboxProviderBindingSnapshot(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    JsonElement OptionsSnapshot,
    JsonElement ReleaseDescriptor)
{
    /// <summary>Validates the binding against its exact Sandbox resource and clones the JSON snapshots.</summary>
    /// <exception cref="ArgumentException">The resource, adapter, options revision, or JSON shape differs.</exception>
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

/// <summary>Sandbox-specific provider descriptor for attaching one negotiated workspace volume.</summary>
public sealed record SandboxWorkspaceAttachment(
    WorkspaceVolumeAttachmentNegotiation Negotiation,
    JsonElement ProviderAttachmentDescriptor)
{
    /// <summary>Validates the negotiated attachment and clones its bounded provider-owned descriptor.</summary>
    /// <exception cref="ArgumentException">The descriptor is not an object or exceeds its byte bound.</exception>
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

/// <summary>
/// Fenced provision request containing the planned resource identity, pinned provider candidate,
/// egress selector labels, and workspace attachment.
/// </summary>
public sealed record SandboxProvisionRequest(
    EnvironmentGenerationFence Fence,
    ProviderCandidate Candidate,
    long ResourceGeneration,
    long FencingGeneration,
    Guid OperationId,
    ImmutableDictionary<string, string> EgressSelectorLabels,
    SandboxWorkspaceAttachment Workspace)
{
    /// <summary>Checks provider seam, generation, operation, labels, workspace scope, and planned resource ID.</summary>
    /// <exception cref="ArgumentException">The request fields do not form the exact planned Sandbox operation.</exception>
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

/// <summary>Request to describe an exact Sandbox resource under its current owner fence.</summary>
public sealed record SandboxDescribeRequest(
    EnvironmentGenerationFence Fence,
    ProviderResourceRef Resource,
    long FencingGeneration,
    SandboxProviderBindingSnapshot ProviderBinding,
    DateTimeOffset LeaseCreatedAt)
{
    /// <summary>When true, the observation must include a validated runtime HTTPS endpoint.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequireRuntimeEndpoint { get; init; }

    /// <summary>Validates the owner fence, resource generation, provider binding, and lease timestamp.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A fencing/resource generation or lease time is invalid.</exception>
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

/// <summary>Request to enumerate resources owned by a lease intent at or above a fencing generation.</summary>
public sealed record SandboxListOwnedRequest(
    EnvironmentGenerationFence Fence,
    long MinimumFencingGeneration,
    SandboxLeaseProvisionIntent ProvisionIntent,
    DateTimeOffset LeaseCreatedAt)
{
    /// <summary>Checks current intent, fencing generation, and persisted creation time.</summary>
    /// <exception cref="ArgumentException">The list request does not carry valid owner fencing evidence.</exception>
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

/// <summary>Request to release one exact Sandbox generation using an idempotency key.</summary>
public sealed record SandboxReleaseRequest(
    EnvironmentGenerationFence Fence,
    ProviderResourceRef Resource,
    long FencingGeneration,
    SandboxProviderBindingSnapshot ProviderBinding,
    string IdempotencyKey)
{
    /// <summary>Validates current fence, provider binding, resource identity, and release idempotency key.</summary>
    /// <exception cref="ArgumentException">The resource or request pin is malformed.</exception>
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

/// <summary>
/// Provider response for a new Sandbox resource, including endpoint/placement, negotiated
/// capabilities, startup evidence, and the non-secret provider binding.
/// </summary>
public sealed record SandboxProvisionedResource(
    ProviderResourceRef Resource,
    SandboxEndpointReference Endpoint,
    SandboxPlacementReference Placement,
    ImmutableHashSet<string> NegotiatedCapabilities,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxProviderBindingSnapshot ProviderBinding)
{
    /// <summary>Validates all provider-issued fields and returns normalized immutable collections.</summary>
    /// <exception cref="ArgumentException">The resource, capabilities, startup evidence, or binding is invalid.</exception>
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

/// <summary>
/// Provider observation used by Environment to verify runtime state, applied networking,
/// startup phase evidence, terminal evidence, and optional runtime endpoint.
/// </summary>
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
    /// <summary>Runtime HTTPS address exposed by an observed Pod when the contract requires it.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SandboxRuntimeHttpsEndpoint? RuntimeEndpoint { get; init; }

    /// <summary>Optional current Pod observation; absence is incomplete evidence, never verified readiness.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SandboxPodObservation? PodObservation { get; init; }

    /// <summary>Checks the observation against the exact describe request and ready-state evidence requirements.</summary>
    /// <exception cref="ArgumentException">The observed resource, fence, endpoint, or state evidence does not match.</exception>
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
            RuntimeEndpoint is not null &&
                (!VmIsolationVerified || !WorkspaceAttachmentVerified ||
                 State is not (SandboxObservedState.Pending or SandboxObservedState.Ready) ||
                 RuntimeEndpoint.Validate() != RuntimeEndpoint) ||
            PodObservation is { } pod &&
                (pod.ValidateFor(request) != pod ||
                 pod.ProvisionOperationId != ProvisionOperationId ||
                 pod.IsolationConfigurationVerified != VmIsolationVerified ||
                 (pod.WorkspaceAttachment is not null) != WorkspaceAttachmentVerified ||
                 RuntimeEndpoint is not null && RuntimeEndpoint.PodUid != pod.PodUid) ||
            State == SandboxObservedState.Ready &&
                (!VmIsolationVerified ||
                 !WorkspaceAttachmentVerified ||
                 VerifiedNetworkGeneration is null ||
                 !StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Configured) ||
                 !StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Ready)))
            throw new ArgumentException("The sandbox observation does not match its exact pinned request.");
        return this with { StartupPhases = StartupPhases.ToImmutableArray() };
    }

    /// <summary>Compares provider options and release descriptors structurally, not by JSON formatting.</summary>
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

/// <summary>Observed runtime Service DNS address and Kubernetes object identities.</summary>
public sealed record SandboxRuntimeHttpsEndpoint(
    Uri Address,
    string PodAddress,
    string PodUid,
    string ServiceUid)
{
    /// <summary>Optional receiver configure endpoint, which must match the expected runtime route.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Uri? ReceiverConfigureEndpoint { get; init; }

    /// <summary>Requires HTTPS Service DNS on port 8443 and a valid non-loopback Pod IP and UIDs.</summary>
    /// <exception cref="ArgumentException">The endpoint or observed Kubernetes identity is invalid.</exception>
    public SandboxRuntimeHttpsEndpoint Validate()
    {
        if (Address is not { IsAbsoluteUri: true } || Address.Scheme != Uri.UriSchemeHttps ||
            Address.HostNameType != UriHostNameType.Dns || Address.Port != 8443 ||
            Address.AbsolutePath != "/" || Address.UserInfo.Length != 0 ||
            Address.Query.Length != 0 || Address.Fragment.Length != 0 ||
            !System.Net.IPAddress.TryParse(PodAddress, out var address) ||
            System.Net.IPAddress.IsLoopback(address) ||
            address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any) ||
            address.IsIPv6Multicast ||
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                address.GetAddressBytes()[0] is >= 224 ||
            string.IsNullOrWhiteSpace(PodUid) || PodUid.Length > 256 || PodUid.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(ServiceUid) || ServiceUid.Length > 256 || ServiceUid.Any(char.IsControl))
            throw new ArgumentException("The observed Sandbox HTTPS endpoint is invalid.");
        return this;
    }

    /// <summary>Requires the receiver's configure URL to match this exact endpoint identity.</summary>
    /// <exception cref="ArgumentException">The URL is not the fixed configure route or does not match.</exception>
    public SandboxRuntimeHttpsEndpoint ValidateForConfigureEndpoint(Uri configureEndpoint)
    {
        Validate();
        if (configureEndpoint is not { IsAbsoluteUri: true } ||
            configureEndpoint.Scheme != Uri.UriSchemeHttps ||
            configureEndpoint.HostNameType != UriHostNameType.Dns ||
            configureEndpoint.Port != 8443 ||
            !string.IsNullOrEmpty(configureEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(configureEndpoint.Fragment) ||
            !string.IsNullOrEmpty(configureEndpoint.Query) ||
            configureEndpoint.AbsolutePath != "/runtime/v1/configure" ||
            ReceiverConfigureEndpoint != configureEndpoint)
            throw new ArgumentException("The receiver configure endpoint is invalid or does not match its identity.");
        return this;
    }
}

/// <summary>Whether a startup deadline was exceeded for one phase or for the whole lease.</summary>
public enum SandboxStartupBudgetFailureKind
{
    PhaseExceeded,
    TotalExceeded
}

/// <summary>Evidence that a configured Sandbox startup phase or total budget was missed.</summary>
public sealed record SandboxStartupBudgetFailure(
    SandboxStartupBudgetFailureKind Kind,
    SandboxStartupPhase Phase,
    int BudgetSeconds,
    DateTimeOffset Deadline,
    DateTimeOffset ObservedAt)
{
    /// <summary>Validates the failure kind, phase, positive budget, and late observation time.</summary>
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

/// <summary>Per-phase and overall deadlines used to evaluate Sandbox startup progress.</summary>
public sealed record SandboxStartupTimeBudgets(
        int ScheduledSeconds,
        int ImageReadySeconds,
        int StartedSeconds,
        int ConfiguredSeconds,
        int ReadySeconds,
        int TotalSeconds)
    {
        /// <summary>Requires every phase and total startup budget to be positive.</summary>
        public SandboxStartupTimeBudgets Validate()
        {
            if (ScheduledSeconds <= 0 || ImageReadySeconds <= 0 || StartedSeconds <= 0 ||
                ConfiguredSeconds <= 0 || ReadySeconds <= 0 || TotalSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(SandboxStartupTimeBudgets),
                    "Every startup phase and total time budget must have a configured positive value.");
            return this;
        }

        /// <summary>Returns the configured deadline duration for a startup phase.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The phase is unsupported.</exception>
        public int For(SandboxStartupPhase phase) => phase switch
        {
            SandboxStartupPhase.Scheduled => ScheduledSeconds,
            SandboxStartupPhase.ImageReady => ImageReadySeconds,
            SandboxStartupPhase.Started => StartedSeconds,
            SandboxStartupPhase.Configured => ConfiguredSeconds,
            SandboxStartupPhase.Ready => ReadySeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

        /// <summary>
        /// Finds the earliest missed phase or total deadline from chronological provider observations;
        /// returns null while all observed and pending phases remain within budget.
        /// </summary>
        /// <exception cref="ArgumentException">Evidence is missing, duplicated, or timestamped out of order.</exception>
        public SandboxStartupBudgetFailure? Evaluate(
            DateTimeOffset leaseCreatedAt,
            ImmutableArray<SandboxStartupPhaseObservation> phases,
            DateTimeOffset now)
        {
            Validate();
            if (leaseCreatedAt <= DateTimeOffset.MinValue || leaseCreatedAt > now ||
                phases.IsDefault || phases.Any(phase => phase is null) ||
                phases.Select(phase => phase.Phase).Distinct().Count() != phases.Length)
                throw new ArgumentException("Sandbox startup evidence is missing, duplicated, or in the future.");
            var observedPhases = phases.ToDictionary(phase => phase.Phase);
            var failures = new List<SandboxStartupBudgetFailure>();
            var previousAt = leaseCreatedAt;
            foreach (var phase in Enum.GetValues<SandboxStartupPhase>())
            {
                var deadline = previousAt.AddSeconds(For(phase));
                if (!observedPhases.TryGetValue(phase, out var observed))
                {
                    if (now > deadline)
                        failures.Add(new(SandboxStartupBudgetFailureKind.PhaseExceeded,
                            phase, For(phase), deadline, now));
                    break;
                }
                if (observed.ObservedAt < previousAt || observed.ObservedAt > now)
                    throw new ArgumentException("Sandbox startup phase timestamps must be chronological and not in the future.");
                if (observed.ObservedAt > deadline)
                    failures.Add(new(SandboxStartupBudgetFailureKind.PhaseExceeded,
                        phase, For(phase), deadline, observed.ObservedAt));
                previousAt = observed.ObservedAt;
            }
            var totalDeadline = leaseCreatedAt.AddSeconds(TotalSeconds);
            var totalObservedAt = observedPhases.TryGetValue(SandboxStartupPhase.Ready, out var ready)
                ? ready.ObservedAt : now;
            if (totalObservedAt > totalDeadline)
                failures.Add(new(SandboxStartupBudgetFailureKind.TotalExceeded,
                    SandboxStartupPhase.Ready, TotalSeconds, totalDeadline, totalObservedAt));
            return failures.OrderBy(failure => failure.Deadline).FirstOrDefault();
        }
}

/// <summary>Provider-reported terminal state of a Sandbox Pod.</summary>
public enum SandboxTerminalReason
{
    PodSucceeded,
    PodFailed
}

/// <summary>
/// Immutable terminal evidence bound to one claim UID, Sandbox UID, fencing generation,
/// observed generation, and terminal reason.
/// </summary>
public sealed record SandboxTerminalEvidence(
    int ContractVersion,
    string ClaimUid,
    string SandboxUid,
    long FencingGeneration,
    long SandboxObservedGeneration,
    SandboxTerminalReason Reason,
    DateTimeOffset ObservedAt)
{
    /// <summary>Checks version, resource IDs, generations, reason, and timestamp.</summary>
    /// <exception cref="ArgumentException">The terminal evidence is malformed.</exception>
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

/// <summary>Provider's outcome when the exact Sandbox resource is released.</summary>
public enum SandboxReleaseDisposition
{
    Released,
    KnownOwnedAbsent
}

/// <summary>Provider acknowledgment of release or known absence for one exact Sandbox request.</summary>
public sealed record SandboxReleaseReceipt(
    ProviderResourceRef Resource,
    string IdempotencyKey,
    SandboxReleaseDisposition Disposition)
{
    /// <summary>Requires the receipt to identify the same resource, key, and valid disposition.</summary>
    /// <exception cref="ArgumentException">The receipt does not match the exact request.</exception>
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

/// <summary>
/// Request to clean up a partially created claim after authorized abandonment, before a
/// complete Sandbox resource was persisted.
/// </summary>
public sealed record SandboxPartialReleaseRequest(
    EnvironmentGenerationFence Fence,
    SandboxLeaseSnapshot Lease)
{
    /// <summary>Requires the current authorized-retirement lease and its exact saved provider request.</summary>
    /// <exception cref="ArgumentException">The lease is stale, not retiring, or already has a full resource.</exception>
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

/// <summary>
/// Provider receipt proving all exact partial-claim resources and Pods were removed or
/// were already absent under the specified Environment and fencing generations.
/// </summary>
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
    /// <summary>Checks exact operation IDs, deterministic Kubernetes names, and absence of owned objects.</summary>
    /// <exception cref="ArgumentException">The cleanup evidence does not prove exact owner-fenced retirement.</exception>
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

    /// <summary>Allows an absent UID or validates a bounded UID for an owned resource.</summary>
    private static bool IsValidUid(string? value) =>
        value is null ||
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(char.IsControl);
}

/// <summary>
/// Sandbox provider boundary for provision, observation, owned-resource enumeration,
/// full release, and partial release effects.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>Creates a resource at the exact planned identity and returns provider evidence.</summary>
    Task<SandboxProvisionedResource> ProvisionAsync(
        SandboxProvisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads actual provider state for the pinned resource and fencing generation.</summary>
    Task<SandboxObservation> DescribeAsync(
        SandboxDescribeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates resources owned by the saved lease intent for recovery and cleanup.</summary>
    Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
        SandboxListOwnedRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases the complete exact Sandbox resource.</summary>
    Task<SandboxReleaseReceipt> ReleaseAsync(
        SandboxReleaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a partial claim after owner-authorized retirement.</summary>
    Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
        SandboxPartialReleaseRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Why an Environment is retiring a Sandbox lease.</summary>
public enum SandboxRetirementReason
{
    Finished,
    AuthorizedAbandon
}

/// <summary>
/// Saved provider input for provisioning. The exact adapter, options, selection, and request
/// JSON let owner recovery repeat or reconcile an effect without rebuilding from mutable config.
/// </summary>
public sealed record SandboxLeaseProvisionIntent(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    JsonElement OptionsSnapshot,
    JsonElement SelectionSnapshot,
    JsonElement ProviderRequest)
{
    /// <summary>Validates canonical adapter/options pins and clones bounded JSON snapshots.</summary>
    /// <exception cref="ArgumentException">A pin is invalid or a saved JSON object exceeds its limit.</exception>
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

    /// <summary>Returns whether an element is a bounded JSON object.</summary>
    private static bool IsBoundedObject(JsonElement value, int maxLength) =>
        value.ValueKind == JsonValueKind.Object && value.GetRawText().Length <= maxLength;
}

/// <summary>
/// Persisted lease owner view including all provision, retirement, fencing, retry, and
/// reconciliation metadata needed to recover provider effects.
/// </summary>
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
    /// <summary>Fingerprint of current retirement authorization and request, when retiring.</summary>
    public string? RetirementFingerprint { get; init; }
    /// <summary>Issuer that authorized retirement, when supplied.</summary>
    public string? RetiringIssuer { get; init; }
    /// <summary>Digest of immutable provider request persisted before an external effect.</summary>
    public string? ProviderRequestFingerprint { get; init; }
    /// <summary>Owner revision that fences updates to this lease snapshot.</summary>
    public long LeaseRevision { get; init; }
    /// <summary>Lease expiry for nonterminal work; terminal leases have no expiry.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    /// <summary>Receipt for provider cleanup of a partial resource.</summary>
    public SandboxPartialReleaseReceipt? PartialReleaseReceipt { get; init; }
    /// <summary>Persisted lease creation time used to evaluate startup budgets.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Checks lease revision, terminal expiry, and partial-release state consistency.</summary>
    /// <exception cref="ArgumentException">The lease state and revision metadata conflict.</exception>
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

/// <summary>Lease and claim token used to clean up a provider resource created after its lease became terminal.</summary>
public sealed record SandboxLateResourceCleanupClaim(
    SandboxLeaseSnapshot Lease,
    SandboxProvisionedResource ProvisionedResource,
    string IdempotencyKey,
    Guid ClaimToken,
    DateTimeOffset ClaimExpiresAt)
{
    /// <summary>Resource digest used to fence cleanup to the exact late provider object.</summary>
    public string ResourceFingerprint { get; init; } = string.Empty;
    /// <summary>Provider attempt that created the late resource, when known.</summary>
    public Guid? ProvisionAttemptId { get; init; }
    /// <summary>Provider attempt that removed the late resource, when known.</summary>
    public Guid? ReleaseAttemptId { get; init; }

    /// <summary>Requires a terminal old lease and a valid claim for the exact late resource generation.</summary>
    /// <exception cref="ArgumentException">The lease, resource, fingerprint, or claim metadata is inconsistent.</exception>
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
            ClaimExpiresAt <= DateTimeOffset.MinValue ||
            !SandboxProviderAttemptRequest.IsFingerprint(ResourceFingerprint))
            throw new ArgumentException("The late Sandbox resource cleanup claim is invalid.");
        return this;
    }
}

/// <summary>Request to complete authorized partial release with its provider receipt.</summary>
public sealed record SandboxPartialReleaseCompletionRequest(
    EnvironmentGenerationFence Fence,
    SandboxPartialReleaseReceipt Receipt)
{
    /// <summary>Persisted provider attempt associated with this partial release, when available.</summary>
    public Guid? ProviderAttemptId { get; init; }
}

/// <summary>Lease reservation and duplicate flag returned for an idempotent provision request.</summary>
public sealed record SandboxLeaseReservation(SandboxLeaseSnapshot Lease, bool Replayed);

/// <summary>Provider effect category recorded in the durable attempt journal.</summary>
public enum SandboxProviderEffectKind
{
    Provision,
    Release,
    PartialRelease,
    LateResourceRelease,
    LegacyUnknown
}

/// <summary>State of an external Sandbox effect attempt, including uncertain outcomes.</summary>
public enum SandboxProviderAttemptState
{
    InProgress,
    ResultRecorded,
    FailedNoEffect,
    Unknown,
    Completed,
    LegacyUnknown
}

/// <summary>
/// Durable identity of one provider effect. Request fingerprints bind retries to exact
/// provider inputs; late-resource fingerprints additionally bind asynchronous cleanup.
/// </summary>
public sealed record SandboxProviderAttemptRequest(
    EnvironmentGenerationFence Fence,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    Guid OperationId,
    SandboxProviderEffectKind EffectKind,
    string RequestFingerprint,
    string? LateResourceFingerprint = null)
{
    /// <summary>Builds the operation fingerprint from the persisted provision intent.</summary>
    public static SandboxProviderAttemptRequest ForProvision(SandboxLeaseSnapshot lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var fingerprint = lease.ProviderRequestFingerprint
            ?? throw new ArgumentException("The immutable Sandbox provider request is not saved.", nameof(lease));
        return new SandboxProviderAttemptRequest(
            lease.Fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId,
            SandboxProviderEffectKind.Provision,
            fingerprint).Validate();
    }

    /// <summary>Builds a release fingerprint from the exact provider resource and release descriptor.</summary>
    public static SandboxProviderAttemptRequest ForRelease(
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource resource)
    {
        ArgumentNullException.ThrowIfNull(lease);
        resource = (resource ?? throw new ArgumentNullException(nameof(resource))).Validate();
        return ForSavedEffect(
            lease,
            SandboxProviderEffectKind.Release,
            string.Join('\0',
                lease.ReleaseIdempotencyKey ?? string.Empty,
                ResourceIdentity(resource),
                resource.ProviderBinding.ReleaseDescriptor.GetRawText()));
    }

    /// <summary>Builds a partial-release fingerprint from retirement and saved request pins.</summary>
    public static SandboxProviderAttemptRequest ForPartialRelease(SandboxLeaseSnapshot lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ForSavedEffect(
            lease,
            SandboxProviderEffectKind.PartialRelease,
            string.Join('\0',
                lease.ReleaseIdempotencyKey ?? string.Empty,
                lease.CurrentFencingGeneration,
                lease.RetirementFingerprint ?? string.Empty,
                lease.ProviderRequestFingerprint ?? string.Empty));
    }

    /// <summary>Builds the late-resource release attempt from a validated cleanup claim.</summary>
    public static SandboxProviderAttemptRequest ForLateResourceRelease(
        SandboxLateResourceCleanupClaim claim)
    {
        claim = (claim ?? throw new ArgumentNullException(nameof(claim))).Validate();
        return ForLateResourceRelease(
            claim.Lease,
            claim.ProvisionedResource,
            claim.IdempotencyKey,
            claim.ResourceFingerprint);
    }

    /// <summary>Builds a late-resource release fingerprint from its exact resource and descriptor.</summary>
    public static SandboxProviderAttemptRequest ForLateResourceRelease(
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource provisionedResource,
        string idempotencyKey,
        string resourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(lease);
        provisionedResource = (provisionedResource ?? throw new ArgumentNullException(nameof(provisionedResource)))
            .Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (!IsFingerprint(resourceFingerprint))
            throw new ArgumentException("The late-resource fingerprint is invalid.", nameof(resourceFingerprint));
        var details = string.Join('\0',
            idempotencyKey,
            resourceFingerprint,
            ResourceIdentity(provisionedResource),
            provisionedResource.ProviderBinding.ReleaseDescriptor.GetRawText());
        return ForSavedEffect(
            lease,
            SandboxProviderEffectKind.LateResourceRelease,
            details,
            resourceFingerprint);
    }

    /// <summary>Returns canonical lowercase SHA-256 for a provider request fingerprint.</summary>
    public static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Validates the source fence, effect identity, request digest, and optional late-resource digest.</summary>
    /// <exception cref="ArgumentException">The attempt identity or fingerprints are malformed.</exception>
    public SandboxProviderAttemptRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        if (ResourceGeneration < 1 ||
            ProviderFencingGeneration < 1 ||
            OperationId == Guid.Empty ||
            !Enum.IsDefined(EffectKind) ||
            !IsFingerprint(RequestFingerprint) ||
            (EffectKind == SandboxProviderEffectKind.LateResourceRelease) !=
                IsFingerprint(LateResourceFingerprint))
            throw new ArgumentException("The Sandbox provider attempt identity is invalid.");
        return this;
    }

    /// <summary>Returns whether a value is a canonical lowercase SHA-256 fingerprint.</summary>
    internal static bool IsFingerprint(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static SandboxProviderAttemptRequest ForSavedEffect(
        SandboxLeaseSnapshot lease,
        SandboxProviderEffectKind effectKind,
        string details,
        string? lateResourceFingerprint = null)
    {
        var owner = lease.Fence.Owner;
        var canonical = string.Join('\0',
            "sandbox-provider-attempt-v1",
            effectKind,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            lease.Fence.LifecycleGeneration,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId.ToString("N"),
            details);
        return new SandboxProviderAttemptRequest(
            lease.Fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId,
            effectKind,
            Fingerprint(canonical),
            lateResourceFingerprint).Validate();
    }

    /// <summary>Builds stable resource and adapter identity text for an attempt fingerprint.</summary>
    private static string ResourceIdentity(SandboxProvisionedResource resource) =>
        string.Join('\0',
            resource.Resource.Seam,
            resource.Resource.ProviderId,
            resource.Resource.ResourceId,
            resource.Resource.Generation,
            resource.ProviderBinding.ProviderId,
            resource.ProviderBinding.AdapterVersion,
            resource.ProviderBinding.OptionsSchemaVersion,
            resource.ProviderBinding.OptionsRevision);
}

/// <summary>Persisted provider attempt state and any recorded outcome used to recover uncertain effects.</summary>
public sealed record SandboxProviderAttemptSnapshot(
    Guid AttemptId,
    SandboxProviderAttemptRequest Request,
    SandboxProviderAttemptState State,
    JsonElement? Outcome,
    string? FailureCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? OutcomeRecordedAt,
    DateTimeOffset? ResolvedAt)
{
    /// <summary>Checks operation request, timestamps, state-specific outcome, and resolution metadata.</summary>
    /// <exception cref="ArgumentException">The attempt record is inconsistent with its state.</exception>
    public SandboxProviderAttemptSnapshot Validate()
    {
        _ = (Request ?? throw new ArgumentNullException(nameof(Request))).Validate();
        if (AttemptId == Guid.Empty ||
            !Enum.IsDefined(State) ||
            StartedAt <= DateTimeOffset.MinValue ||
            OutcomeRecordedAt is { } recordedAt && recordedAt < StartedAt ||
            ResolvedAt is { } resolvedAt && (OutcomeRecordedAt is null || resolvedAt < OutcomeRecordedAt) ||
            FailureCode is { } code &&
                (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl)) ||
            (State is SandboxProviderAttemptState.InProgress or SandboxProviderAttemptState.LegacyUnknown) &&
                (Outcome is not null || OutcomeRecordedAt is not null || ResolvedAt is not null) ||
            (State is SandboxProviderAttemptState.ResultRecorded or SandboxProviderAttemptState.FailedNoEffect or
                SandboxProviderAttemptState.Unknown or SandboxProviderAttemptState.Completed) &&
                (Outcome is not { ValueKind: JsonValueKind.Object } || OutcomeRecordedAt is null) ||
            (State is SandboxProviderAttemptState.FailedNoEffect or SandboxProviderAttemptState.Completed
                ? ResolvedAt is null
                : ResolvedAt is not null))
            throw new ArgumentException("The Sandbox provider attempt snapshot is invalid.");
        return this with { Outcome = Outcome?.Clone() };
    }
}

/// <summary>Attempt snapshot and idempotent replay status returned by the owner.</summary>
public sealed record SandboxProviderAttemptReservation(
    SandboxProviderAttemptSnapshot Attempt,
    bool Replayed);

/// <summary>Redacted stable receipt for reading one persisted provider attempt over an owner API.</summary>
public sealed record SandboxProviderAttemptReceipt(
    int ContractVersion,
    Guid AttemptId,
    Guid OperationId,
    long LifecycleGeneration,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    SandboxProviderEffectKind EffectKind,
    SandboxProviderAttemptState State,
    string RequestFingerprint,
    string? LateResourceFingerprint,
    string? FailureCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? OutcomeRecordedAt,
    DateTimeOffset? ResolvedAt)
{
    /// <summary>Projects a validated attempt snapshot into its public receipt fields.</summary>
    public static SandboxProviderAttemptReceipt From(SandboxProviderAttemptSnapshot attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        attempt.Validate();
        return new(
            1,
            attempt.AttemptId,
            attempt.Request.OperationId,
            attempt.Request.Fence.LifecycleGeneration,
            attempt.Request.ResourceGeneration,
            attempt.Request.ProviderFencingGeneration,
            attempt.Request.EffectKind,
            attempt.State,
            attempt.Request.RequestFingerprint,
            attempt.Request.LateResourceFingerprint,
            attempt.FailureCode,
            attempt.StartedAt,
            attempt.OutcomeRecordedAt,
            attempt.ResolvedAt);
    }
}

/// <summary>Versioned read response for all provider attempts associated with one operation.</summary>
public sealed record SandboxProviderAttemptReadResponseV1(
    int ContractVersion,
    EnvironmentGenerationFence CurrentFence,
    Guid OperationId,
    ImmutableArray<SandboxProviderAttemptReceipt> Attempts);

/// <summary>Compact provider-state evidence used to reconcile a late-resource cleanup record.</summary>
public sealed record SandboxObservationSummary(
    ProviderResourceRef Resource,
    SandboxObservedState State,
    long FencingGeneration,
    bool VmIsolationVerified,
    bool WorkspaceAttachmentVerified,
    long? VerifiedNetworkGeneration,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxTerminalEvidence? TerminalEvidence,
    SandboxStartupBudgetFailure? StartupFailure);

/// <summary>Owner-scoped late-resource record with its source fence, attempt evidence, and optional observation.</summary>
public sealed record SandboxLateResourceCleanupReadItem(
    EnvironmentGenerationFence SourceFence,
    Guid OperationId,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    long CurrentFencingGeneration,
    string ResourceFingerprint,
    ProviderResourceRef Resource,
    SandboxProviderAttemptReceipt? ProvisionAttempt,
    SandboxProviderAttemptReceipt? ReleaseAttempt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClaimExpiresAt,
    SandboxReleaseReceipt? ReleaseReceipt,
    SandboxObservationSummary? ProviderObservation);

/// <summary>Versioned bounded page of late-resource cleanup evidence.</summary>
public sealed record SandboxLateResourceCleanupReadResponseV1(
    int ContractVersion,
    EnvironmentGenerationFence CurrentFence,
    ImmutableArray<SandboxLateResourceCleanupReadItem> Items,
    bool HasMore);

/// <summary>Combined owner lease, provider resource, and late-resource journal record.</summary>
public sealed record SandboxLateResourceCleanupRecord(
    SandboxLeaseSnapshot Lease,
    SandboxProvisionedResource ProvisionedResource,
    SandboxLateResourceCleanupReadItem Receipt);

/// <summary>Current Projects owner evidence that authorizes an Environment to abandon a Sandbox run.</summary>
public sealed record SandboxRetirementAuthorization(
    string Issuer,
    string ActorId,
    long MembershipRevision)
{
    /// <summary>Requires bounded actor identity and a positive current membership revision.</summary>
    /// <exception cref="ArgumentException">The authorization evidence is missing or invalid.</exception>
    public SandboxRetirementAuthorization Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || Issuer.Length > 512 || Issuer.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(ActorId) || ActorId.Length > 256 || ActorId.Any(char.IsControl) ||
            MembershipRevision < 1)
            throw new ArgumentException("Current Projects authorization evidence is invalid.");
        return this;
    }
}

/// <summary>
/// Durable lease owner boundary for provision/release, claim and fencing checks, attempt
/// journals, reconciliation reads, and late-resource cleanup.
/// </summary>
public interface ISandboxLeaseStore
{
    /// <summary>Reserves a run's provider lease using an idempotency key and immutable provision intent.</summary>
    Task<SandboxLeaseReservation> ReserveProvisionAsync(
        EnvironmentGenerationFence fence,
        string idempotencyKey,
        SandboxLeaseProvisionIntent intent,
        CancellationToken cancellationToken);

    /// <summary>Reads the current lease while requiring the exact current Environment fence.</summary>
    Task<SandboxLeaseSnapshot?> GetCurrentAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs an owner-scoped read while retaining the active Environment owner lock.
    /// The callback must not recursively call this reader or perform provider effects;
    /// the lock protects the current lease snapshot for the duration of the callback.
    /// </summary>
    Task<TResult> GetCurrentAsync<TResult>(
        EnvironmentGenerationFence fence,
        Func<SandboxLeaseSnapshot?, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken);

    /// <summary>Reads the lease and workspace under the same retained Environment owner lock.</summary>
    Task<TResult> GetCurrentWithWorkspaceAsync<TResult>(
        EnvironmentGenerationFence fence,
        string volumeId,
        Func<SandboxLeaseSnapshot?, EnvironmentWorkspaceVolumeSnapshot?, CancellationToken, Task<TResult>> callback,
        CancellationToken cancellationToken);

    /// <summary>Reads a lease at a specific resource generation under the supplied Environment fence.</summary>
    Task<SandboxLeaseSnapshot?> GetAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        CancellationToken cancellationToken);

    /// <summary>Persists the exact serialized provider request before dispatching its external effect.</summary>
    Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        JsonElement providerRequest,
        CancellationToken cancellationToken);

    /// <summary>Records provision success, no-effect failure, or uncertain outcome for the lease.</summary>
    Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource? provisionedResource,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null,
        string? providerErrorCode = null);

    /// <summary>Begins a durable provider attempt before making an external Sandbox call.</summary>
    Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
        SandboxProviderAttemptRequest request,
        CancellationToken cancellationToken);

    /// <summary>Begins an attempt only if the request's source fence equals the current authorized fence.</summary>
    Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
        SandboxProviderAttemptRequest request,
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(currentFence);
        if (request.Fence != currentFence)
            throw new ArgumentException(
                "The provider attempt source fence is not the current Environment authorization fence.",
                nameof(currentFence));
        return BeginProviderAttemptAsync(request, cancellationToken);
    }

    /// <summary>Persists provider outcome evidence and whether an effect may have occurred.</summary>
    Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
        Guid attemptId,
        EnvironmentGenerationFence fence,
        JsonElement outcome,
        bool effectMayHaveApplied,
        string? failureCode,
        CancellationToken cancellationToken);

    /// <summary>Reads attempt history for the exact current Environment and operation.</summary>
    Task<ImmutableArray<SandboxProviderAttemptSnapshot>> GetProviderAttemptsAsync(
        EnvironmentGenerationFence currentFence,
        Guid operationId,
        CancellationToken cancellationToken);

    /// <summary>Reads late provider resources requiring reconciliation and cleanup.</summary>
    Task<(ImmutableArray<SandboxLateResourceCleanupRecord> Items, bool HasMore)> GetLateResourceCleanupRecordsAsync(
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts lease retirement. Authorized abandonment requires current Projects evidence;
    /// normal completion must carry matching terminal proof.
    /// </summary>
    Task<SandboxLeaseSnapshot> BeginRetirementAsync(
        EnvironmentGenerationFence fence,
        long resourceGeneration,
        long providerFencingGeneration,
        SandboxRetirementReason reason,
        string idempotencyKey,
        SandboxRetirementAuthorization? authorization,
        SandboxTerminalEvidence? terminalEvidence,
        CancellationToken cancellationToken);

    /// <summary>Commits full release after exact receipt validation and an optional final authority check.</summary>
    Task<SandboxLeaseSnapshot> CompleteReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        long providerFencingGeneration,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null,
        Guid? providerAttemptId = null);

    /// <summary>Commits partial-resource retirement after matching owner and provider receipt validation.</summary>
    Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
        SandboxPartialReleaseCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Claims one late resource for cleanup with a lease token and expiry.</summary>
    Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    /// <summary>Records late-resource release against the current fence and matching cleanup claim.</summary>
    Task CompleteLateResourceCleanupAsync(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim claim,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null);
}

/// <summary>
/// Safe provider failure carrying a stable code and whether an external effect may already
/// have occurred; the owner uses the latter to choose reconciliation instead of retry.
/// </summary>
public sealed class SandboxProviderException(
    string code,
    string message,
    bool effectMayHaveApplied,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>Stable non-sensitive provider error code.</summary>
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("A safe sandbox provider error code is required.", nameof(code))
        : code;

    /// <summary>True when the provider call may have changed external state despite the failure.</summary>
    public bool EffectMayHaveApplied { get; } = effectMayHaveApplied;
}
