using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

public sealed record AgentSandboxPersistentVolumeClaimAttachment(
    int ContractVersion,
    string StorageProviderId,
    string Namespace,
    string ClaimName,
    string ClaimUid)
{
    public AgentSandboxPersistentVolumeClaimAttachment ValidateFor(
        WorkspaceVolumeAttachmentNegotiation negotiation)
    {
        ArgumentNullException.ThrowIfNull(negotiation);
        negotiation.Validate();
        if (ContractVersion != 1 ||
            !string.Equals(StorageProviderId, negotiation.StorageResource.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(ClaimUid, negotiation.StorageResource.ResourceId, StringComparison.Ordinal) ||
            !IsDnsSubdomain(Namespace) ||
            !IsDnsSubdomain(ClaimName) ||
            string.IsNullOrWhiteSpace(ClaimUid))
            throw new ArgumentException(
                "The persistent volume claim descriptor does not match the negotiated Storage resource.");
        return this;
    }

    private static bool IsDnsSubdomain(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 253 &&
        value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            label[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            label[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            label.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));
}

public sealed partial class AgentSandboxProvider : ISandboxProvider, ISandboxBuildTestCommandProvider
{
    private const string ExtensionsApiGroup = "extensions.agents.x-k8s.io";
    private const string SandboxesApiGroup = "agents.x-k8s.io";
    private const string ManagedLabel = "agentweaver.dev/managed-sandbox";
    private const string OwnerLabel = "agentweaver.dev/sandbox-owner";
    private const string LifecycleGenerationLabel = "agentweaver.dev/sandbox-lifecycle";
    private const string ResourceGenerationLabel = "agentweaver.dev/sandbox-generation";
    private const string FencingGenerationLabel = "agentweaver.dev/sandbox-fence";
    private const string OperationLabel = "agentweaver.dev/sandbox-operation";
    private const string OwnerAnnotation = "agentweaver.dev/sandbox-owner";
    private const string RecoveryAnnotation = "agentweaver.dev/sandbox-recovery";
    private const string ProviderBindingAnnotation = "agentweaver.dev/provider-binding";
    private const string ClaimUidLabel = "agents.x-k8s.io/claim-uid";
    private const string WorkspaceVolumeName = "agentweaver-workspace";
    private const string WorkspaceContainerName = "agenthost";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly AgentSandboxOptions _options;
    private readonly KubernetesAgentSandboxClient _client;
    private readonly TimeProvider _timeProvider;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public AgentSandboxProvider(
        AgentSandboxOptions options,
        KubernetesAgentSandboxClient client,
        TimeProvider? timeProvider = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SandboxProvisionedResource> ProvisionAsync(
        SandboxProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateCandidate(request.Candidate, _options);
        ValidateEgressSelectorLabels(request.EgressSelectorLabels);
        var workspace = ReadWorkspaceAttachment(request.Workspace, _options);
        await VerifyRuntimeClassAsync(_options, cancellationToken).ConfigureAwait(false);
        var workspaceFingerprint = await VerifyWorkspaceClaimAsync(
            request.Fence,
            request.Workspace.Negotiation,
            workspace,
            cancellationToken).ConfigureAwait(false);

        var ownerFingerprint = GetOwnerFingerprint(request.Fence.Owner);
        var names = GetResourceNames(
            ownerFingerprint,
            request.Fence,
            request.Fence.LifecycleGeneration,
            request.ResourceGeneration,
            request.FencingGeneration,
            request.OperationId,
            request.Candidate.OptionsRevision);
        var labels = BuildResourceLabels(
            ownerFingerprint,
            request.Fence.LifecycleGeneration,
            request.ResourceGeneration,
            request.FencingGeneration,
            request.OperationId);
        var template = BuildTemplate(request, workspace, names.TemplateName, labels, _options);

        var effectsMayHaveApplied = false;
        try
        {
            var ensuredTemplate = await EnsureResourceAsync(
                ExtensionsApiGroup,
                "sandboxtemplates",
                _options.Namespace,
                names.TemplateName,
                template,
                cancellationToken).ConfigureAwait(false);
            effectsMayHaveApplied |= ensuredTemplate.Created;
            var templateUid = ValidateTemplate(
                ensuredTemplate.Resource,
                request.Workspace,
                request.EgressSelectorLabels,
                workspace,
                names.TemplateName,
                ownerFingerprint,
                labels,
                _options);

            var warmPool = BuildWarmPool(names.WarmPoolName, names.TemplateName, labels);
            var ensuredWarmPool = await EnsureResourceAsync(
                ExtensionsApiGroup,
                "sandboxwarmpools",
                _options.Namespace,
                names.WarmPoolName,
                warmPool,
                cancellationToken).ConfigureAwait(false);
            effectsMayHaveApplied |= ensuredWarmPool.Created;
            var warmPoolUid = ValidateWarmPool(
                ensuredWarmPool.Resource,
                _options.Namespace,
                names.WarmPoolName,
                names.TemplateName,
                ownerFingerprint,
                labels);

            var endpoint = CreateEndpointReference(
                ownerFingerprint,
                request.ResourceGeneration,
                request.FencingGeneration,
                request.OperationId);
            var recovery = new AgentSandboxRecoveryDescriptor(
                1,
                AgentSandboxProviderMetadata.ProviderId,
                AgentSandboxProviderMetadata.AdapterVersion.ToString(),
                _options.OptionsSchemaVersion,
                _options.OptionsRevision,
                JsonSerializer.SerializeToElement(_options, JsonOptions),
                _options.Namespace,
                ownerFingerprint,
                request.EgressSelectorLabels,
                request.Fence.LifecycleGeneration,
                request.ResourceGeneration,
                request.FencingGeneration,
                request.OperationId.ToString("N"),
                names.ClaimName,
                names.TemplateName,
                templateUid,
                names.WarmPoolName,
                warmPoolUid,
                workspace.Namespace,
                workspace.ClaimName,
                workspace.ClaimUid,
                workspace.StorageProviderId,
                request.Workspace.Negotiation.Volume.ResourceGeneration,
                request.Workspace.Negotiation.MountPath,
                request.Workspace.Negotiation.ReadOnly,
                workspaceFingerprint,
                endpoint.Value,
                _client.ClusterIdentity);
            var claim = BuildClaim(names.ClaimName, names.WarmPoolName, labels, recovery);
            var ensuredClaim = await EnsureResourceAsync(
                ExtensionsApiGroup,
                "sandboxclaims",
                _options.Namespace,
                names.ClaimName,
                claim,
                cancellationToken).ConfigureAwait(false);
            effectsMayHaveApplied |= ensuredClaim.Created;
            var claimUid = ValidateClaim(
                ensuredClaim.Resource,
                names.ClaimName,
                names.WarmPoolName,
                ownerFingerprint,
                labels,
                recovery);

            var resource = new ProviderResourceRef(
                ProviderSeam.Sandbox,
                AgentSandboxProviderMetadata.ProviderId,
                claimUid,
                request.ResourceGeneration);
            var binding = BuildBinding(recovery with { ClaimUid = claimUid }, resource);
            var bindingJson = JsonSerializer.Serialize(binding, JsonOptions);
            if (!TryGetAnnotation(ensuredClaim.Resource, ProviderBindingAnnotation, out var existingBinding))
            {
                await _client.SetAnnotationAsync(
                    ExtensionsApiGroup,
                    "sandboxclaims",
                    _options.Namespace,
                    names.ClaimName,
                    claimUid,
                    ProviderBindingAnnotation,
                    bindingJson,
                    cancellationToken).ConfigureAwait(false);
                effectsMayHaveApplied = true;
            }
            else if (!string.Equals(existingBinding, bindingJson, StringComparison.Ordinal))
            {
                throw new SandboxProviderException(
                    "provider_binding_mismatch",
                    "The existing sandbox claim has a different pinned provider binding.",
                    effectMayHaveApplied: true);
            }

            var capabilities = AgentSandboxProviderMetadata.CreateRegistration(_options)
                .Descriptor.AdvertisedCapabilities;
            return new SandboxProvisionedResource(
                resource,
                endpoint,
                CreatePlacementReference(_client.ClusterIdentity, _options.Namespace),
                capabilities,
                [],
                binding).Validate();
        }
        catch (SandboxProviderException exception)
        {
            throw new SandboxProviderException(
                exception.Code,
                exception.Message,
                effectsMayHaveApplied || exception.EffectMayHaveApplied,
                exception);
        }
    }

    public async Task<SandboxObservation> DescribeAsync(
        SandboxDescribeRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var (options, descriptor) = ReadBinding(
            request.Fence,
            request.Resource,
            request.FencingGeneration,
            request.ProviderBinding);
        if (!string.Equals(descriptor.Namespace, options.Namespace, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
        var operationId = Guid.ParseExact(descriptor.OperationId, "N");

        var claim = await _client.GetAsync(
            ExtensionsApiGroup,
            "sandboxclaims",
            descriptor.Namespace,
            descriptor.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            var remainingSandboxes = await FindClaimSandboxesAsync(
                descriptor,
                cancellationToken).ConfigureAwait(false);
            var remainingPods = await FindClaimPodsAsync(
                descriptor,
                cancellationToken).ConfigureAwait(false);
            return new SandboxObservation(
                request.Resource,
                remainingSandboxes.Count == 0 && remainingPods.Count == 0
                    ? SandboxObservedState.Absent
                    : SandboxObservedState.Pending,
                request.FencingGeneration,
                false,
                false,
                null,
                [],
                ProvisionOperationId: operationId,
                ProvisionedResource: BuildProvisionedResource(request, descriptor, [])).ValidateFor(request);
        }

        ValidateClaimAgainstDescriptor(claim.Value, request.Resource, descriptor);
        var claimUid = RequiredString(RequiredObject(claim.Value, "metadata"), "uid");
        var sandboxName = ReadOptionalString(claim.Value, "status", "sandbox", "name");
        if (string.IsNullOrWhiteSpace(sandboxName))
            return PendingObservation(
                request, descriptor, [], workspaceVerified: false, isolationVerified: false);

        var sandbox = await _client.GetAsync(
            SandboxesApiGroup,
            "sandboxes",
            descriptor.Namespace,
            sandboxName,
            cancellationToken).ConfigureAwait(false);
        if (sandbox is null)
            return PendingObservation(
                request, descriptor, [], workspaceVerified: false, isolationVerified: false);
        ValidateSandboxOwner(sandbox.Value, claimUid);
        if (TryReadTerminalEvidence(
                sandbox.Value,
                claimUid,
                request.FencingGeneration,
                _timeProvider.GetUtcNow()) is { } terminalEvidence)
            return new SandboxObservation(
                request.Resource,
                SandboxObservedState.Finished,
                request.FencingGeneration,
                VmIsolationVerified: false,
                WorkspaceAttachmentVerified: false,
                VerifiedNetworkGeneration: null,
                StartupPhases: [],
                TerminalEvidence: terminalEvidence,
                ProvisionOperationId: operationId,
                ProvisionedResource: BuildProvisionedResource(request, descriptor, [])).ValidateFor(request);

        var pods = await _client.ListAsync(
            string.Empty,
            "pods",
            descriptor.Namespace,
            ImmutableDictionary<string, string>.Empty
                .Add(ClaimUidLabel, claimUid),
            cancellationToken).ConfigureAwait(false);
        if (pods.Count != 1)
            return PendingObservation(
                request, descriptor, [], workspaceVerified: false, isolationVerified: false);

        var pod = pods[0];
        ValidatePodOwner(
            pod,
            RequiredString(RequiredObject(sandbox.Value, "metadata"), "uid"),
            claimUid,
            descriptor);
        var workspaceVerified = await VerifyAttachedPodAsync(
            pod,
            request.Fence,
            descriptor,
            cancellationToken).ConfigureAwait(false);
        var isolationVerified = await VerifyPodIsolationAsync(
            pod,
            options,
            descriptor.WorkspaceMountPath,
            cancellationToken).ConfigureAwait(false);
        var phases = await ReadStartupPhasesAsync(
            pod, descriptor.Namespace, options, cancellationToken).ConfigureAwait(false);
        var startupFailure = EvaluateStartupBudget(
            request.LeaseCreatedAt,
            phases,
            options.StartupBudgets,
            _timeProvider.GetUtcNow());
        var failed = IsPodFailed(pod) || startupFailure is not null;
        var state = failed
            ? SandboxObservedState.Failed
            : SandboxObservedState.Pending;
        return new SandboxObservation(
            request.Resource,
            state,
            request.FencingGeneration,
            isolationVerified,
            workspaceVerified,
            VerifiedNetworkGeneration: null,
            StartupPhases: phases,
            ProvisionOperationId: operationId,
            ProvisionedResource: BuildProvisionedResource(request, descriptor, phases),
            StartupFailure: startupFailure).ValidateFor(request);
    }

    public async Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
        SandboxListOwnedRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var pinnedIntent = request.ProvisionIntent;
        var options = ReadPinnedOptions(pinnedIntent);
        var ownerFingerprint = GetOwnerFingerprint(request.Fence.Owner);
        var claims = await _client.ListAsync(
            ExtensionsApiGroup,
            "sandboxclaims",
            options.Namespace,
            ImmutableDictionary<string, string>.Empty
                .Add(ManagedLabel, "true")
                .Add(OwnerLabel, ownerFingerprint),
            cancellationToken).ConfigureAwait(false);
        var observations = new List<SandboxObservation>(claims.Count);
        foreach (var claim in claims)
        {
            var labels = ReadLabels(claim);
            if (!labels.TryGetValue(OwnerLabel, out var actualOwner) ||
                !string.Equals(actualOwner, ownerFingerprint, StringComparison.Ordinal))
                continue;
            if (!labels.TryGetValue(FencingGenerationLabel, out var fencingText) ||
                !long.TryParse(fencingText, NumberStyles.None, CultureInfo.InvariantCulture, out var fencingGeneration) ||
                fencingGeneration < request.MinimumFencingGeneration)
                continue;
            if (!TryGetAnnotation(claim, RecoveryAnnotation, out var recoveryJson))
                throw new SandboxProviderException(
                    "recovery_binding_missing",
                    "An owned sandbox claim is missing its recovery descriptor.",
                    effectMayHaveApplied: false);
            var recovery = DeserializeRecoveryDescriptor(recoveryJson);
            ValidateRecoveryDescriptor(recovery, request.Fence, ownerFingerprint, fencingGeneration);
            var claimUid = RequiredString(RequiredObject(claim, "metadata"), "uid");
            var resource = new ProviderResourceRef(
                ProviderSeam.Sandbox,
                AgentSandboxProviderMetadata.ProviderId,
                claimUid,
                recovery.ResourceGeneration);
            var binding = BuildBinding(recovery with { ClaimUid = claimUid }, resource);
            observations.Add(await DescribeAsync(
                new SandboxDescribeRequest(
                    request.Fence,
                    resource,
                    fencingGeneration,
                    binding,
                    request.LeaseCreatedAt),
                cancellationToken).ConfigureAwait(false));
        }

        return observations;
    }

    public async Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
        SandboxPartialReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var lease = request.Lease;
        var options = ReadPinnedOptions(lease.ProvisionIntent);
        var recovery = ReadRecoveryIntent(lease, options);
        var ownerFingerprint = GetOwnerFingerprint(request.Fence.Owner);
        var names = GetResourceNames(
            ownerFingerprint,
            request.Fence,
            lease.Fence.LifecycleGeneration,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId,
            lease.ProvisionIntent.OptionsRevision);
        var labels = BuildResourceLabels(
            ownerFingerprint,
            lease.Fence.LifecycleGeneration,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId);
        var workspace = ReadWorkspaceAttachment(recovery.Workspace, options);
        var claim = await _client.GetAsync(
            ExtensionsApiGroup,
            "sandboxclaims",
            options.Namespace,
            names.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is not null)
            throw new SandboxProviderException(
                "sandbox_partial_claim_present",
                "The exact Sandbox claim exists and must be recovered by its provider UID before release.",
                effectMayHaveApplied: true);

        await EnsureNoPartialClaimChildrenAsync(
            options.Namespace,
            ownerFingerprint,
            lease.OperationId,
            labels,
            cancellationToken).ConfigureAwait(false);

        var template = await _client.GetAsync(
            ExtensionsApiGroup,
            "sandboxtemplates",
            options.Namespace,
            names.TemplateName,
            cancellationToken).ConfigureAwait(false);
        var templateUid = template is null
            ? null
            : ValidateTemplate(
                template.Value,
                recovery.Workspace,
                recovery.EgressSelectorLabels,
                workspace,
                names.TemplateName,
                ownerFingerprint,
                labels,
                options);
        var warmPool = await _client.GetAsync(
            ExtensionsApiGroup,
            "sandboxwarmpools",
            options.Namespace,
            names.WarmPoolName,
            cancellationToken).ConfigureAwait(false);
        var warmPoolUid = warmPool is null
            ? null
            : ValidateWarmPool(
                warmPool.Value,
                options.Namespace,
                names.WarmPoolName,
                names.TemplateName,
                ownerFingerprint,
                labels);

        var effectsMayHaveApplied = false;
        try
        {
            if (warmPoolUid is not null)
            {
                effectsMayHaveApplied = true;
                _ = await DeleteOwnedResourceAsync(
                    ExtensionsApiGroup,
                    "sandboxwarmpools",
                    options.Namespace,
                    names.WarmPoolName,
                    warmPoolUid,
                    ownerFingerprint,
                    lease.Fence.LifecycleGeneration,
                    lease.ResourceGeneration,
                    lease.ProviderFencingGeneration,
                    lease.OperationId.ToString("N"),
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            if (templateUid is not null)
            {
                effectsMayHaveApplied = true;
                _ = await DeleteOwnedResourceAsync(
                    ExtensionsApiGroup,
                    "sandboxtemplates",
                    options.Namespace,
                    names.TemplateName,
                    templateUid,
                    ownerFingerprint,
                    lease.Fence.LifecycleGeneration,
                    lease.ResourceGeneration,
                    lease.ProviderFencingGeneration,
                    lease.OperationId.ToString("N"),
                    options,
                    cancellationToken).ConfigureAwait(false);
            }

            var remainingClaim = await _client.GetAsync(
                ExtensionsApiGroup,
                "sandboxclaims",
                options.Namespace,
                names.ClaimName,
                cancellationToken).ConfigureAwait(false);
            if (remainingClaim is not null)
                throw new SandboxProviderException(
                    "sandbox_partial_claim_present",
                    "The exact Sandbox claim appeared during partial-resource retirement.",
                    effectMayHaveApplied: true);
            await EnsureNoPartialClaimChildrenAsync(
                options.Namespace,
                ownerFingerprint,
                lease.OperationId,
                labels,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException exception)
        {
            throw new SandboxProviderException(
                exception.Code,
                exception.Message,
                effectsMayHaveApplied || exception.EffectMayHaveApplied,
                exception);
        }

        return new SandboxPartialReleaseReceipt(
            lease.OperationId,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.CurrentFencingGeneration,
            lease.LeaseRevision,
            lease.ReleaseIdempotencyKey!,
            options.Namespace,
            names.ClaimName,
            names.TemplateName,
            templateUid,
            names.WarmPoolName,
            warmPoolUid,
            ClaimAbsent: true,
            SandboxesAbsent: true,
            PodsAbsent: true,
            Disposition: templateUid is null && warmPoolUid is null
                ? SandboxReleaseDisposition.KnownOwnedAbsent
                : SandboxReleaseDisposition.Released).ValidateFor(request);
    }

    private AgentSandboxOptions ReadPinnedOptions(SandboxLeaseProvisionIntent pinnedIntent)
    {
        AgentSandboxOptions options;
        try
        {
            options = JsonSerializer.Deserialize<AgentSandboxOptions>(
                    pinnedIntent.OptionsSnapshot,
                    JsonOptions)
                ?? throw new JsonException("The pinned Sandbox options are empty.");
        }
        catch (JsonException exception)
        {
            throw new SandboxProviderException(
                "sandbox_recovery_options_invalid",
                "The pinned Sandbox options are invalid.",
                effectMayHaveApplied: false,
                exception);
        }
        options.Validate();
        if (!string.Equals(
                pinnedIntent.ProviderId,
                AgentSandboxProviderMetadata.ProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                pinnedIntent.AdapterVersion,
                AgentSandboxProviderMetadata.AdapterVersion.ToString(),
                StringComparison.Ordinal) ||
            pinnedIntent.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            !string.Equals(pinnedIntent.OptionsRevision, options.OptionsRevision, StringComparison.Ordinal))
            throw new SandboxProviderException(
                "sandbox_recovery_binding_unavailable",
                "The exact selected Sandbox options or adapter revision is not available for recovery.",
                effectMayHaveApplied: false);
        return options;
    }

    private static AgentSandboxRecoveryIntent ReadRecoveryIntent(
        SandboxLeaseSnapshot lease,
        AgentSandboxOptions options)
    {
        AgentSandboxRecoveryIntent recovery;
        try
        {
            recovery = JsonSerializer.Deserialize<AgentSandboxRecoveryIntent>(
                    lease.ProvisionIntent.ProviderRequest,
                    JsonOptions)
                ?? throw new JsonException("The stored Sandbox provider recovery intent is empty.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new SandboxProviderException(
                "sandbox_recovery_intent_invalid",
                "The stored Sandbox provider recovery request is invalid.",
                effectMayHaveApplied: false,
                exception);
        }

        if (recovery.ContractVersion != 1 ||
            recovery.Workspace is null ||
            recovery.EgressSelectorLabels is null)
            throw ProviderBindingMismatch();
        var workspace = recovery.Workspace.Validate();
        var negotiation = workspace.Negotiation;
        var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
            lease.ProvisionIntent.ProviderId,
            lease.ProvisionIntent.OptionsRevision,
            lease.Fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId);
        ValidateEgressSelectorLabels(recovery.EgressSelectorLabels);
        _ = ReadWorkspaceAttachment(workspace, options);
        if (negotiation.EnvironmentFence != lease.Fence ||
            negotiation.SandboxResource != plannedResource ||
            negotiation.Volume.ProjectId != lease.Fence.Owner.ProjectId ||
            !string.Equals(
                negotiation.StorageResource.ProviderId,
                options.WorkspaceStorageProviderId,
                StringComparison.Ordinal))
            throw ProviderBindingMismatch();
        return recovery with { Workspace = workspace };
    }

    private async Task EnsureNoPartialClaimChildrenAsync(
        string kubernetesNamespace,
        string ownerFingerprint,
        Guid operationId,
        ImmutableDictionary<string, string> labels,
        CancellationToken cancellationToken)
    {
        var selector = ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            labels.Where(pair => pair.Key is ManagedLabel or OwnerLabel or OperationLabel));
        var sandboxes = await _client.ListAsync(
            SandboxesApiGroup,
            "sandboxes",
            kubernetesNamespace,
            selector,
            cancellationToken).ConfigureAwait(false);
        var pods = await _client.ListAsync(
            string.Empty,
            "pods",
            kubernetesNamespace,
            selector,
            cancellationToken).ConfigureAwait(false);
        if (sandboxes.Count != 0 || pods.Count != 0)
            throw new SandboxProviderException(
                "sandbox_partial_children_present",
                "The Sandbox operation still has Kubernetes children without its claim.",
                effectMayHaveApplied: false);
    }

    public async Task<SandboxReleaseReceipt> ReleaseAsync(
        SandboxReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var (options, descriptor) = ReadBinding(
            request.Fence,
            request.Resource,
            request.FencingGeneration,
            request.ProviderBinding);
        var claimInitiallyPresent = await _client.GetAsync(
            ExtensionsApiGroup,
            "sandboxclaims",
            descriptor.Namespace,
            descriptor.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claimInitiallyPresent is not null)
        {
            ValidateClaimAgainstDescriptor(claimInitiallyPresent.Value, request.Resource, descriptor);
            await _client.DeleteAsync(
                ExtensionsApiGroup,
                "sandboxclaims",
                descriptor.Namespace,
                descriptor.ClaimName,
                request.Resource.ResourceId,
                foreground: true,
                cancellationToken).ConfigureAwait(false);
            await WaitForAbsentAsync(
                ExtensionsApiGroup,
                "sandboxclaims",
                descriptor.Namespace,
                descriptor.ClaimName,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        await WaitForClaimChildrenAbsentAsync(descriptor, options, cancellationToken).ConfigureAwait(false);
        var poolExisted = await DeleteOwnedResourceAsync(
            ExtensionsApiGroup,
            "sandboxwarmpools",
            descriptor.Namespace,
            descriptor.WarmPoolName,
            descriptor.WarmPoolUid,
            descriptor.OwnerFingerprint,
            descriptor.LifecycleGeneration,
            descriptor.ResourceGeneration,
            descriptor.FencingGeneration,
            descriptor.OperationId,
            options,
            cancellationToken).ConfigureAwait(false);
        var templateExisted = await DeleteOwnedResourceAsync(
            ExtensionsApiGroup,
            "sandboxtemplates",
            descriptor.Namespace,
            descriptor.TemplateName,
            descriptor.TemplateUid,
            descriptor.OwnerFingerprint,
            descriptor.LifecycleGeneration,
            descriptor.ResourceGeneration,
            descriptor.FencingGeneration,
            descriptor.OperationId,
            options,
            cancellationToken).ConfigureAwait(false);

        return new SandboxReleaseReceipt(
            request.Resource,
            request.IdempotencyKey,
            claimInitiallyPresent is not null || poolExisted || templateExisted
                ? SandboxReleaseDisposition.Released
                : SandboxReleaseDisposition.KnownOwnedAbsent).ValidateFor(request);
    }

    private static void ValidateCandidate(ProviderCandidate candidate, AgentSandboxOptions options)
    {
        if (candidate.Seam != ProviderSeam.Sandbox ||
            !string.Equals(candidate.ProviderId, AgentSandboxProviderMetadata.ProviderId, StringComparison.Ordinal) ||
            candidate.AdapterVersion != AgentSandboxProviderMetadata.AdapterVersion ||
            candidate.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, options.OptionsRevision, StringComparison.Ordinal))
            throw new SandboxProviderException(
                "provider_candidate_mismatch",
                "The effective Sandbox candidate does not match this configured adapter revision.",
                effectMayHaveApplied: false);

        if (!AgentSandboxProviderMetadata.CreateRegistration(options).Descriptor.AdvertisedCapabilities
                .IsSupersetOf(candidate.RequiredCapabilities))
            throw new SandboxProviderException(
                "sandbox_capability_unsupported",
                "The selected Sandbox provider does not verify every required capability.",
                effectMayHaveApplied: false);
    }

    private static AgentSandboxPersistentVolumeClaimAttachment ReadWorkspaceAttachment(
        SandboxWorkspaceAttachment workspace,
        AgentSandboxOptions options)
    {
        AgentSandboxPersistentVolumeClaimAttachment descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<AgentSandboxPersistentVolumeClaimAttachment>(
                    workspace.ProviderAttachmentDescriptor,
                    JsonOptions)
                ?? throw new JsonException("Workspace attachment descriptor is empty.");
            descriptor.ValidateFor(workspace.Negotiation);
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new SandboxProviderException(
                "workspace_attachment_invalid",
                "The negotiated workspace PVC descriptor is invalid.",
                effectMayHaveApplied: false,
                exception);
        }

        if (workspace.Negotiation.Protocol != WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim ||
            !string.Equals(
                workspace.Negotiation.StorageResource.ProviderId,
                options.WorkspaceStorageProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(descriptor.Namespace, options.Namespace, StringComparison.Ordinal))
            throw new SandboxProviderException(
                "workspace_attachment_unsupported",
                "The selected workspace attachment is not supported by this Sandbox provider.",
                effectMayHaveApplied: false);
        return descriptor;
    }

    private async Task VerifyRuntimeClassAsync(
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        var runtimeClass = await _client.GetClusterAsync(
            "node.k8s.io",
            "v1",
            "runtimeclasses",
            options.RuntimeClassName,
            cancellationToken).ConfigureAwait(false);
        if (runtimeClass is null ||
            !string.Equals(
                ReadOptionalString(runtimeClass.Value, "handler"),
                options.ExpectedRuntimeHandler,
                StringComparison.Ordinal))
            throw new SandboxProviderException(
                "runtime_class_unverified",
                "The configured Kubernetes RuntimeClass does not map to the required VM handler.",
                effectMayHaveApplied: false);
    }

    private async Task<string> VerifyWorkspaceClaimAsync(
        EnvironmentGenerationFence fence,
        WorkspaceVolumeAttachmentNegotiation negotiation,
        AgentSandboxPersistentVolumeClaimAttachment descriptor,
        CancellationToken cancellationToken)
    {
        var claim = await _client.GetAsync(
            string.Empty,
            "persistentvolumeclaims",
            descriptor.Namespace,
            descriptor.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
            throw new SandboxProviderException(
                "workspace_claim_missing",
                "The negotiated workspace PVC does not exist.",
                effectMayHaveApplied: false);
        return ValidateWorkspaceClaim(claim.Value, fence, negotiation, descriptor, requireBound: true);
    }

    private static string ValidateWorkspaceClaim(
        JsonElement claim,
        EnvironmentGenerationFence fence,
        WorkspaceVolumeAttachmentNegotiation negotiation,
        AgentSandboxPersistentVolumeClaimAttachment descriptor,
        bool requireBound)
    {
        var metadata = RequiredObject(claim, "metadata");
        var annotations = ReadStringDictionary(metadata, "annotations");
        var expectedGeneration = negotiation.Volume.ResourceGeneration.ToString(CultureInfo.InvariantCulture);
        var expectedEnvironment = fence.Owner.EnvironmentId;
        var uid = RequiredString(metadata, "uid");
        if (!string.Equals(RequiredString(metadata, "namespace"), descriptor.Namespace, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(metadata, "name"), descriptor.ClaimName, StringComparison.Ordinal) ||
            !string.Equals(uid, descriptor.ClaimUid, StringComparison.Ordinal) ||
            !HasAnnotation(annotations, "agentweaver.dev/project-id", fence.Owner.ProjectId) ||
            !HasAnnotation(annotations, "agentweaver.dev/volume-id", negotiation.Volume.VolumeId) ||
            !HasAnnotation(annotations, "agentweaver.dev/generation", expectedGeneration) ||
            !HasAnnotation(annotations, "agentweaver.dev/environment-id", expectedEnvironment))
            throw new SandboxProviderException(
                "workspace_claim_mismatch",
                "The PVC UID or owner annotations do not match the negotiated workspace generation.",
                effectMayHaveApplied: false);
        if (requireBound &&
            !string.Equals(
                ReadOptionalString(claim, "status", "phase"),
                "Bound",
                StringComparison.Ordinal))
            throw new SandboxProviderException(
                "workspace_claim_not_bound",
                "The negotiated workspace PVC is not Bound.",
                effectMayHaveApplied: false);
        return GetWorkspaceFingerprint(
            annotations["agentweaver.dev/project-id"],
            annotations["agentweaver.dev/volume-id"],
            annotations["agentweaver.dev/generation"],
            annotations["agentweaver.dev/environment-id"]);
    }

    private static JsonElement BuildTemplate(
        SandboxProvisionRequest request,
        AgentSandboxPersistentVolumeClaimAttachment workspace,
        string name,
        ImmutableDictionary<string, string> resourceLabels,
        AgentSandboxOptions options)
    {
        var podLabels = request.EgressSelectorLabels.ToBuilder();
        foreach (var label in resourceLabels)
            podLabels[label.Key] = label.Value;

        var template = JsonSerializer.SerializeToElement(new
        {
            apiVersion = "extensions.agents.x-k8s.io/v1beta1",
            kind = "SandboxTemplate",
            metadata = new
            {
                name,
                @namespace = options.Namespace,
                labels = resourceLabels,
                annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [OwnerAnnotation] = resourceLabels[OwnerLabel]
                }
            },
            spec = new
            {
                networkPolicyManagement = "Unmanaged",
                envVarsInjectionPolicy = "Disallowed",
                volumeClaimTemplatesPolicy = "Disallowed",
                service = false,
                podTemplate = new
                {
                    metadata = new { labels = podLabels.ToImmutable() },
                    spec = new
                    {
                        automountServiceAccountToken = false,
                        hostNetwork = false,
                        restartPolicy = "Never",
                        runtimeClassName = options.RuntimeClassName,
                        securityContext = new
                        {
                            runAsNonRoot = true,
                            runAsUser = 1000,
                            runAsGroup = 1000,
                            seccompProfile = new { type = "RuntimeDefault" }
                        },
                        containers = new[]
                        {
                            new
                            {
                                name = WorkspaceContainerName,
                                image = options.ContainerImage,
                                imagePullPolicy = "IfNotPresent",
                                resources = new
                                {
                                    requests = new Dictionary<string, string>
                                    {
                                        ["cpu"] = options.CpuRequest,
                                        ["memory"] = options.MemoryRequest
                                    },
                                    limits = new Dictionary<string, string>
                                    {
                                        ["cpu"] = options.CpuRequest,
                                        ["memory"] = options.MemoryRequest
                                    }
                                },
                                securityContext = new
                                {
                                    allowPrivilegeEscalation = false,
                                    runAsNonRoot = true,
                                    runAsUser = 1000,
                                    runAsGroup = 1000,
                                    readOnlyRootFilesystem = true,
                                    capabilities = new { drop = new[] { "ALL" } },
                                    seccompProfile = new { type = "RuntimeDefault" }
                                },
                                volumeMounts = new[]
                                {
                                    new
                                    {
                                        name = WorkspaceVolumeName,
                                        mountPath = request.Workspace.Negotiation.MountPath,
                                        readOnly = request.Workspace.Negotiation.ReadOnly
                                    }
                                }
                            }
                        },
                        volumes = new[]
                        {
                            new
                            {
                                name = WorkspaceVolumeName,
                                persistentVolumeClaim = new
                                {
                                    claimName = workspace.ClaimName,
                                    readOnly = request.Workspace.Negotiation.ReadOnly
                                }
                            }
                        }
                    }
                }
            }
        });
        if (options.AgentHost is null)
            return template;
        var document = JsonNode.Parse(template.GetRawText())!;
        ApplyAgentHostProfile(document["spec"]!["podTemplate"]!["spec"]!.AsObject(),
            options, request.Workspace.Negotiation.MountPath);
        return JsonSerializer.SerializeToElement(document);
    }

    private static void ApplyAgentHostProfile(JsonObject podSpec, AgentSandboxOptions options, string mountPath)
    {
        var host = options.AgentHost ?? throw new ArgumentException("An explicit AgentHost launch profile is required.");
        var podSecurity = podSpec["securityContext"]!.AsObject();
        podSecurity["runAsUser"] = 1000;
        podSecurity["runAsGroup"] = 1000;
        podSecurity["fsGroup"] = 1000;
        podSecurity["fsGroupChangePolicy"] = "OnRootMismatch";
        var container = podSpec["containers"]![0]!.AsObject();
        container["securityContext"]!["runAsUser"] = 1000;
        container["securityContext"]!["runAsGroup"] = 1000;
        container["workingDir"] = "/app";
        container["env"] = JsonSerializer.SerializeToNode(new[]
        {
            new { name = "AgentHost__PrivateStateDirectory", value = "/state" },
            new { name = "AgentHost__WorkingDirectory", value = mountPath },
            new { name = "ASPNETCORE_URLS", value = "https://+:8443" },
            new { name = "ASPNETCORE_Kestrel__Certificates__Default__Path", value = "/run/agenthost-tls/tls.crt" },
            new { name = "ASPNETCORE_Kestrel__Certificates__Default__KeyPath", value = "/run/agenthost-tls/tls.key" }
        });
        container["ports"] = JsonSerializer.SerializeToNode(new[] { new { name = "https", containerPort = 8443 } });
        container["livenessProbe"] = JsonSerializer.SerializeToNode(new
        {
            httpGet = new { path = "/health/live", port = 8443, scheme = "HTTPS" },
            periodSeconds = 10, timeoutSeconds = 5
        });
        container["readinessProbe"] = JsonSerializer.SerializeToNode(new
        {
            httpGet = new { path = "/health/ready", port = 8443, scheme = "HTTPS" },
            periodSeconds = 5, timeoutSeconds = 20, failureThreshold = 1
        });
        container["startupProbe"] = JsonSerializer.SerializeToNode(new
        {
            httpGet = new { path = "/health/live", port = 8443, scheme = "HTTPS" },
            periodSeconds = 5, timeoutSeconds = 5,
            failureThreshold = (int)Math.Ceiling(options.StartupBudgets.TotalSeconds / 5d)
        });
        var volumes = podSpec["volumes"]!.AsArray();
        volumes.Add(JsonSerializer.SerializeToNode(new { name = "agenthost-state", emptyDir = new { } }));
        volumes.Add(JsonSerializer.SerializeToNode(new { name = "agenthost-tmp", emptyDir = new { } }));
        volumes.Add(JsonSerializer.SerializeToNode(new
        {
            name = "agenthost-configuration",
            configMap = new { name = host.ConfigurationMapName, defaultMode = 288,
                items = new[] { new { key = "appsettings.json", path = "appsettings.json" } } }
        }));
        volumes.Add(JsonSerializer.SerializeToNode(new
        {
            name = "agenthost-tls", secret = new { secretName = host.TlsSecretName, defaultMode = 288 }
        }));
        var mounts = container["volumeMounts"]!.AsArray();
        mounts.Add(JsonSerializer.SerializeToNode(new { name = "agenthost-state", mountPath = "/state", readOnly = false }));
        mounts.Add(JsonSerializer.SerializeToNode(new { name = "agenthost-tmp", mountPath = "/tmp", readOnly = false }));
        mounts.Add(JsonSerializer.SerializeToNode(new
        {
            name = "agenthost-configuration", mountPath = "/app/appsettings.json", subPath = "appsettings.json", readOnly = true
        }));
        mounts.Add(JsonSerializer.SerializeToNode(new { name = "agenthost-tls", mountPath = "/run/agenthost-tls", readOnly = true }));
    }

    private JsonElement BuildWarmPool(
        string name,
        string templateName,
        ImmutableDictionary<string, string> labels) =>
        JsonSerializer.SerializeToElement(new
        {
            apiVersion = "extensions.agents.x-k8s.io/v1beta1",
            kind = "SandboxWarmPool",
            metadata = new
            {
                name,
                @namespace = _options.Namespace,
                labels,
                annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [OwnerAnnotation] = labels[OwnerLabel]
                }
            },
            spec = new
            {
                replicas = 0,
                sandboxTemplateRef = new { name = templateName },
                updateStrategy = new { type = "Recreate" }
            }
        });

    private JsonElement BuildClaim(
        string name,
        string warmPoolName,
        ImmutableDictionary<string, string> labels,
        AgentSandboxRecoveryDescriptor recovery) =>
        JsonSerializer.SerializeToElement(new
        {
            apiVersion = "extensions.agents.x-k8s.io/v1beta1",
            kind = "SandboxClaim",
            metadata = new
            {
                name,
                @namespace = _options.Namespace,
                labels,
                annotations = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [OwnerAnnotation] = recovery.OwnerFingerprint,
                    [RecoveryAnnotation] = JsonSerializer.Serialize(recovery, JsonOptions)
                }
            },
            spec = new
            {
                warmPoolRef = new { name = warmPoolName },
                lifecycle = new { shutdownPolicy = "DeleteForeground" }
            }
        });

    private static ImmutableDictionary<string, string> BuildResourceLabels(
        string ownerFingerprint,
        long lifecycleGeneration,
        long resourceGeneration,
        long fencingGeneration,
        Guid operationId) =>
        ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new[]
            {
                new KeyValuePair<string, string>(ManagedLabel, "true"),
                new KeyValuePair<string, string>(OwnerLabel, ownerFingerprint),
                new KeyValuePair<string, string>(
                    LifecycleGenerationLabel,
                    lifecycleGeneration.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    ResourceGenerationLabel,
                    resourceGeneration.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(
                    FencingGenerationLabel,
                    fencingGeneration.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>(OperationLabel, operationId.ToString("N"))
            });

    private async Task<(JsonElement Resource, bool Created)> EnsureResourceAsync(
        string apiGroup,
        string plural,
        string kubernetesNamespace,
        string name,
        JsonElement desired,
        CancellationToken cancellationToken)
    {
        var existing = await _client.GetAsync(
            apiGroup, plural, kubernetesNamespace, name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return (existing.Value, false);
        try
        {
            return (await _client.CreateAsync(
                apiGroup, plural, kubernetesNamespace, desired, cancellationToken).ConfigureAwait(false), true);
        }
        catch (SandboxProviderException exception) when (exception.Code == "kubernetes_conflict")
        {
            existing = await _client.GetAsync(
                apiGroup, plural, kubernetesNamespace, name, cancellationToken).ConfigureAwait(false);
            if (existing is null)
                throw new SandboxProviderException(
                    "kubernetes_conflict_unresolved",
                    "Kubernetes reported a creation conflict but the resource could not be read.",
                    effectMayHaveApplied: true,
                    exception);
            return (existing.Value, false);
        }
    }

    private static string ValidateTemplate(
        JsonElement resource,
        SandboxWorkspaceAttachment workspaceAttachment,
        ImmutableDictionary<string, string> egressSelectorLabels,
        AgentSandboxPersistentVolumeClaimAttachment workspace,
        string name,
        string ownerFingerprint,
        ImmutableDictionary<string, string> labels,
        AgentSandboxOptions options)
    {
        var uid = ValidateResourceMetadata(
            resource, name, options.Namespace, ownerFingerprint, labels);
        var spec = RequiredObject(resource, "spec");
        if (ReadOptionalString(spec, "networkPolicyManagement") != "Unmanaged" ||
            ReadOptionalString(spec, "envVarsInjectionPolicy") != "Disallowed" ||
            ReadOptionalString(spec, "volumeClaimTemplatesPolicy") != "Disallowed" ||
            !TryGetBoolean(spec, "service", out var serviceEnabled) ||
            serviceEnabled)
            throw ProviderResourceMismatch("The owned SandboxTemplate has unsupported policy settings.");

        var podTemplate = RequiredObject(spec, "podTemplate");
        var podLabels = ReadStringDictionary(RequiredObject(podTemplate, "metadata"), "labels");
        foreach (var label in egressSelectorLabels)
            if (!HasAnnotationOrLabel(podLabels, label.Key, label.Value))
                throw ProviderResourceMismatch("The owned Pod template is missing its verified egress selector.");
        foreach (var label in labels)
            if (!HasAnnotationOrLabel(podLabels, label.Key, label.Value))
                throw ProviderResourceMismatch("The owned Pod template is missing an ownership or egress label.");

        var podSpec = RequiredObject(podTemplate, "spec");
        if (!string.Equals(
                ReadOptionalString(podSpec, "runtimeClassName"),
                options.RuntimeClassName,
                StringComparison.Ordinal) ||
            !TryGetBoolean(podSpec, "automountServiceAccountToken", out var automount) ||
            automount ||
            !TryGetBoolean(podSpec, "hostNetwork", out var hostNetwork) ||
            hostNetwork)
            throw ProviderResourceMismatch("The owned Pod template does not enforce its configured isolation.");

        var containers = RequiredArray(podSpec, "containers");
        if (containers.GetArrayLength() != 1)
            throw ProviderResourceMismatch("The owned Pod template must contain exactly one AgentHost container.");
        var container = containers[0];
        if (ReadOptionalString(container, "name") != WorkspaceContainerName ||
            ReadOptionalString(container, "image") != options.ContainerImage)
            throw ProviderResourceMismatch("The owned AgentHost container differs from the pinned provider options.");
        ValidatePodSecurityContext(RequiredObject(podSpec, "securityContext"), container, 1000);
        ValidateContainerResources(container, options);
        ValidateWorkspacePodMount(podSpec, container, workspace, workspaceAttachment.Negotiation);
        ValidateAgentHostProfile(podSpec, container, options, workspaceAttachment.Negotiation.MountPath);
        return uid;
    }

    private static string ValidateWarmPool(
        JsonElement resource,
        string kubernetesNamespace,
        string name,
        string templateName,
        string ownerFingerprint,
        ImmutableDictionary<string, string> labels)
    {
        var uid = ValidateResourceMetadata(
            resource, name, kubernetesNamespace, ownerFingerprint, labels);
        var spec = RequiredObject(resource, "spec");
        if (!TryGetInt64(spec, "replicas", out var replicas) ||
            replicas != 0 ||
            ReadOptionalString(spec, "sandboxTemplateRef", "name") != templateName ||
            ReadOptionalString(spec, "updateStrategy", "type") != "Recreate")
            throw ProviderResourceMismatch("The owned SandboxWarmPool is not a zero-replica pool for its exact template.");
        return uid;
    }

    private static string ValidateClaim(
        JsonElement resource,
        string name,
        string warmPoolName,
        string ownerFingerprint,
        ImmutableDictionary<string, string> labels,
        AgentSandboxRecoveryDescriptor recovery)
    {
        var uid = ValidateResourceMetadata(
            resource, name, recovery.Namespace, ownerFingerprint, labels);
        var spec = RequiredObject(resource, "spec");
        if (ReadOptionalString(spec, "warmPoolRef", "name") != warmPoolName ||
            ReadOptionalString(spec, "lifecycle", "shutdownPolicy") != "DeleteForeground" ||
            !TryGetAnnotation(resource, RecoveryAnnotation, out var recoveryJson) ||
            !string.Equals(recoveryJson, JsonSerializer.Serialize(recovery, JsonOptions), StringComparison.Ordinal))
            throw ProviderResourceMismatch("The owned SandboxClaim differs from its fenced recovery descriptor.");
        return uid;
    }

    private static string ValidateResourceMetadata(
        JsonElement resource,
        string expectedName,
        string? expectedNamespace,
        string ownerFingerprint,
        ImmutableDictionary<string, string> expectedLabels)
    {
        var metadata = RequiredObject(resource, "metadata");
        if (!string.Equals(
                RequiredString(metadata, "name"),
                expectedName,
                StringComparison.Ordinal) ||
            expectedNamespace is not null &&
                !string.Equals(
                    RequiredString(metadata, "namespace"),
                    expectedNamespace,
                    StringComparison.Ordinal) ||
            !string.Equals(
                ReadOptionalString(metadata, "annotations", OwnerAnnotation),
                ownerFingerprint,
                StringComparison.Ordinal))
            throw ProviderResourceMismatch("The Kubernetes object identity or owner does not match this sandbox lease.");

        var actualLabels = ReadStringDictionary(metadata, "labels");
        foreach (var expected in expectedLabels)
            if (!actualLabels.TryGetValue(expected.Key, out var value) ||
                !string.Equals(value, expected.Value, StringComparison.Ordinal))
                throw ProviderResourceMismatch("The Kubernetes object ownership labels do not match this sandbox lease.");
        return RequiredString(metadata, "uid");
    }

    private static void ValidatePodSecurityContext(JsonElement podSecurityContext, JsonElement container, int user)
    {
        if (!TryGetBoolean(podSecurityContext, "runAsNonRoot", out var podNonRoot) ||
            !podNonRoot ||
            !TryGetInt64(podSecurityContext, "runAsUser", out var podUser) ||
            podUser != user ||
            !TryGetInt64(podSecurityContext, "runAsGroup", out var podGroup) ||
            podGroup != user ||
            ReadOptionalString(podSecurityContext, "seccompProfile", "type") != "RuntimeDefault")
            throw ProviderResourceMismatch("The owned Pod security context is not restricted.");

        var security = RequiredObject(container, "securityContext");
        var capabilities = RequiredObject(security, "capabilities");
        var dropped = RequiredArray(capabilities, "drop");
        if (!TryGetBoolean(security, "allowPrivilegeEscalation", out var privilegeEscalation) ||
            privilegeEscalation ||
            !TryGetBoolean(security, "runAsNonRoot", out var containerNonRoot) ||
            !containerNonRoot ||
            !TryGetInt64(security, "runAsUser", out var containerUser) ||
            containerUser != user ||
            !TryGetInt64(security, "runAsGroup", out var containerGroup) ||
            containerGroup != user ||
            !TryGetBoolean(security, "readOnlyRootFilesystem", out var readOnlyRoot) ||
            !readOnlyRoot ||
            ReadOptionalString(security, "seccompProfile", "type") != "RuntimeDefault" ||
            dropped.GetArrayLength() != 1 ||
            dropped[0].ValueKind != JsonValueKind.String ||
            dropped[0].GetString() != "ALL")
            throw ProviderResourceMismatch("The owned AgentHost container security context is not restricted.");
    }

    private static void ValidateAgentHostProfile(
        JsonElement podSpec, JsonElement container, AgentSandboxOptions options, string mountPath)
    {
        if (options.AgentHost is null)
            return;
        var actual = JsonNode.Parse(podSpec.GetRawText())!.AsObject();
        var volumes = RequiredArray(podSpec, "volumes");
        var mounts = RequiredArray(container, "volumeMounts");
        var workspaceVolume = volumes.EnumerateArray().Single(volume => ReadOptionalString(volume, "name") == WorkspaceVolumeName);
        var workspaceMount = mounts.EnumerateArray().Single(mount => ReadOptionalString(mount, "name") == WorkspaceVolumeName);
        var expected = new JsonObject
        {
            ["securityContext"] = new JsonObject(),
            ["volumes"] = new JsonArray(JsonNode.Parse(workspaceVolume.GetRawText())),
            ["containers"] = new JsonArray(new JsonObject
            {
                ["securityContext"] = new JsonObject(),
                ["volumeMounts"] = new JsonArray(JsonNode.Parse(workspaceMount.GetRawText()))
            })
        };
        ApplyAgentHostProfile(expected, options, mountPath);
        if (!ContainsLaunchConfiguration(actual, expected))
            throw ProviderResourceMismatch("The owned AgentHost launch configuration differs from its pinned profile.");
    }

    private static bool ContainsLaunchConfiguration(JsonNode? actual, JsonNode? expected) => expected switch
    {
        JsonObject properties => actual is JsonObject candidate &&
            properties.All(property => candidate.TryGetPropertyValue(property.Key, out var value) &&
                ContainsLaunchConfiguration(value, property.Value)),
        JsonArray values => actual is JsonArray candidate && candidate.Count == values.Count &&
            values.Select((value, index) => ContainsLaunchConfiguration(candidate[index], value)).All(match => match),
        _ => JsonNode.DeepEquals(actual, expected)
    };

    private static void ValidateContainerResources(JsonElement container, AgentSandboxOptions options)
    {
        var resources = RequiredObject(container, "resources");
        var requests = RequiredObject(resources, "requests");
        var limits = RequiredObject(resources, "limits");
        if (ReadOptionalString(requests, "cpu") != options.CpuRequest ||
            ReadOptionalString(requests, "memory") != options.MemoryRequest ||
            ReadOptionalString(limits, "cpu") != options.CpuRequest ||
            ReadOptionalString(limits, "memory") != options.MemoryRequest)
            throw ProviderResourceMismatch("The owned AgentHost container resources differ from pinned options.");
    }

    private static void ValidateWorkspacePodMount(
        JsonElement podSpec,
        JsonElement container,
        AgentSandboxPersistentVolumeClaimAttachment workspace,
        WorkspaceVolumeAttachmentNegotiation negotiation)
    {
        var volumes = RequiredArray(podSpec, "volumes");
        var workspaceVolume = volumes.EnumerateArray().SingleOrDefault(volume =>
            ReadOptionalString(volume, "name") == WorkspaceVolumeName);
        if (workspaceVolume.ValueKind != JsonValueKind.Object ||
            ReadOptionalString(workspaceVolume, "persistentVolumeClaim", "claimName") != workspace.ClaimName ||
            !TryGetBoolean(workspaceVolume, "persistentVolumeClaim", "readOnly", out var volumeReadOnly) ||
            volumeReadOnly != negotiation.ReadOnly)
            throw ProviderResourceMismatch("The owned Pod template does not mount the exact workspace PVC.");

        var mounts = RequiredArray(container, "volumeMounts");
        var workspaceMount = mounts.EnumerateArray().SingleOrDefault(mount =>
            ReadOptionalString(mount, "name") == WorkspaceVolumeName);
        if (workspaceMount.ValueKind != JsonValueKind.Object ||
            ReadOptionalString(workspaceMount, "mountPath") != negotiation.MountPath ||
            !TryGetBoolean(workspaceMount, "readOnly", out var mountReadOnly) ||
            mountReadOnly != negotiation.ReadOnly)
            throw ProviderResourceMismatch("The owned Pod template does not mount the workspace at its negotiated path.");
    }

    private static void ValidateClaimAgainstDescriptor(
        JsonElement claim,
        ProviderResourceRef resource,
        AgentSandboxRecoveryDescriptor descriptor)
    {
        var labels = BuildResourceLabels(
            descriptor.OwnerFingerprint,
            descriptor.LifecycleGeneration,
            descriptor.ResourceGeneration,
            descriptor.FencingGeneration,
            Guid.ParseExact(descriptor.OperationId, "N"));
        _ = ValidateResourceMetadata(
            claim,
            descriptor.ClaimName,
            descriptor.Namespace,
            descriptor.OwnerFingerprint,
            labels);
        var metadata = RequiredObject(claim, "metadata");
        if (!string.Equals(RequiredString(metadata, "uid"), resource.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(claim, "spec", "warmPoolRef", "name"),
                descriptor.WarmPoolName, StringComparison.Ordinal) ||
            !TryGetAnnotation(claim, RecoveryAnnotation, out var recoveryJson) ||
            !RecoveryDescriptorMatches(recoveryJson, descriptor with { ClaimUid = null }))
            throw ProviderResourceMismatch("The current claim does not match the exact pinned sandbox owner and generation.");
    }

    private static bool RecoveryDescriptorMatches(
        string recoveryJson,
        AgentSandboxRecoveryDescriptor expected)
    {
        try
        {
            var actual = JsonNode.Parse(recoveryJson);
            var expectedJson = JsonSerializer.SerializeToNode(expected, JsonOptions);
            return actual is not null &&
                   expectedJson is not null &&
                   JsonNode.DeepEquals(actual, expectedJson);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ValidateSandboxOwner(JsonElement sandbox, string claimUid)
    {
        var metadata = RequiredObject(sandbox, "metadata");
        if (!HasOwnerReference(metadata, claimUid))
            throw ProviderResourceMismatch("The Agent Sandbox resource is not controlled by the exact SandboxClaim UID.");
    }

    private static SandboxTerminalEvidence? TryReadTerminalEvidence(
        JsonElement sandbox,
        string claimUid,
        long fencingGeneration,
        DateTimeOffset observedAt)
    {
        var metadata = RequiredObject(sandbox, "metadata");
        var sandboxUid = RequiredString(metadata, "uid");
        var conditions = TryGetArray(RequiredObject(sandbox, "status"), "conditions");
        if (conditions is not { } values)
            return null;
        foreach (var condition in values.EnumerateArray())
        {
            if (ReadOptionalString(condition, "type") != "Finished" ||
                ReadOptionalString(condition, "status") != "True" ||
                !TryGetInt64(condition, "observedGeneration", out var observedGeneration) ||
                !TryGetInt64(metadata, "generation", out var resourceGeneration) ||
                observedGeneration < 1 ||
                observedGeneration != resourceGeneration ||
                !TryGetDateTime(condition, "lastTransitionTime", out var transitionedAt))
                continue;
            var reason = ReadOptionalString(condition, "reason") switch
            {
                "PodSucceeded" => SandboxTerminalReason.PodSucceeded,
                "PodFailed" => SandboxTerminalReason.PodFailed,
                _ => (SandboxTerminalReason?)null
            };
            if (reason is null)
                continue;
            return new SandboxTerminalEvidence(
                1,
                claimUid,
                sandboxUid,
                fencingGeneration,
                observedGeneration,
                reason.Value,
                transitionedAt <= observedAt ? transitionedAt : observedAt).Validate();
        }
        return null;
    }

    private static void ValidatePodOwner(
        JsonElement pod,
        string sandboxUid,
        string claimUid,
        AgentSandboxRecoveryDescriptor descriptor)
    {
        var metadata = RequiredObject(pod, "metadata");
        var labels = ReadStringDictionary(metadata, "labels");
        if (!HasOwnerReference(metadata, sandboxUid) ||
            !labels.TryGetValue(ClaimUidLabel, out var actualClaimUid) ||
            !string.Equals(actualClaimUid, claimUid, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The backing Pod is not owned by the observed Sandbox.");
        foreach (var label in descriptor.EgressSelectorLabels)
            if (!labels.TryGetValue(label.Key, out var value) ||
                !string.Equals(value, label.Value, StringComparison.Ordinal))
                throw ProviderResourceMismatch("The backing Pod does not match its pinned egress selector.");
        var operationId = Guid.ParseExact(descriptor.OperationId, "N");
        var resourceLabels = BuildResourceLabels(
            descriptor.OwnerFingerprint,
            descriptor.LifecycleGeneration,
            descriptor.ResourceGeneration,
            descriptor.FencingGeneration,
            operationId);
        foreach (var label in resourceLabels)
            if (!labels.TryGetValue(label.Key, out var value) ||
                !string.Equals(value, label.Value, StringComparison.Ordinal))
                throw ProviderResourceMismatch("The backing Pod ownership labels do not match its Sandbox claim.");
    }

    private async Task<bool> VerifyAttachedPodAsync(
        JsonElement pod,
        EnvironmentGenerationFence fence,
        AgentSandboxRecoveryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var podSpec = RequiredObject(pod, "spec");
        var container = RequiredArray(podSpec, "containers").EnumerateArray()
            .SingleOrDefault(candidate => ReadOptionalString(candidate, "name") == WorkspaceContainerName);
        if (container.ValueKind != JsonValueKind.Object)
            return false;
        var volumes = RequiredArray(podSpec, "volumes");
        var volume = volumes.EnumerateArray().SingleOrDefault(candidate =>
            ReadOptionalString(candidate, "name") == WorkspaceVolumeName);
        var mounts = RequiredArray(container, "volumeMounts");
        var mount = mounts.EnumerateArray().SingleOrDefault(candidate =>
            ReadOptionalString(candidate, "name") == WorkspaceVolumeName);
        if (volume.ValueKind != JsonValueKind.Object ||
            mount.ValueKind != JsonValueKind.Object ||
            ReadOptionalString(volume, "persistentVolumeClaim", "claimName") != descriptor.WorkspaceClaimName ||
            ReadOptionalString(mount, "mountPath") != descriptor.WorkspaceMountPath ||
            !TryGetBoolean(volume, "persistentVolumeClaim", "readOnly", out var volumeReadOnly) ||
            volumeReadOnly != descriptor.WorkspaceReadOnly ||
            !TryGetBoolean(mount, "readOnly", out var mountReadOnly) ||
            mountReadOnly != descriptor.WorkspaceReadOnly)
            return false;

        var claim = await _client.GetAsync(
            string.Empty,
            "persistentvolumeclaims",
            descriptor.WorkspaceNamespace,
            descriptor.WorkspaceClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
            return false;
        return ValidateWorkspaceClaimForFence(
            claim.Value,
            fence,
            descriptor);
    }

    private static bool ValidateWorkspaceClaimForFence(
        JsonElement claim,
        EnvironmentGenerationFence fence,
        AgentSandboxRecoveryDescriptor descriptor)
    {
        var metadata = RequiredObject(claim, "metadata");
        if (!string.Equals(RequiredString(metadata, "namespace"), descriptor.WorkspaceNamespace, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(metadata, "name"), descriptor.WorkspaceClaimName, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(metadata, "uid"), descriptor.WorkspaceClaimUid, StringComparison.Ordinal) ||
            !string.Equals(ReadOptionalString(claim, "status", "phase"), "Bound", StringComparison.Ordinal))
            return false;
        var annotations = ReadStringDictionary(metadata, "annotations");
        if (!annotations.TryGetValue("agentweaver.dev/project-id", out var projectId) ||
            !string.Equals(projectId, fence.Owner.ProjectId, StringComparison.Ordinal) ||
            !annotations.TryGetValue("agentweaver.dev/environment-id", out var environmentId) ||
            !string.Equals(environmentId, fence.Owner.EnvironmentId, StringComparison.Ordinal) ||
            !annotations.TryGetValue("agentweaver.dev/volume-id", out var volumeId) ||
            !annotations.TryGetValue("agentweaver.dev/generation", out var generation) ||
            !string.Equals(generation, descriptor.WorkspaceResourceGeneration.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
            return false;
        return string.Equals(
            GetWorkspaceFingerprint(projectId, volumeId, generation, environmentId),
            descriptor.WorkspaceIdentityFingerprint,
            StringComparison.Ordinal);
    }

    private async Task<bool> VerifyPodIsolationAsync(
        JsonElement pod,
        AgentSandboxOptions options,
        string workspaceMountPath,
        CancellationToken cancellationToken)
    {
        var podSpec = RequiredObject(pod, "spec");
        if (!string.Equals(
                ReadOptionalString(podSpec, "runtimeClassName"),
                options.RuntimeClassName,
                StringComparison.Ordinal) ||
            !TryGetBoolean(podSpec, "automountServiceAccountToken", out var automount) ||
            automount ||
            !TryGetBoolean(podSpec, "hostNetwork", out var hostNetwork) ||
            hostNetwork)
            return false;
        if (options.AgentHost is not null)
        {
            var containers = RequiredArray(podSpec, "containers");
            if (containers.GetArrayLength() != 1)
                return false;
            ValidatePodSecurityContext(RequiredObject(podSpec, "securityContext"), containers[0], 1000);
            ValidateAgentHostProfile(podSpec, containers[0], options, workspaceMountPath);
        }
        try
        {
            await VerifyRuntimeClassAsync(options, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SandboxProviderException exception) when (exception.Code == "runtime_class_unverified")
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<JsonElement>> FindClaimSandboxesAsync(
        AgentSandboxRecoveryDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var sandboxes = await _client.ListAsync(
            SandboxesApiGroup,
            "sandboxes",
            descriptor.Namespace,
            ImmutableDictionary<string, string>.Empty,
            cancellationToken).ConfigureAwait(false);
        var result = new List<JsonElement>();
        foreach (var sandbox in sandboxes)
        {
            var metadata = RequiredObject(sandbox, "metadata");
            if (HasOwnerReference(metadata, descriptor.ClaimUid!))
                result.Add(sandbox);
        }
        return result;
    }

    private Task<IReadOnlyList<JsonElement>> FindClaimPodsAsync(
        AgentSandboxRecoveryDescriptor descriptor,
        CancellationToken cancellationToken) =>
        _client.ListAsync(
            string.Empty,
            "pods",
            descriptor.Namespace,
            ImmutableDictionary<string, string>.Empty.Add(ClaimUidLabel, descriptor.ClaimUid!),
            cancellationToken);

    private async Task<bool> DeleteOwnedResourceAsync(
        string apiGroup,
        string plural,
        string kubernetesNamespace,
        string name,
        string expectedUid,
        string ownerFingerprint,
        long lifecycleGeneration,
        long resourceGeneration,
        long fencingGeneration,
        string operationId,
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        var resource = await _client.GetAsync(
            apiGroup, plural, kubernetesNamespace, name, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            return false;
        var operation = Guid.ParseExact(operationId, "N");
        _ = ValidateResourceMetadata(
            resource.Value,
            name,
            kubernetesNamespace,
            ownerFingerprint,
            BuildResourceLabels(
                ownerFingerprint,
                lifecycleGeneration,
                resourceGeneration,
                fencingGeneration,
                operation));
        var metadata = RequiredObject(resource.Value, "metadata");
        var actualUid = RequiredString(metadata, "uid");
        if (!string.Equals(actualUid, expectedUid, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The current Kubernetes object UID differs from the pinned cleanup target.");

        await _client.DeleteAsync(
            apiGroup,
            plural,
            kubernetesNamespace,
            name,
            expectedUid,
            foreground: true,
            cancellationToken).ConfigureAwait(false);
        await WaitForAbsentAsync(
            apiGroup,
            plural,
            kubernetesNamespace,
            name,
            options,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WaitForAbsentAsync(
        string apiGroup,
        string plural,
        string kubernetesNamespace,
        string name,
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(options.ReconciliationTimeoutSeconds);
        while (true)
        {
            var current = await _client.GetAsync(
                apiGroup, plural, kubernetesNamespace, name, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return;
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new SandboxProviderException(
                    "sandbox_release_unconfirmed",
                    "The exact Kubernetes resource remains after its UID-preconditioned deletion.",
                    effectMayHaveApplied: true);
            await Task.Delay(
                MinDelay(remaining, options.PollIntervalMilliseconds),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForClaimChildrenAbsentAsync(
        AgentSandboxRecoveryDescriptor descriptor,
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(options.ReconciliationTimeoutSeconds);
        while (true)
        {
            var sandboxes = await FindClaimSandboxesAsync(descriptor, cancellationToken).ConfigureAwait(false);
            var pods = await FindClaimPodsAsync(descriptor, cancellationToken).ConfigureAwait(false);
            if (sandboxes.Count == 0 && pods.Count == 0)
                return;
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new SandboxProviderException(
                    "sandbox_children_remain",
                    "Agent Sandbox resources remain after foreground claim deletion.",
                    effectMayHaveApplied: true);
            await Task.Delay(
                MinDelay(remaining, options.PollIntervalMilliseconds),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan MinDelay(TimeSpan remaining, int pollIntervalMilliseconds)
    {
        var poll = TimeSpan.FromMilliseconds(pollIntervalMilliseconds);
        return remaining < poll ? remaining : poll;
    }

    private SandboxObservation PendingObservation(
        SandboxDescribeRequest request,
        AgentSandboxRecoveryDescriptor descriptor,
        ImmutableArray<SandboxStartupPhaseObservation> phases,
        bool workspaceVerified,
        bool isolationVerified)
    {
        var options = JsonSerializer.Deserialize<AgentSandboxOptions>(descriptor.OptionsSnapshot, JsonOptions)
            ?? throw ProviderBindingMismatch();
        var startupFailure = EvaluateStartupBudget(
            request.LeaseCreatedAt,
            phases,
            options.StartupBudgets,
            _timeProvider.GetUtcNow());
        return new SandboxObservation(
            request.Resource,
            startupFailure is null ? SandboxObservedState.Pending : SandboxObservedState.Failed,
            request.FencingGeneration,
            isolationVerified,
            workspaceVerified,
            null,
            phases,
            ProvisionOperationId: Guid.ParseExact(descriptor.OperationId, "N"),
            ProvisionedResource: BuildProvisionedResource(request, descriptor, phases),
            StartupFailure: startupFailure).ValidateFor(request);
    }

    private static SandboxStartupBudgetFailure? EvaluateStartupBudget(
        DateTimeOffset leaseCreatedAt,
        ImmutableArray<SandboxStartupPhaseObservation> phases,
        AgentSandboxStartupBudgets budgets,
        DateTimeOffset now)
    {
        try
        {
            return budgets.ToContract().Evaluate(leaseCreatedAt, phases, now);
        }
        catch (ArgumentException exception)
        {
            throw new SandboxProviderException(
                "sandbox_startup_evidence_invalid", exception.Message, effectMayHaveApplied: false);
        }
    }

    private SandboxProvisionedResource BuildProvisionedResource(
        SandboxDescribeRequest request,
        AgentSandboxRecoveryDescriptor descriptor,
        ImmutableArray<SandboxStartupPhaseObservation> phases) =>
        new SandboxProvisionedResource(
            request.Resource,
            new SandboxEndpointReference(descriptor.EndpointId),
            CreatePlacementReference(descriptor.ClusterIdentity, descriptor.Namespace),
            AgentSandboxProviderMetadata.CreateRegistration(
                    JsonSerializer.Deserialize<AgentSandboxOptions>(descriptor.OptionsSnapshot, JsonOptions)
                    ?? throw ProviderBindingMismatch())
                .Descriptor.AdvertisedCapabilities,
            phases,
            request.ProviderBinding).Validate();

    private (AgentSandboxOptions Options, AgentSandboxRecoveryDescriptor Descriptor) ReadBinding(
        EnvironmentGenerationFence fence,
        ProviderResourceRef resource,
        long fencingGeneration,
        SandboxProviderBindingSnapshot binding)
    {
        _ = binding.ValidateFor(resource);
        if (!string.Equals(binding.ProviderId, AgentSandboxProviderMetadata.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(
                binding.AdapterVersion,
                AgentSandboxProviderMetadata.AdapterVersion.ToString(),
                StringComparison.Ordinal) ||
            binding.OptionsSchemaVersion != AgentSandboxOptions.CurrentOptionsSchemaVersion)
            throw ProviderBindingMismatch();

        AgentSandboxOptions options;
        AgentSandboxRecoveryDescriptor descriptor;
        try
        {
            options = (JsonSerializer.Deserialize<AgentSandboxOptions>(
                    binding.OptionsSnapshot,
                    JsonOptions)
                ?? throw new JsonException("Provider options are empty.")).Validate();
            descriptor = DeserializeRecoveryDescriptor(binding.ReleaseDescriptor.GetRawText());
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new SandboxProviderException(
                "provider_binding_invalid",
                "The pinned Agent Sandbox provider binding is invalid.",
                effectMayHaveApplied: false,
                exception);
        }

        var ownerFingerprint = GetOwnerFingerprint(fence.Owner);
        ValidateRecoveryDescriptor(descriptor, fence, ownerFingerprint, fencingGeneration);
        if (!string.Equals(options.OptionsRevision, binding.OptionsRevision, StringComparison.Ordinal) ||
            options.OptionsSchemaVersion != binding.OptionsSchemaVersion ||
            !string.Equals(options.Namespace, descriptor.Namespace, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ClusterIdentity, _client.ClusterIdentity, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ClaimUid, resource.ResourceId, StringComparison.Ordinal) ||
            descriptor.ResourceGeneration != resource.Generation ||
            !string.Equals(descriptor.StorageProviderId, options.WorkspaceStorageProviderId, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
        return (options, descriptor);
    }

    private static SandboxProviderBindingSnapshot BuildBinding(
        AgentSandboxRecoveryDescriptor recovery,
        ProviderResourceRef resource)
    {
        if (string.IsNullOrWhiteSpace(recovery.ClaimUid))
            throw ProviderBindingMismatch();
        return new SandboxProviderBindingSnapshot(
            AgentSandboxProviderMetadata.ProviderId,
            AgentSandboxProviderMetadata.AdapterVersion.ToString(),
            recovery.OptionsSchemaVersion,
            recovery.OptionsRevision,
            recovery.OptionsSnapshot.Clone(),
            JsonSerializer.SerializeToElement(recovery, JsonOptions))
            .ValidateFor(resource);
    }

    private static void ValidateRecoveryDescriptor(
        AgentSandboxRecoveryDescriptor descriptor,
        EnvironmentGenerationFence fence,
        string ownerFingerprint,
        long fencingGeneration)
    {
        if (descriptor.ContractVersion != 1 ||
            !string.Equals(descriptor.ProviderId, AgentSandboxProviderMetadata.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.AdapterVersion,
                AgentSandboxProviderMetadata.AdapterVersion.ToString(),
                StringComparison.Ordinal) ||
            descriptor.OptionsSchemaVersion != AgentSandboxOptions.CurrentOptionsSchemaVersion ||
            string.IsNullOrWhiteSpace(descriptor.OptionsRevision) ||
            descriptor.OptionsSnapshot.ValueKind != JsonValueKind.Object ||
            descriptor.EgressSelectorLabels is null ||
            descriptor.EgressSelectorLabels.IsEmpty ||
            !string.Equals(descriptor.OwnerFingerprint, ownerFingerprint, StringComparison.Ordinal) ||
            descriptor.LifecycleGeneration != fence.LifecycleGeneration ||
            descriptor.ResourceGeneration < 1 ||
            descriptor.FencingGeneration != fencingGeneration ||
            !Guid.TryParseExact(descriptor.OperationId, "N", out var operationId) ||
            operationId == Guid.Empty ||
            descriptor.EndpointId == Guid.Empty ||
            !IsDnsSubdomain(descriptor.Namespace) ||
            !IsDnsSubdomain(descriptor.ClaimName) ||
            !IsDnsSubdomain(descriptor.TemplateName) ||
            !IsDnsSubdomain(descriptor.WarmPoolName) ||
            string.IsNullOrWhiteSpace(descriptor.TemplateUid) ||
            string.IsNullOrWhiteSpace(descriptor.WarmPoolUid) ||
            !IsDnsSubdomain(descriptor.WorkspaceNamespace) ||
            !IsDnsSubdomain(descriptor.WorkspaceClaimName) ||
            string.IsNullOrWhiteSpace(descriptor.WorkspaceClaimUid) ||
            string.IsNullOrWhiteSpace(descriptor.StorageProviderId) ||
            descriptor.WorkspaceResourceGeneration < 1 ||
            !IsWorkspaceMountPath(descriptor.WorkspaceMountPath) ||
            !IsSha256(descriptor.WorkspaceIdentityFingerprint) ||
            !IsSha256(descriptor.ClusterIdentity) ||
            !string.Equals(GetOwnerFingerprint(fence.Owner), ownerFingerprint, StringComparison.Ordinal) ||
            descriptor.Namespace != descriptor.WorkspaceNamespace)
            throw ProviderBindingMismatch();

        var options = JsonSerializer.Deserialize<AgentSandboxOptions>(
            descriptor.OptionsSnapshot,
            JsonOptions);
        if (options is null)
            throw ProviderBindingMismatch();
        _ = options.Validate();
        if (
            !string.Equals(options.OptionsRevision, descriptor.OptionsRevision, StringComparison.Ordinal) ||
            !string.Equals(options.Namespace, descriptor.Namespace, StringComparison.Ordinal) ||
            !string.Equals(options.WorkspaceStorageProviderId, descriptor.StorageProviderId, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
        ValidateEgressSelectorLabels(descriptor.EgressSelectorLabels);

        var names = GetResourceNames(
            ownerFingerprint,
            fence,
            descriptor.LifecycleGeneration,
            descriptor.ResourceGeneration,
            descriptor.FencingGeneration,
            operationId,
            descriptor.OptionsRevision);
        if (!string.Equals(names.ClaimName, descriptor.ClaimName, StringComparison.Ordinal) ||
            !string.Equals(names.TemplateName, descriptor.TemplateName, StringComparison.Ordinal) ||
            !string.Equals(names.WarmPoolName, descriptor.WarmPoolName, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
    }

    private static AgentSandboxRecoveryDescriptor DeserializeRecoveryDescriptor(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentSandboxRecoveryDescriptor>(value, JsonOptions)
                ?? throw new JsonException("The recovery descriptor is empty.");
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException)
        {
            throw new SandboxProviderException(
                "recovery_binding_invalid",
                "The persisted Agent Sandbox recovery descriptor is invalid.",
                effectMayHaveApplied: false,
                exception);
        }
    }

    private static SandboxProviderException ProviderBindingMismatch() =>
        new(
            "provider_binding_mismatch",
            "The pinned Agent Sandbox binding does not match the exact owner and resource generation.",
            effectMayHaveApplied: false);

    private static SandboxProviderException ProviderResourceMismatch(string message) =>
        new("provider_resource_mismatch", message, effectMayHaveApplied: false);

    private static (string ClaimName, string TemplateName, string WarmPoolName) GetResourceNames(
        string ownerFingerprint,
        EnvironmentGenerationFence fence,
        long lifecycleGeneration,
        long resourceGeneration,
        long fencingGeneration,
        Guid operationId,
        string optionsRevision)
    {
        if (fence.LifecycleGeneration != lifecycleGeneration ||
            !string.Equals(GetOwnerFingerprint(fence.Owner), ownerFingerprint, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            AgentSandboxProviderMetadata.ProviderId,
            optionsRevision,
            fence,
            resourceGeneration,
            fencingGeneration,
            operationId);
        var suffix = planned.ResourceId["aw-claim-".Length..];
        return (
            planned.ResourceId,
            $"aw-template-{suffix}",
            $"aw-pool-{suffix}");
    }

    private static string GetOwnerFingerprint(EnvironmentOwnerIdentity owner) =>
        Hash($"{owner.TenantId}\0{owner.ProjectId}\0{owner.RunId}\0{owner.EnvironmentId}")[..40];

    private static string GetWorkspaceFingerprint(
        string projectId,
        string volumeId,
        string resourceGeneration,
        string environmentId) =>
        Hash($"{projectId}\0{volumeId}\0{resourceGeneration}\0{environmentId}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SandboxEndpointReference CreateEndpointReference(
        string ownerFingerprint,
        long resourceGeneration,
        long fencingGeneration,
        Guid operationId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ownerFingerprint}\0{resourceGeneration}\0{fencingGeneration}\0{operationId:N}\0endpoint"));
        return new SandboxEndpointReference(new Guid(digest.AsSpan(0, 16))).Validate();
    }

    private static SandboxPlacementReference CreatePlacementReference(
        string clusterIdentity,
        string kubernetesNamespace) =>
        new SandboxPlacementReference(
            $"k8s-{Hash($"{clusterIdentity}\0{kubernetesNamespace}")[..32]}").Validate();

    private async Task<ImmutableArray<SandboxStartupPhaseObservation>> ReadStartupPhasesAsync(
        JsonElement pod,
        string kubernetesNamespace,
        AgentSandboxOptions options,
        CancellationToken cancellationToken)
    {
        var phases = ImmutableArray.CreateBuilder<SandboxStartupPhaseObservation>();
        if (HasTrueCondition(pod, "PodScheduled", out var scheduledAt))
            phases.Add(new SandboxStartupPhaseObservation(
                SandboxStartupPhase.Scheduled,
                1,
                scheduledAt ?? throw new SandboxProviderException(
                    "sandbox_startup_evidence_missing",
                    "The scheduled placement condition has no transition timestamp.",
                    effectMayHaveApplied: false)));

        var containerStatus = FindContainerStatus(pod, WorkspaceContainerName);
        var startedAt = DateTimeOffset.MinValue;
        var selectedImageRunning = containerStatus is { } selectedContainer &&
            IsSelectedImageRunning(pod, selectedContainer, options, out startedAt);
        if (selectedImageRunning)
            phases.Add(new SandboxStartupPhaseObservation(
                SandboxStartupPhase.Started,
                1,
                startedAt));

        if (ReadOptionalString(pod, "metadata", "uid") is { } podUid &&
            selectedImageRunning)
        {
            var events = await _client.ListAsync(
                string.Empty,
                "events",
                kubernetesNamespace,
                ImmutableDictionary<string, string>.Empty,
                cancellationToken,
                $"involvedObject.uid={podUid}").ConfigureAwait(false);
            if (TryReadMatchingImagePullCompletedAt(
                    events, podUid, options, out var imageReadyAt))
                phases.Add(new SandboxStartupPhaseObservation(
                    SandboxStartupPhase.ImageReady,
                    1,
                    imageReadyAt,
                    options.ContainerImageDigest,
                    options.ContainerImageCompressedPullBytes));
        }

        return phases
            .GroupBy(phase => phase.Phase)
            .Select(group => group.OrderBy(phase => phase.ObservedAt).First())
            .OrderBy(phase => phase.Phase)
            .ToImmutableArray();
    }

    private static bool IsSelectedImageRunning(
        JsonElement pod,
        JsonElement container,
        AgentSandboxOptions options,
        out DateTimeOffset startedAt)
    {
        startedAt = default;
        if (ReadOptionalString(container, "imageID") is not { } imageId ||
            !imageId.EndsWith(options.ContainerImageDigest, StringComparison.Ordinal) ||
            TryGetArray(TryGetObject(pod, "spec") ?? default, "containers") is not { } containers)
            return false;

        foreach (var spec in containers.EnumerateArray())
        {
            if (ReadOptionalString(spec, "name") == WorkspaceContainerName &&
                ReadOptionalString(spec, "image") == options.ContainerImage &&
                TryGetDateTime(container, "state", "running", "startedAt", out startedAt))
                return true;
        }
        startedAt = default;
        return false;
    }

    private static bool TryReadMatchingImagePullCompletedAt(
        IReadOnlyList<JsonElement> events,
        string podUid,
        AgentSandboxOptions options,
        out DateTimeOffset completedAt)
    {
        completedAt = default;
        foreach (var item in events)
        {
            if (ReadOptionalString(item, "reason") != "Pulled" ||
                ReadOptionalString(item, "involvedObject", "uid") != podUid ||
                (ReadOptionalString(item, "message") ?? ReadOptionalString(item, "note")) is not { } message ||
                !message.Contains(options.ContainerImageDigest, StringComparison.Ordinal))
                continue;
            if (TryGetDateTime(item, "eventTime", out completedAt) ||
                TryGetDateTime(item, "lastTimestamp", out completedAt) ||
                TryGetDateTime(item, ["metadata", "creationTimestamp"], out completedAt))
                return true;
        }
        return false;
    }

    private static bool HasTrueCondition(
        JsonElement resource,
        string type,
        out DateTimeOffset? transitionTime)
    {
        transitionTime = null;
        var conditions = TryGetObject(resource, "status") is { } status
            ? TryGetArray(status, "conditions")
            : null;
        if (conditions is not { } values)
            return false;
        foreach (var condition in values.EnumerateArray())
        {
            if (!string.Equals(ReadOptionalString(condition, "type"), type, StringComparison.Ordinal) ||
                !string.Equals(ReadOptionalString(condition, "status"), "True", StringComparison.Ordinal))
                continue;
            if (TryGetDateTime(condition, "lastTransitionTime", out var observed))
                transitionTime = observed;
            return true;
        }
        return false;
    }

    private static JsonElement? FindContainerStatus(JsonElement pod, string containerName)
    {
        var status = TryGetObject(pod, "status");
        var statuses = status is { } value ? TryGetArray(value, "containerStatuses") : null;
        if (statuses is not { } array)
            return null;
        foreach (var item in array.EnumerateArray())
            if (string.Equals(ReadOptionalString(item, "name"), containerName, StringComparison.Ordinal))
                return item;
        return null;
    }

    private static bool IsPodFailed(JsonElement pod) =>
        string.Equals(ReadOptionalString(pod, "status", "phase"), "Failed", StringComparison.Ordinal);

    private static bool IsWorkspaceMountPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Any(char.IsControl) || value.Contains('\\') ||
            !value.StartsWith("/workspace/agentweaver/", StringComparison.Ordinal))
            return false;
        var segments = value.Split('/');
        return segments.Skip(1).All(segment => segment is not ("" or "." or ".."));
    }

    private static bool IsDnsSubdomain(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 253 &&
        value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 &&
            label[0] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            label[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' &&
            label.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateEgressSelectorLabels(
        ImmutableDictionary<string, string> labels)
    {
        foreach (var (key, value) in labels)
        {
            if (!IsKubernetesLabelKey(key) ||
                value.Length > 63 ||
                value.Any(character =>
                    character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                        >= '0' and <= '9' or '.' or '_' or '-')) ||
                value.Length > 0 &&
                    (value[0] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') ||
                     value[^1] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')) ||
                key.StartsWith("kubernetes.io/", StringComparison.Ordinal) ||
                key.StartsWith("k8s.io/", StringComparison.Ordinal) ||
                key.StartsWith("agents.x-k8s.io/", StringComparison.Ordinal) ||
                key is ManagedLabel or OwnerLabel or LifecycleGenerationLabel or ResourceGenerationLabel or
                    FencingGenerationLabel or OperationLabel)
                throw new SandboxProviderException(
                    "egress_selector_invalid",
                    "A Kubernetes egress selector label is invalid or uses a reserved ownership key.",
                    effectMayHaveApplied: false);
        }
    }

    private static bool IsKubernetesLabelKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 317)
            return false;
        var parts = value.Split('/');
        if (parts.Length is < 1 or > 2 ||
            !IsLabelName(parts[^1]))
            return false;
        if (parts.Length == 2)
            return IsDnsSubdomain(parts[0]);
        return true;
    }

    private static bool IsLabelName(string value) =>
        value.Length is >= 1 and <= 63 &&
        value[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' &&
        value[^1] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static bool HasOwnerReference(JsonElement metadata, string uid)
    {
        var references = TryGetArray(metadata, "ownerReferences");
        if (references is not { } array)
            return false;
        return array.EnumerateArray().Any(reference =>
            string.Equals(ReadOptionalString(reference, "uid"), uid, StringComparison.Ordinal) &&
            TryGetBoolean(reference, "controller", out var controller) &&
            controller);
    }

    private static ImmutableDictionary<string, string> ReadLabels(JsonElement resource) =>
        ReadStringDictionary(RequiredObject(resource, "metadata"), "labels");

    private static bool TryGetAnnotation(JsonElement resource, string name, out string value)
    {
        var metadata = TryGetObject(resource, "metadata");
        var annotations = metadata is { } objectValue
            ? TryGetObject(objectValue, "annotations")
            : null;
        if (annotations is { } dictionary &&
            dictionary.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool HasAnnotation(
        ImmutableDictionary<string, string> annotations,
        string key,
        string expected) =>
        annotations.TryGetValue(key, out var value) &&
        string.Equals(value, expected, StringComparison.Ordinal);

    private static bool HasAnnotationOrLabel(
        ImmutableDictionary<string, string> labels,
        string key,
        string expected) =>
        labels.TryGetValue(key, out var value) &&
        string.Equals(value, expected, StringComparison.Ordinal);

    private static ImmutableDictionary<string, string> ReadStringDictionary(
        JsonElement parent,
        string name)
    {
        var value = TryGetObject(parent, name);
        if (value is null)
            return ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in value.Value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new SandboxProviderException(
                    "kubernetes_response_invalid",
                    "Kubernetes returned a non-string metadata value.",
                    effectMayHaveApplied: false);
            builder.Add(property.Name, property.Value.GetString()!);
        }
        return builder.ToImmutable();
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) =>
        TryGetObject(parent, name) ??
        throw new SandboxProviderException(
            "kubernetes_response_invalid",
            $"Kubernetes response is missing object '{name}'.",
            effectMayHaveApplied: false);

    private static JsonElement RequiredObject(JsonElement parent, string first, string second) =>
        RequiredObject(RequiredObject(parent, first), second);

    private static string RequiredString(JsonElement parent, string name) =>
        ReadOptionalString(parent, name) ??
        throw new SandboxProviderException(
            "kubernetes_response_invalid",
            $"Kubernetes response is missing string '{name}'.",
            effectMayHaveApplied: false);

    private static string? ReadOptionalString(JsonElement parent, params string[] path)
    {
        var current = parent;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String
            ? current.GetString()
            : null;
    }

    private static bool TryGetBoolean(JsonElement parent, string name, out bool value) =>
        TryGetBoolean(parent, [name], out value);

    private static bool TryGetBoolean(
        JsonElement parent,
        string first,
        string second,
        out bool value) =>
        TryGetBoolean(parent, [first, second], out value);

    private static bool TryGetBoolean(JsonElement parent, string[] path, out bool value)
    {
        value = false;
        if (!TryGetPropertyPath(parent, path, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt64(out value);
    }

    private static JsonElement RequiredArray(JsonElement parent, string name) =>
        TryGetArray(parent, name) ??
        throw new SandboxProviderException(
            "kubernetes_response_invalid",
            $"Kubernetes response is missing array '{name}'.",
            effectMayHaveApplied: false);

    private static JsonElement? TryGetArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : null;

    private static JsonElement? TryGetObject(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static bool TryGetDateTime(
        JsonElement parent,
        string name,
        out DateTimeOffset result) =>
        TryGetDateTime(parent, [name], out result);

    private static bool TryGetDateTime(
        JsonElement parent,
        string first,
        string second,
        string third,
        out DateTimeOffset result) =>
        TryGetDateTime(parent, [first, second, third], out result);

    private static bool TryGetDateTime(
        JsonElement parent,
        string[] path,
        out DateTimeOffset result)
    {
        result = default;
        return TryGetPropertyPath(parent, path, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   element.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out result);
    }

    private static bool TryGetPropertyPath(
        JsonElement parent,
        string[] path,
        out JsonElement value)
    {
        value = parent;
        foreach (var segment in path)
        {
            if (!value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    private sealed record AgentSandboxRecoveryDescriptor(
        int ContractVersion,
        string ProviderId,
        string AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision,
        JsonElement OptionsSnapshot,
        string Namespace,
        string OwnerFingerprint,
        ImmutableDictionary<string, string> EgressSelectorLabels,
        long LifecycleGeneration,
        long ResourceGeneration,
        long FencingGeneration,
        string OperationId,
        string ClaimName,
        string TemplateName,
        string TemplateUid,
        string WarmPoolName,
        string WarmPoolUid,
        string WorkspaceNamespace,
        string WorkspaceClaimName,
        string WorkspaceClaimUid,
        string StorageProviderId,
        long WorkspaceResourceGeneration,
        string WorkspaceMountPath,
        bool WorkspaceReadOnly,
        string WorkspaceIdentityFingerprint,
        Guid EndpointId,
        string ClusterIdentity)
    {
        public string? ClaimUid { get; init; }
    }

    private sealed record AgentSandboxRecoveryIntent(
        int ContractVersion,
        SandboxWorkspaceAttachment Workspace,
        ImmutableDictionary<string, string> EgressSelectorLabels);
}
