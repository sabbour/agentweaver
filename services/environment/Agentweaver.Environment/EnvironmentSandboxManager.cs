using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;

namespace Agentweaver.Environment;

public sealed record SandboxProvisionApiRequest(
    string VolumeId,
    long VolumeResourceGeneration,
    long DataGeneration,
    string MountPath,
    bool ReadOnly,
    long NetworkPolicyGeneration,
    string IdempotencyKey)
{
    public SandboxProvisionApiRequest Validate()
    {
        _ = new WorkspaceVolumeReference("project", VolumeId, VolumeResourceGeneration).Validate();
        if (DataGeneration < 0 || NetworkPolicyGeneration < 1 ||
            string.IsNullOrWhiteSpace(IdempotencyKey) ||
            IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("The Sandbox provision request is invalid.");
        return this;
    }
}

public sealed record SandboxAbandonApiRequest(
    long ResourceGeneration,
    long ProviderFencingGeneration,
    string IdempotencyKey)
{
    public SandboxAbandonApiRequest Validate()
    {
        if (ResourceGeneration < 1 || ProviderFencingGeneration < 1 ||
            string.IsNullOrWhiteSpace(IdempotencyKey) ||
            IdempotencyKey.Length > 128 ||
            IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("The Sandbox abandonment request is invalid.");
        return this;
    }
}

public sealed record SandboxObservationSummary(
    ProviderResourceRef Resource,
    SandboxObservedState State,
    long FencingGeneration,
    bool VmIsolationVerified,
    bool WorkspaceAttachmentVerified,
    long? VerifiedNetworkGeneration,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxTerminalEvidence? TerminalEvidence);

public sealed record EnvironmentSandboxResult(
    Guid OperationId,
    SandboxLeaseState State,
    long ResourceGeneration,
    long ProviderFencingGeneration,
    long CurrentFencingGeneration,
    ProviderResourceRef? Resource,
    SandboxEndpointReference? Endpoint,
    SandboxPlacementReference? Placement,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    SandboxRetirementReason? RetirementReason,
    SandboxObservationSummary? Observation,
    bool ReadyForDispatch);

public sealed class EnvironmentSandboxManager(
    IProjectsConfigClient projects,
    IEnvironmentLifecycleStore lifecycleStore,
    ISandboxLeaseStore leaseStore,
    ISandboxProvider sandboxProvider,
    AgentSandboxOptions sandboxOptions,
    KubernetesAgentSandboxClient kubernetesClient,
    CiliumEgressProviderOptions ciliumOptions,
    EnvironmentEgressManager egressManager)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<EnvironmentSandboxResult> ProvisionAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxProvisionApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Validate();
        sandboxOptions.Validate();
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var candidate = ResolveSandboxCandidate(context.Selection.RunSelection);
        var workspace = await ReadWorkspaceAsync(
            context.Fence, context.Selection.RunSelection, request, cancellationToken).ConfigureAwait(false);
        _ = await egressManager.VerifyNetworkForSandboxAsync(
            caller,
            context.Fence,
            context.Selection,
            request.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);

        var optionsSnapshot = JsonSerializer.SerializeToElement(sandboxOptions, JsonOptions);
        var intent = new SandboxLeaseProvisionIntent(
            candidate.ProviderId,
            candidate.AdapterVersion.ToString(),
            candidate.OptionsSchemaVersion,
            candidate.OptionsRevision,
            optionsSnapshot,
            JsonSerializer.SerializeToElement(context.Selection.RunSelection, JsonOptions),
            JsonSerializer.SerializeToElement(request, JsonOptions));
        var reservation = await leaseStore.ReserveProvisionAsync(
            context.Fence,
            request.IdempotencyKey,
            intent,
            cancellationToken).ConfigureAwait(false);
        var lease = reservation.Lease;
        if (lease.State != SandboxLeaseState.Provisioning)
        {
            if (lease.State == SandboxLeaseState.Active && lease.IsCurrent)
                return await ObserveAsync(
                    caller,
                    context,
                    lease,
                    request.NetworkPolicyGeneration,
                    cancellationToken).ConfigureAwait(false);
            return ToResult(lease);
        }

        var providerCallStarted = false;
        SandboxProvisionedResource? provisioned = null;
        try
        {
            var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
                candidate.ProviderId,
                candidate.OptionsRevision,
                context.Fence,
                lease.ResourceGeneration,
                lease.ProviderFencingGeneration,
                lease.OperationId);
            var workspaceAttachment = NegotiateWorkspace(
                context.Fence,
                request,
                workspace,
                plannedResource);
            var selector = CreateEgressSelector(context.Fence, context.Selection);
            var provisionRequest = new SandboxProvisionRequest(
                context.Fence,
                candidate,
                lease.ResourceGeneration,
                lease.ProviderFencingGeneration,
                lease.OperationId,
                selector.MatchLabels,
                workspaceAttachment).Validate();
            var recoveryIntent = JsonSerializer.SerializeToElement(
                new SandboxProviderRecoveryIntent(1, request, provisionRequest.Workspace, selector.MatchLabels),
                JsonOptions);
            lease = await leaseStore.SaveProviderRequestAsync(
                lease.OperationId,
                context.Fence,
                recoveryIntent,
                cancellationToken).ConfigureAwait(false);
            if (lease.State != SandboxLeaseState.Provisioning)
                return ToResult(lease);

            await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
            await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
            _ = await egressManager.VerifyNetworkForSandboxAsync(
                caller,
                context.Fence,
                context.Selection,
                request.NetworkPolicyGeneration,
                cancellationToken).ConfigureAwait(false);
            var latestWorkspace = await ReadWorkspaceAsync(
                context.Fence, context.Selection.RunSelection, request, cancellationToken).ConfigureAwait(false);
            if (!SameWorkspace(workspace, latestWorkspace))
                throw new EnvironmentLifecycleException(
                    "workspace_generation_changed",
                    "The exact Workspace resource or data generation changed before Sandbox provisioning.");

            await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
            await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
            providerCallStarted = true;
            provisioned = await sandboxProvider.ProvisionAsync(
                provisionRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException exception)
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: exception.EffectMayHaveApplied || providerCallStarted,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: providerCallStarted,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            providerCallStarted && exception is (ArgumentException or InvalidOperationException or JsonException))
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            providerCallStarted && exception is (IOException or HttpRequestException or TimeoutException))
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: true,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            !providerCallStarted && exception is (ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                ProjectsConfigApiException or
                EnvironmentLifecycleException or
                CiliumPolicyException or
                HttpRequestException))
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: false,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        lease = await leaseStore.CompleteProvisionAsync(
            lease.OperationId,
            context.Fence,
            provisioned ?? throw new InvalidOperationException("The Sandbox provider returned no resource."),
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        if (lease.State != SandboxLeaseState.Active)
            return ToResult(lease);
        return await ObserveAsync(
            caller,
            context,
            lease,
            request.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentSandboxResult?> InspectAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        long networkPolicyGeneration,
        CancellationToken cancellationToken)
    {
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        if (networkPolicyGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(networkPolicyGeneration));
        var lease = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return null;
        if (lease.ProvisionedResource is null ||
            lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.ReconciliationRequired)
            return ToResult(lease);
        return await ObserveAsync(
            caller,
            context,
            lease,
            networkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentSandboxResult> AbandonAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxAbandonApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Validate();
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetAsync(
                context.Fence, request.ResourceGeneration, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException("sandbox_lease_unknown", "No current Sandbox lease exists.");
        if (lease.ResourceGeneration != request.ResourceGeneration ||
            lease.ProviderFencingGeneration != request.ProviderFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The requested Sandbox resource generation or provider fence is stale.");
        var authorization = new SandboxRetirementAuthorization(
            context.Selection.Authorization.Issuer,
            context.Selection.Authorization.ActorId,
            context.Selection.Authorization.MembershipRevision);
        lease = await leaseStore.BeginRetirementAsync(
            context.Fence,
            request.ResourceGeneration,
            request.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            request.IdempotencyKey,
            authorization,
            terminalEvidence: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EnvironmentSandboxResult?> ReconcileAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        long? networkPolicyGeneration,
        CancellationToken cancellationToken)
    {
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return null;

        if (lease.State == SandboxLeaseState.Active && lease.ProvisionedResource is not null)
        {
            var describeRequest = CreateDescribeRequest(lease);
            var observation = (await sandboxProvider.DescribeAsync(describeRequest, cancellationToken)
                .ConfigureAwait(false)).ValidateFor(describeRequest);
            await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
            if (observation.State == SandboxObservedState.Finished && observation.TerminalEvidence is { } evidence)
            {
                lease = await leaseStore.BeginRetirementAsync(
                    context.Fence,
                    lease.ResourceGeneration,
                    lease.ProviderFencingGeneration,
                    SandboxRetirementReason.Finished,
                    "finished-" + lease.OperationId.ToString("N"),
                    authorization: null,
                    evidence,
                    cancellationToken).ConfigureAwait(false);
                return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (networkPolicyGeneration is > 0)
                return await ObserveAsync(
                    caller,
                    context,
                    lease,
                    networkPolicyGeneration.Value,
                    cancellationToken).ConfigureAwait(false);
            return ToResult(lease, observation);
        }

        if (lease.ProvisionedResource is null &&
            lease.ProviderRequestFingerprint is null &&
            lease.State is (SandboxLeaseState.Provisioning or SandboxLeaseState.Releasing))
        {
            lease = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: false,
                cancellationToken).ConfigureAwait(false);
            await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
            return ToResult(lease);
        }

        if (lease.ProvisionedResource is null &&
            lease.ProviderRequestFingerprint is not null &&
            lease.State is (SandboxLeaseState.Provisioning or
                SandboxLeaseState.Releasing or
                SandboxLeaseState.ReconciliationRequired))
        {
            var owned = await sandboxProvider.ListOwnedAsync(
                new SandboxListOwnedRequest(context.Fence, lease.ProviderFencingGeneration),
                cancellationToken).ConfigureAwait(false);
            var recovered = owned.Where(observation =>
                    observation.ProvisionOperationId == lease.OperationId &&
                    observation.FencingGeneration == lease.ProviderFencingGeneration &&
                    observation.Resource.Generation == lease.ResourceGeneration &&
                    observation.ProvisionedResource is not null)
                .ToArray();
            if (recovered.Length == 1)
            {
                var recoveredResource = recovered[0].ProvisionedResource!;
                _ = recovered[0].ValidateFor(new SandboxDescribeRequest(
                    context.Fence,
                    recovered[0].Resource,
                    lease.ProviderFencingGeneration,
                    recoveredResource.ProviderBinding));
                lease = await leaseStore.CompleteProvisionAsync(
                    lease.OperationId,
                    context.Fence,
                    recoveredResource,
                    effectMayHaveApplied: true,
                    cancellationToken).ConfigureAwait(false);
                await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
                if (lease.State == SandboxLeaseState.Releasing)
                    return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken)
                        .ConfigureAwait(false);
                if (lease.State == SandboxLeaseState.Active && networkPolicyGeneration is > 0)
                    return await ObserveAsync(
                        caller,
                        context,
                        lease,
                        networkPolicyGeneration.Value,
                        cancellationToken).ConfigureAwait(false);
                return ToResult(lease);
            }
            if (recovered.Length > 1)
                throw new EnvironmentLifecycleException(
                    "sandbox_recovery_ambiguous",
                    "More than one exact Sandbox claim matches the durable owner operation.");
        }

        if (lease.State == SandboxLeaseState.Releasing)
            return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken).ConfigureAwait(false);
        return ToResult(lease);
    }

    private async Task<EnvironmentSandboxResult> ObserveAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        SandboxLeaseSnapshot lease,
        long networkPolicyGeneration,
        CancellationToken cancellationToken)
    {
        _ = await egressManager.VerifyNetworkForSandboxAsync(
            caller, context.Fence, context.Selection, networkPolicyGeneration, cancellationToken)
            .ConfigureAwait(false);
        var describeRequest = CreateDescribeRequest(lease);
        var observation = (await sandboxProvider.DescribeAsync(
            describeRequest, cancellationToken).ConfigureAwait(false)).ValidateFor(describeRequest);
        var currentContext = await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var currentLease = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (currentLease is null ||
            currentLease.OperationId != lease.OperationId ||
            currentLease.CurrentFencingGeneration != lease.CurrentFencingGeneration ||
            currentLease.State != lease.State)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease changed during provider observation.");
        _ = await egressManager.VerifyNetworkForSandboxAsync(
            caller, context.Fence, currentContext.Selection, networkPolicyGeneration, cancellationToken)
            .ConfigureAwait(false);
        if (observation.State == SandboxObservedState.Pending &&
            observation.VmIsolationVerified &&
            observation.WorkspaceAttachmentVerified &&
            observation.StartupPhases.Any(phase => phase.Phase == SandboxStartupPhase.Ready))
            observation = observation with
            {
                State = SandboxObservedState.Ready,
                VerifiedNetworkGeneration = networkPolicyGeneration
            };
        else if (observation.State is not (SandboxObservedState.Finished or SandboxObservedState.Absent))
            observation = observation with { VerifiedNetworkGeneration = networkPolicyGeneration };
        observation = observation.ValidateFor(CreateDescribeRequest(lease));
        return ToResult(currentLease, observation);
    }

    private async Task<EnvironmentSandboxResult> ReleaseRetiringLeaseAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        if (lease.State != SandboxLeaseState.Releasing || lease.ProvisionedResource is null)
            return ToResult(lease);
        var expectedResource = lease.ProvisionedResource.Resource;
        var currentLease = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (currentLease is null ||
            currentLease.OperationId != lease.OperationId ||
            currentLease.State != SandboxLeaseState.Releasing ||
            currentLease.ResourceGeneration != lease.ResourceGeneration ||
            currentLease.ProviderFencingGeneration != lease.ProviderFencingGeneration ||
            currentLease.CurrentFencingGeneration != lease.CurrentFencingGeneration ||
            currentLease.ProvisionedResource?.Resource != expectedResource)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease is no longer the exact current retiring resource.");
        lease = currentLease;
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var provisionedResource = lease.ProvisionedResource
            ?? throw new EnvironmentLifecycleException(
                "sandbox_resource_unknown",
                "The retiring Sandbox lease no longer has its exact provider resource.");
        var releaseRequest = new SandboxReleaseRequest(
            context.Fence,
            provisionedResource.Resource,
            lease.ProviderFencingGeneration,
            provisionedResource.ProviderBinding,
            lease.ReleaseIdempotencyKey
                ?? throw new EnvironmentLifecycleException(
                    "sandbox_release_intent_missing",
                    "The retiring Sandbox lease is missing its durable release idempotency key.")).Validate();
        var receipt = await sandboxProvider.ReleaseAsync(releaseRequest, cancellationToken).ConfigureAwait(false);
        var current = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.OperationId != lease.OperationId ||
            current.State != SandboxLeaseState.Releasing ||
            current.CurrentFencingGeneration != lease.CurrentFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease changed during provider release; the receipt was not accepted.");
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var released = await leaseStore.CompleteReleaseAsync(
            lease.OperationId,
            context.Fence,
            lease.ProviderFencingGeneration,
            receipt,
            cancellationToken).ConfigureAwait(false);
        return ToResult(released);
    }

    private async Task<AuthorizedSandboxContext> AuthorizeAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var initialAuthorization = await projects.GetAuthorizationContextAsync(
            caller, cancellationToken).ConfigureAwait(false);
        var owner = new EnvironmentOwnerIdentity(
            initialAuthorization.TenantId,
            projectId,
            runId,
            environmentId);
        var selection = await egressManager.GetAuthorizedRunSelectionAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        if (!EnvironmentEgressManager.SameAuthorizationContext(
                initialAuthorization,
                selection.Authorization))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "Projects authorization changed while the Sandbox owner was resolved.");
        var lifecycle = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        return new(lifecycle.Fence, selection);
    }

    private async Task<WorkspaceContext> ReadWorkspaceAsync(
        EnvironmentGenerationFence fence,
        EffectiveNetworkPolicySelection selection,
        SandboxProvisionApiRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = await lifecycleStore.GetWorkspaceVolumeAsync(
            fence, request.VolumeId, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "workspace_volume_unknown",
                "The exact owner-scoped Workspace volume does not exist.");
        if (snapshot.ResourceGeneration != request.VolumeResourceGeneration ||
            snapshot.DataGeneration != request.DataGeneration ||
            snapshot.Phase is not (EnvironmentWorkspaceVolumeState.Ready or
                EnvironmentWorkspaceVolumeState.Bound or EnvironmentWorkspaceVolumeState.Attached) ||
            snapshot.Resource is null ||
            snapshot.ProviderBinding is null)
            throw new EnvironmentLifecycleException(
                "workspace_generation_mismatch",
                "The requested Workspace resource or data generation is not the current ready owner generation.");

        var spec = JsonSerializer.Deserialize<WorkspaceVolumeSpec>(snapshot.Specification, JsonOptions)
            ?? throw new EnvironmentLifecycleException(
                "workspace_specification_invalid",
                "The stored Workspace volume specification is invalid.");
        if (spec.ProjectId != fence.Owner.ProjectId ||
            spec.VolumeId != request.VolumeId ||
            !spec.AllowsEnvironment(fence.Owner.ProjectId, fence.Owner.EnvironmentId) ||
            spec.Consistency != WorkspaceVolumeConsistency.Strict)
            throw new NotSupportedException(
                "The selected Workspace volume is not authorized for this Environment or is unsupported.");

        var storageSelection = GetExclusiveCandidate(selection, ProviderSeam.Storage);
        var binding = snapshot.ProviderBinding.ValidateFor(snapshot.Resource);
        if (storageSelection.ProviderId != snapshot.Resource.ProviderId ||
            storageSelection.ProviderId != AzureFilesCsiProviderMetadata.ProviderId ||
            storageSelection.AdapterVersion != binding.AdapterVersion ||
            storageSelection.OptionsSchemaVersion != binding.OptionsSchemaVersion ||
            storageSelection.OptionsRevision != binding.OptionsRevision)
            throw new NotSupportedException(
                "The current immutable run selection does not match the pinned Workspace Storage provider.");

        var storageOptions = JsonSerializer.Deserialize<AzureFilesCsiOptions>(
                binding.OptionsSnapshot,
                JsonOptions)
            ?? throw new EnvironmentLifecycleException(
                "workspace_provider_binding_invalid",
                "The pinned Workspace Storage options are invalid.");
        storageOptions.Validate();
        var registration = AzureFilesCsiProviderMetadata.CreateRegistration(storageOptions);
        var advertised = storageSelection.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        if (storageSelection.Hosting != registration.Descriptor.Hosting ||
            storageSelection.AdapterVersion != registration.Descriptor.AdapterVersion.ToString() ||
            storageSelection.OptionsSchemaVersion != registration.OptionsSchemaVersion ||
            !advertised.SetEquals(registration.Descriptor.AdvertisedCapabilities) ||
            !storageSelection.RequiredCapabilities.All(advertised.Contains))
            throw new NotSupportedException(
                "The current immutable run selection does not match the registered Workspace Storage provider.");

        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            WorkspaceVolumeCapabilities.ForAccessMode(spec.AccessMode),
            WorkspaceVolumeCapabilities.ReadOnlyMount);
        var storageResource = new WorkspaceVolumeResource(
            snapshot.Resource,
            capabilities,
            binding).Validate();
        var releaseDescriptor = binding.ReleaseDescriptor;
        var claim = new AgentSandboxPersistentVolumeClaimAttachment(
            1,
            binding.ProviderId,
            RequiredJsonString(releaseDescriptor, "namespace"),
            RequiredJsonString(releaseDescriptor, "claimName"),
            RequiredJsonString(releaseDescriptor, "claimUid"));
        if (!string.Equals(storageOptions.Namespace, sandboxOptions.Namespace, StringComparison.Ordinal) ||
            !string.Equals(claim.Namespace, storageOptions.Namespace, StringComparison.Ordinal) ||
            !string.Equals(
                RequiredJsonString(releaseDescriptor, "clusterIdentity"),
                kubernetesClient.ClusterIdentity,
                StringComparison.Ordinal))
            throw new NotSupportedException(
                "The Workspace PVC provider binding targets a different Kubernetes cluster or namespace.");
        return new(snapshot, spec, storageResource, claim);
    }

    private SandboxWorkspaceAttachment NegotiateWorkspace(
        EnvironmentGenerationFence fence,
        SandboxProvisionApiRequest request,
        WorkspaceContext workspace,
        ProviderResourceRef plannedResource)
    {
        var reference = new WorkspaceVolumeReference(
            fence.Owner.ProjectId,
            request.VolumeId,
            request.VolumeResourceGeneration);
        var mount = new WorkspaceVolumeMountDeclaration(reference, request.MountPath, request.ReadOnly);
        var manifest = new WorkspaceVolumeMountManifest([mount]);
        var sandboxProfile = new WorkspaceSandboxAttachmentProfile(
            plannedResource,
            Enum.GetValues<WorkspaceVolumeAccessMode>().ToImmutableHashSet(),
            ImmutableHashSet.Create(WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            ImmutableHashSet.Create(StringComparer.Ordinal, sandboxOptions.WorkspaceStorageProviderId),
            SupportsReadOnlyMounts: true);
        var negotiation = WorkspaceVolumeAttachmentNegotiator.Negotiate(
            fence.Owner.ProjectId,
            fence.Owner.EnvironmentId,
            fence.Owner.RunId,
            workspace.Specification,
            workspace.StorageResource,
            manifest,
            mount,
            plannedResource,
            fence,
            request.DataGeneration,
            sandboxProfile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        return new SandboxWorkspaceAttachment(
            negotiation,
            JsonSerializer.SerializeToElement(workspace.ClaimAttachment, JsonOptions)).Validate();
    }

    private ProviderCandidate ResolveSandboxCandidate(EffectiveNetworkPolicySelection selection)
    {
        if (!string.Equals(sandboxOptions.Namespace, ciliumOptions.Namespace, StringComparison.Ordinal))
            throw new NotSupportedException(
                "Sandbox and Network Policy must use the same Kubernetes namespace for endpoint selection.");
        var effective = GetExclusiveCandidate(selection, ProviderSeam.Sandbox);
        var registration = AgentSandboxProviderMetadata.CreateRegistration(sandboxOptions);
        if (effective.ProviderId != registration.Descriptor.Id ||
            effective.AdapterVersion != registration.Descriptor.AdapterVersion.ToString() ||
            effective.OptionsSchemaVersion != registration.OptionsSchemaVersion ||
            effective.OptionsRevision != registration.OptionsRevision ||
            effective.Hosting != registration.Descriptor.Hosting ||
            !effective.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(registration.Descriptor.AdvertisedCapabilities))
            throw new NotSupportedException(
                "The selected Sandbox adapter or options revision is not installed; no fallback is permitted.");

        var required = effective.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
            .Union(
            [
                SandboxCapabilities.VmIsolation,
                SandboxCapabilities.WorkspacePersistentVolumeClaim
            ]);
        var catalog = ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Sandbox, registration.Descriptor.Id)],
            []);
        if (!catalog.IsSuccess)
            throw new InvalidOperationException(catalog.Error?.Message ?? "Sandbox provider registration is invalid.");
        var result = new ProviderResolver(catalog.Value!).Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            null,
            registration.Descriptor.AdapterVersion,
            registration.OptionsSchemaVersion,
            required));
        if (!result.IsSuccess || result.Value?.Candidate is null)
            throw new NotSupportedException(
                result.Error?.Message ?? "The selected Sandbox capabilities are unavailable.");
        return result.Value.Candidate;
    }

    private async Task<AuthorizedSandboxContext> EnsureUnchangedAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        CancellationToken cancellationToken)
    {
        await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        var latest = await egressManager.GetAuthorizedRunSelectionAsync(
            caller, context.Fence.Owner, cancellationToken).ConfigureAwait(false);
        if (!EnvironmentEgressManager.SameAuthorizationContext(
                context.Selection.Authorization,
                latest.Authorization) ||
            !JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(context.Selection.RunSelection, JsonOptions),
                JsonSerializer.SerializeToNode(latest.RunSelection, JsonOptions)))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current Projects authorization or immutable run selection changed during the Sandbox operation.");
        return new(context.Fence, latest);
    }

    private EnvironmentEgressSelector CreateEgressSelector(
        EnvironmentGenerationFence fence,
        EnvironmentEgressManager.AuthorizedSelection selection) =>
        EnvironmentEgressSelector.Create(
            fence.Owner.EnvironmentId,
            selection.TenantId,
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            ciliumOptions.Namespace);

    private static EffectiveProviderCandidate GetExclusiveCandidate(
        EffectiveNetworkPolicySelection selection,
        ProviderSeam seam)
    {
        var matches = selection.Providers.Where(provider => provider.Seam == seam).ToArray();
        if (matches.Length != 1 ||
            matches[0].Cardinality != ProviderCardinality.Exclusive ||
            matches[0].Candidates.IsDefaultOrEmpty ||
            matches[0].Candidates.Length != 1 ||
            matches[0].Candidates[0].Seam != seam)
            throw new NotSupportedException(
                $"The immutable run selection does not contain exactly one exclusive {seam} provider.");
        return matches[0].Candidates[0];
    }

    private static SandboxDescribeRequest CreateDescribeRequest(SandboxLeaseSnapshot lease) =>
        new(
            lease.Fence,
            lease.ProvisionedResource?.Resource
                ?? throw new EnvironmentLifecycleException(
                    "sandbox_resource_unknown",
                    "The Sandbox lease has no pinned provider resource."),
            lease.ProviderFencingGeneration,
            lease.ProvisionedResource.ProviderBinding);

    private static string RequiredJsonString(JsonElement document, string name)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new EnvironmentLifecycleException(
                "workspace_provider_binding_invalid",
                "The pinned Workspace provider descriptor is missing a required identity.");
        return value.GetString()!;
    }

    private static bool SameWorkspace(WorkspaceContext left, WorkspaceContext right) =>
        left.Snapshot.Resource == right.Snapshot.Resource &&
        left.Snapshot.ResourceGeneration == right.Snapshot.ResourceGeneration &&
        left.Snapshot.DataGeneration == right.Snapshot.DataGeneration &&
        left.Snapshot.ProviderBinding is not null &&
        right.Snapshot.ProviderBinding is not null &&
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left.Snapshot.ProviderBinding, JsonOptions),
            JsonSerializer.SerializeToNode(right.Snapshot.ProviderBinding, JsonOptions));

    private static EnvironmentSandboxResult ToResult(
        SandboxLeaseSnapshot lease,
        SandboxObservation? observation = null)
    {
        var resource = lease.ProvisionedResource;
        var summary = observation is null
            ? null
            : new SandboxObservationSummary(
                observation.Resource,
                observation.State,
                observation.FencingGeneration,
                observation.VmIsolationVerified,
                observation.WorkspaceAttachmentVerified,
                observation.VerifiedNetworkGeneration,
                observation.StartupPhases,
                observation.TerminalEvidence);
        var phases = observation?.StartupPhases ??
            resource?.StartupPhases ??
            ImmutableArray<SandboxStartupPhaseObservation>.Empty;
        var ready = lease.State == SandboxLeaseState.Active &&
            observation?.State == SandboxObservedState.Ready;
        return new(
            lease.OperationId,
            lease.State,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.CurrentFencingGeneration,
            resource?.Resource,
            resource?.Endpoint,
            resource?.Placement,
            phases,
            lease.RetirementReason,
            summary,
            ready);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record WorkspaceContext(
        EnvironmentWorkspaceVolumeSnapshot Snapshot,
        WorkspaceVolumeSpec Specification,
        WorkspaceVolumeResource StorageResource,
        AgentSandboxPersistentVolumeClaimAttachment ClaimAttachment);

    private sealed record SandboxProviderRecoveryIntent(
        int ContractVersion,
        SandboxProvisionApiRequest Request,
        SandboxWorkspaceAttachment Workspace,
        ImmutableDictionary<string, string> EgressSelectorLabels);

    private sealed record AuthorizedSandboxContext(
        EnvironmentGenerationFence Fence,
        EnvironmentEgressManager.AuthorizedSelection Selection);
}
