using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
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

public sealed record EnvironmentSandboxPlacementProjectionV1(
    int ContractVersion,
    string TenantId,
    string ProjectId,
    string RunId,
    string EnvironmentId,
    long LifecycleGeneration,
    long CurrentFencingGeneration,
    long ProviderFencingGeneration,
    long LeaseRevision,
    DateTimeOffset LeaseExpiresAt,
    bool IsCurrent,
    SandboxLeaseState State,
    ProviderResourceRef Resource,
    SandboxEndpointReference Endpoint,
    SandboxPlacementReference Placement)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxImageIdentity? Image { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentRuntimeReadinessEvidence? RuntimeReadiness { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentRuntimeWorkspaceEvidence? RuntimeWorkspace { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentSandboxConsumerPinV1? ProviderPin { get; init; }
    [JsonIgnore]
    public RuntimeWorkspaceResumeRequest? RuntimeResume { get; init; }
}

public sealed record EnvironmentSandboxConsumerPinV1(
    int ContractVersion,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    ProviderResourceRef Resource,
    ImmutableHashSet<string> NegotiatedCapabilities);

public sealed record EnvironmentRuntimeReadinessEvidence(
    DateTimeOffset LeaseCreatedAt,
    SandboxObservation Observation,
    SandboxStartupTimeBudgets StartupBudgets,
    string WorkspaceMountPath);

public sealed record EnvironmentRuntimeWorkspaceEvidence(
    WorkspaceVolumeAttachmentNegotiation Workspace,
    long TransitionRevision);

public sealed class EnvironmentSandboxManager(
    IProjectsConfigClient projects,
    IEnvironmentLifecycleStore lifecycleStore,
    ISandboxLeaseStore leaseStore,
    ISandboxProvider sandboxProvider,
    AgentSandboxOptions sandboxOptions,
    KubernetesAgentSandboxClient kubernetesClient,
    CiliumEgressProviderOptions ciliumOptions,
    EnvironmentEgressManager egressManager,
    EnvironmentRuntimeSuspendReleaseAuthority? suspendReleaseAuthority = null)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public Task<EnvironmentSandboxResult> ProvisionAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxProvisionApiRequest request,
        CancellationToken cancellationToken) =>
        ProvisionCoreAsync(caller, projectId, runId, environmentId, request, cancellationToken);

    internal Task<EnvironmentSandboxResult> ProvisionResumeAsync(
        CurrentCallerRequest caller,
        SandboxProvisionApiRequest request,
        RuntimeWorkspaceResumeRequest resume,
        Func<CancellationToken, Task> requireCurrent,
        CancellationToken cancellationToken)
    {
        resume.Validate();
        var owner = resume.SourceManifest.EnvironmentFence!.Owner;
        return ProvisionCoreAsync(caller, owner.ProjectId, owner.RunId, owner.EnvironmentId,
            request, cancellationToken, resume, requireCurrent);
    }

    private async Task<EnvironmentSandboxResult> ProvisionCoreAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxProvisionApiRequest request,
        CancellationToken cancellationToken,
        RuntimeWorkspaceResumeRequest? resume = null,
        Func<CancellationToken, Task>? requireCurrent = null)
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
        if (requireCurrent is not null)
            await requireCurrent(cancellationToken).ConfigureAwait(false);
        var reservation = await leaseStore.ReserveProvisionAsync(
            context.Fence,
            request.IdempotencyKey,
            intent,
            cancellationToken).ConfigureAwait(false);
        var lease = reservation.Lease;
        if (lease.State != SandboxLeaseState.Provisioning)
        {
            if (requireCurrent is not null)
                await requireCurrent(cancellationToken).ConfigureAwait(false);
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
        var providerAttemptStarted = false;
        Guid? providerAttemptId = null;
        var workspaceAttachmentAttempted = false;
        long? workspaceAttachmentTransitionRevision = null;
        SandboxProvisionedResource? provisioned = null;
        try
        {
            if (requireCurrent is not null)
                await requireCurrent(cancellationToken).ConfigureAwait(false);
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
            workspaceAttachmentTransitionRevision = GetWorkspaceAttachmentTransitionRevision(workspace.Snapshot);
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
                new SandboxProviderRecoveryIntent(
                    1,
                    request,
                    provisionRequest.Workspace,
                    selector.MatchLabels,
                    workspaceAttachmentTransitionRevision.Value)
                {
                    RuntimeResume = resume
                },
                JsonOptions);
            if (requireCurrent is not null)
                await requireCurrent(cancellationToken).ConfigureAwait(false);
            lease = await leaseStore.SaveProviderRequestAsync(
                lease.OperationId,
                context.Fence,
                recoveryIntent,
                cancellationToken).ConfigureAwait(false);
            if (lease.State != SandboxLeaseState.Provisioning)
                return ToResult(lease);

            if (requireCurrent is not null)
                await requireCurrent(cancellationToken).ConfigureAwait(false);
            workspaceAttachmentAttempted = true;
            await AttachWorkspaceForSandboxAsync(
                context.Fence,
                lease.OperationId,
                workspace.Snapshot,
                workspaceAttachmentTransitionRevision.Value,
                cancellationToken).ConfigureAwait(false);
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
            if (!SameWorkspace(workspace, latestWorkspace) ||
                latestWorkspace.Snapshot.Phase != EnvironmentWorkspaceVolumeState.Attached ||
                latestWorkspace.Snapshot.TransitionRevision != workspaceAttachmentTransitionRevision.Value)
                throw new EnvironmentLifecycleException(
                    "workspace_generation_changed",
                    "The exact Workspace resource or data generation changed before Sandbox provisioning.");

            await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
            await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
            if (requireCurrent is not null)
                await requireCurrent(cancellationToken).ConfigureAwait(false);
            providerAttemptStarted = true;
            var attemptReservation = await leaseStore.BeginProviderAttemptAsync(
                SandboxProviderAttemptRequest.ForProvision(lease),
                cancellationToken).ConfigureAwait(false);
            if (attemptReservation.Replayed ||
                attemptReservation.Attempt.State != SandboxProviderAttemptState.InProgress)
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_unresolved",
                    "A durable Sandbox provider attempt already exists; another provision call was not started.",
                    SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));
            providerAttemptId = attemptReservation.Attempt.AttemptId;
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
                effectMayHaveApplied: exception.EffectMayHaveApplied,
                cancellationToken: CancellationToken.None,
                providerAttemptId: providerAttemptId,
                providerErrorCode: exception.Code).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (!providerCallStarted && workspaceAttachmentAttempted)
                await EnsureWorkspaceAttachmentReleasedAsync(
                    context.Fence,
                    lease.OperationId,
                    workspace.Snapshot,
                    workspaceAttachmentTransitionRevision!.Value,
                    CancellationToken.None).ConfigureAwait(false);
            if (providerAttemptId is not null)
                _ = await leaseStore.CompleteProvisionAsync(
                    lease.OperationId,
                    context.Fence,
                    provisionedResource: null,
                    effectMayHaveApplied: true,
                    cancellationToken: CancellationToken.None,
                    providerAttemptId,
                    providerErrorCode: "provider_cancelled").ConfigureAwait(false);
            else if (!providerAttemptStarted)
                _ = await leaseStore.CompleteProvisionAsync(
                    lease.OperationId,
                    context.Fence,
                    provisionedResource: null,
                    effectMayHaveApplied: false,
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
                cancellationToken: CancellationToken.None,
                providerAttemptId,
                providerErrorCode: "provider_invalid_result").ConfigureAwait(false);
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
                cancellationToken: CancellationToken.None,
                providerAttemptId,
                providerErrorCode: "provider_transport_unknown").ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            !providerAttemptStarted && exception is (ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                RuntimeAuthorizationException or
                ProjectsConfigApiException or
                EnvironmentLifecycleException or
                CiliumPolicyException or
                HttpRequestException))
        {
            if (workspaceAttachmentAttempted)
                await EnsureWorkspaceAttachmentReleasedAsync(
                    context.Fence,
                    lease.OperationId,
                    workspace.Snapshot,
                    workspaceAttachmentTransitionRevision!.Value,
                    CancellationToken.None).ConfigureAwait(false);
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
            cancellationToken: CancellationToken.None,
            providerAttemptId: providerAttemptId).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        if (requireCurrent is not null)
            await requireCurrent(cancellationToken).ConfigureAwait(false);
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

    public Task<EnvironmentSandboxPlacementProjectionV1?> GetCurrentPlacementAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken) =>
        GetCurrentPlacementCoreAsync(
            caller, projectId, runId, environmentId, runBoundRead: false,
            (projection, _) => Task.FromResult(projection), cancellationToken);

    internal Task<EnvironmentSandboxPlacementProjectionV1?> GetCurrentRunBoundPlacementAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken) =>
        GetCurrentPlacementCoreAsync(
            caller, projectId, runId, environmentId, runBoundRead: true,
            (projection, _) => Task.FromResult(projection), cancellationToken);

    internal async Task<TResult> GetCurrentPlacementCoreAsync<TResult>(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        bool runBoundRead,
        Func<EnvironmentSandboxPlacementProjectionV1?, CancellationToken, Task<TResult>> project,
        CancellationToken cancellationToken,
        bool includeRuntimeReadiness = false,
        bool includeRuntimeWorkspace = false,
        bool includeRuntimeEndpoint = false,
        Func<EnvironmentSandboxPlacementProjectionV1, bool>? requireEndpointWhen = null)
    {
        if ((includeRuntimeReadiness || includeRuntimeWorkspace || includeRuntimeEndpoint ||
                requireEndpointWhen is not null) && !runBoundRead)
            throw new ArgumentException("Runtime evidence requires run-bound placement-read authority.");
        var authorization = runBoundRead
            ? await egressManager.GetAuthorizedRunEnvironmentPlacementReadAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false)
            : await egressManager.GetAuthorizedRunEnvironmentControlAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(authorization.Owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        if (runBoundRead)
            await egressManager.EnsureRunEnvironmentPlacementReadAuthorizationUnchangedAsync(
                caller, authorization.Owner, authorization.Authorization, cancellationToken).ConfigureAwait(false);
        else
            await egressManager.EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
                caller, authorization.Owner, authorization.Authorization, cancellationToken).ConfigureAwait(false);
        if (includeRuntimeWorkspace && lease is not null)
            return await leaseStore.GetCurrentWithWorkspaceAsync(
                lifecycle.Fence, ReadProvisionRequest(lease).VolumeId,
                ProjectAsync, cancellationToken).ConfigureAwait(false);
        return await leaseStore.GetCurrentAsync(
            lifecycle.Fence, (currentLease, token) => ProjectAsync(currentLease, null, token),
            cancellationToken).ConfigureAwait(false);

        async Task<TResult> ProjectAsync(
            SandboxLeaseSnapshot? currentLease,
            EnvironmentWorkspaceVolumeSnapshot? workspace,
            CancellationToken callbackCancellationToken)
        {
            if (!SameCurrentPlacementLease(lease, currentLease))
                throw new EnvironmentLifecycleException(
                    "sandbox_lease_stale",
                    "The current Sandbox lease changed while its placement was being authorized.");
            if (runBoundRead)
                await egressManager.EnsureRunEnvironmentPlacementReadAuthorizationUnchangedAsync(
                    caller,
                    authorization.Owner,
                    authorization.Authorization,
                    callbackCancellationToken).ConfigureAwait(false);
            else
                await egressManager.EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
                    caller,
                    authorization.Owner,
                    authorization.Authorization,
                    callbackCancellationToken).ConfigureAwait(false);
            var projection = currentLease is null
                ? null
                : ProjectCurrentPlacement(
                    authorization.Owner,
                    lifecycle.Fence,
                    currentLease,
                    DateTimeOffset.UtcNow);
            if (projection is not null && includeRuntimeWorkspace)
                projection = projection with
                {
                    RuntimeWorkspace = ProjectRuntimeWorkspace(lifecycle.Fence, currentLease!, workspace)
                };
            var requireRuntimeEndpoint = projection is not null &&
                (includeRuntimeEndpoint || projection.RuntimeResume is not null ||
                 requireEndpointWhen?.Invoke(projection) == true);
            if (projection is not null && (includeRuntimeReadiness || requireRuntimeEndpoint))
            {
                var pinnedLease = currentLease!;
                var selection = pinnedLease.ProvisionIntent.SelectionSnapshot
                    .Deserialize<EffectiveNetworkPolicySelection>(JsonOptions)
                    ?? throw new EnvironmentLifecycleException(
                        "sandbox_selection_unavailable", "The Sandbox lease has no retained run selection.");
                var provision = ReadProvisionRequest(pinnedLease);
                if (selection.ProjectId != projectId || selection.RunId != runId)
                    throw new EnvironmentLifecycleException(
                        "sandbox_selection_stale", "The retained Sandbox selection has a different owner.");
                var selected = new EnvironmentEgressManager.AuthorizedSelection(
                    authorization.Authorization, selection);
                await egressManager.VerifyNetworkForSandboxAsync(
                    caller, lifecycle.Fence, selected, provision.NetworkPolicyGeneration,
                    callbackCancellationToken, runBoundRead).ConfigureAwait(false);
                var describe = CreateDescribeRequest(pinnedLease) with
                {
                    RequireRuntimeEndpoint = requireRuntimeEndpoint
                };
                var observation = (await sandboxProvider.DescribeAsync(describe, callbackCancellationToken)
                    .ConfigureAwait(false)).ValidateFor(describe);
                await egressManager.VerifyNetworkForSandboxAsync(
                    caller, lifecycle.Fence, selected, provision.NetworkPolicyGeneration,
                    callbackCancellationToken, runBoundRead).ConfigureAwait(false);
                if (observation.State is not (SandboxObservedState.Finished or SandboxObservedState.Absent))
                    observation = observation with { VerifiedNetworkGeneration = provision.NetworkPolicyGeneration };
                var pinnedOptions = pinnedLease.ProvisionIntent.OptionsSnapshot
                    .Deserialize<AgentSandboxOptions>(JsonOptions)
                    ?? throw new EnvironmentLifecycleException(
                        "sandbox_provider_binding_invalid", "The retained Sandbox options are missing.");
                pinnedOptions.Validate();
                projection = projection with
                {
                    RuntimeReadiness = new(describe.LeaseCreatedAt, observation.ValidateFor(describe),
                        pinnedOptions.StartupBudgets.ToContract(), provision.MountPath)
                };
            }
            return await project(projection, callbackCancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RuntimeSuspendPlacementReleaseReceipt> ReleaseSuspendPlacementAsync(
        CurrentCallerRequest caller,
        RuntimeSuspendPlacementReleaseRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        var host = request.HostReceipt;
        var binding = host.Registration.Binding;
        var context = await AuthorizeAsync(
            caller, binding.ProjectId, binding.RunId, binding.EnvironmentId, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetAsync(
            context.Fence, binding.PlacementGeneration, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException("sandbox_lease_unknown", "The suspended Sandbox is missing.");
        var detach = await ReadWorkspaceDetachmentAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);
        if (detach.SuspendRelease is null ||
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(detach.SuspendRelease, JsonOptions),
                JsonSerializer.SerializeToElement(request, JsonOptions)))
            throw new RuntimeAuthorizationException("runtime_suspend_release_evidence_mismatch");
        await RequireSuspendReleaseAsync(caller, detach.SuspendRelease, cancellationToken).ConfigureAwait(false);
        var result = await AbandonAsync(
            caller, binding.ProjectId, binding.RunId, binding.EnvironmentId,
            new(binding.PlacementGeneration, binding.EnvironmentProviderFencingGeneration,
                $"suspend-release-{host.OperationId:D}-{host.ManifestId:D}"),
            cancellationToken).ConfigureAwait(false);
        var volume = await lifecycleStore.GetWorkspaceVolumeAsync(
            context.Fence, host.WorkspaceFlush!.Volume.VolumeId, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException("workspace_volume_unknown", "The suspended Workspace is missing.");
        if (volume.Phase != EnvironmentWorkspaceVolumeState.Bound || volume.Resource is null)
            throw new RuntimeAuthorizationException("runtime_suspend_release_detachment_incomplete");
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, request, cancellationToken).ConfigureAwait(false);
        return new RuntimeSuspendPlacementReleaseReceipt(
            1, host.OperationId, host.ManifestId, context.Fence,
            result.Resource ?? throw new RuntimeAuthorizationException("runtime_suspend_release_resource_missing"),
            result.ProviderFencingGeneration, result.State, host.WorkspaceFlush.Volume, volume.Resource,
            volume.DataGeneration, volume.TransitionRevision).ValidateFor(request);
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
        if (lease.ProvisionedResource is not null)
        {
            var detach = await ReadWorkspaceDetachmentAsync(context.Fence, lease, cancellationToken)
                .ConfigureAwait(false);
            await RequireSuspendReleaseAsync(caller, detach.SuspendRelease, cancellationToken).ConfigureAwait(false);
        }
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

    public async Task<SandboxProviderAttemptReadResponseV1> GetProviderAttemptsAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A Sandbox operation ID is required.", nameof(operationId));
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var attempts = await leaseStore.GetProviderAttemptsAsync(
            context.Fence, operationId, cancellationToken).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return new(
            1,
            context.Fence,
            operationId,
            attempts.Select(SandboxProviderAttemptReceipt.From).ToImmutableArray());
    }

    public async Task<SandboxLateResourceCleanupReadResponseV1> GetLateResourceCleanupsAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var context = await AuthorizeAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var (items, hasMore) = await leaseStore.GetLateResourceCleanupRecordsAsync(
            context.Fence, cancellationToken).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return new(
            1,
            context.Fence,
            items.Select(item => item.Receipt).ToImmutableArray(),
            hasMore);
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
        var result = await ReconcileCurrentLeaseAsync(
            caller, context, networkPolicyGeneration, cancellationToken).ConfigureAwait(false);
        var lateCleanupResult = await ReconcileNextLateResourceCleanupAsync(
            caller, context, cancellationToken).ConfigureAwait(false);
        return result ?? lateCleanupResult;
    }

    private async Task<EnvironmentSandboxResult?> ReconcileCurrentLeaseAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        long? networkPolicyGeneration,
        CancellationToken cancellationToken)
    {
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
                var detach = await ReadWorkspaceDetachmentAsync(context.Fence, lease, cancellationToken)
                    .ConfigureAwait(false);
                await RequireSuspendReleaseAsync(caller, detach.SuspendRelease, cancellationToken).ConfigureAwait(false);
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
                new SandboxListOwnedRequest(
                    context.Fence,
                    lease.ProviderFencingGeneration,
                    lease.ProvisionIntent,
                    lease.CreatedAt ?? throw new EnvironmentLifecycleException(
                        "sandbox_lease_created_at_missing",
                        "The Sandbox lease has no persisted creation timestamp for startup budgets.")),
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
                    recoveredResource.ProviderBinding,
                    lease.CreatedAt ?? throw new EnvironmentLifecycleException(
                        "sandbox_lease_created_at_missing",
                        "The Sandbox lease has no persisted creation timestamp for startup budgets.")));
                var providerAttemptId = await RequireProvisionAttemptIdAsync(
                    context.Fence, lease, cancellationToken).ConfigureAwait(false);
                lease = await leaseStore.CompleteProvisionAsync(
                    lease.OperationId,
                    context.Fence,
                    recoveredResource,
                    effectMayHaveApplied: true,
                    cancellationToken: CancellationToken.None,
                    providerAttemptId).ConfigureAwait(false);
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
            if (recovered.Length == 0 &&
                lease.RetirementReason == SandboxRetirementReason.AuthorizedAbandon)
                return await ReleasePartialRetiringLeaseAsync(
                    caller, context, lease, cancellationToken).ConfigureAwait(false);
            if (recovered.Length == 0 &&
                lease.RetirementReason is null &&
                (lease.State is SandboxLeaseState.Provisioning or SandboxLeaseState.ReconciliationRequired) &&
                lease.IsCurrent &&
                lease.CurrentFencingGeneration == lease.ProviderFencingGeneration)
                return await ResumeProvisioningAsync(
                    caller, context, lease, cancellationToken).ConfigureAwait(false);
        }

        if (lease.State == SandboxLeaseState.Releasing)
            return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken).ConfigureAwait(false);
        return ToResult(lease);
    }

    private async Task<EnvironmentSandboxResult?> ReconcileNextLateResourceCleanupAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        CancellationToken cancellationToken)
    {
        var cleanup = await leaseStore.ClaimNextLateResourceCleanupAsync(
            context.Fence,
            cancellationToken).ConfigureAwait(false);
        if (cleanup is null)
            return null;

        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var detachment = await ReadWorkspaceDetachmentAsync(
            context.Fence, cleanup.Lease, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, cancellationToken)
            .ConfigureAwait(false);
        var request = new SandboxReleaseRequest(
            cleanup.Lease.Fence,
            cleanup.ProvisionedResource.Resource,
            cleanup.Lease.ProviderFencingGeneration,
            cleanup.ProvisionedResource.ProviderBinding,
            cleanup.IdempotencyKey).Validate();
        var attemptReservation = await leaseStore.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForLateResourceRelease(cleanup),
            cancellationToken).ConfigureAwait(false);
        if (attemptReservation.Attempt.State is not (
                SandboxProviderAttemptState.InProgress or SandboxProviderAttemptState.ResultRecorded) ||
            attemptReservation.Attempt.State == SandboxProviderAttemptState.InProgress &&
                attemptReservation.Replayed)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unresolved",
                "A prior late Sandbox cleanup attempt has no durable result; another release call was not started.",
                SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));

        SandboxReleaseReceipt receipt;
        if (attemptReservation.Replayed)
        {
            receipt = attemptReservation.Attempt.Outcome is { } savedOutcome
                ? JsonSerializer.Deserialize<SandboxReleaseReceipt>(savedOutcome.GetRawText(), JsonOptions)
                    ?? throw new InvalidOperationException("The saved late Sandbox release outcome is empty.")
                : throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_outcome_missing",
                    "The recorded late Sandbox release attempt has no receipt outcome.",
                    SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));
        }
        else
        {
            try
            {
                receipt = await sandboxProvider.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (SandboxProviderException exception)
            {
                await RecordLateCleanupFailureAsync(
                    new { errorCode = exception.Code, effectMayHaveApplied = exception.EffectMayHaveApplied },
                    exception.EffectMayHaveApplied,
                    exception.Code).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await RecordLateCleanupFailureAsync(
                    new { errorCode = "provider_cancelled", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException or TimeoutException or
                    ArgumentException or InvalidOperationException or JsonException)
            {
                await RecordLateCleanupFailureAsync(
                    new { errorCode = "provider_result_unknown", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_result_unknown").ConfigureAwait(false);
                throw;
            }

            try
            {
                receipt = receipt.ValidateFor(request);
            }
            catch (ArgumentException)
            {
                await RecordLateCleanupFailureAsync(
                    JsonSerializer.SerializeToElement(receipt, JsonOptions),
                    effectMayHaveApplied: true,
                    failureCode: "provider_invalid_receipt").ConfigureAwait(false);
                throw;
            }
            await leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptReservation.Attempt.AttemptId,
                cleanup.Lease.Fence,
                JsonSerializer.SerializeToElement(receipt, JsonOptions),
                effectMayHaveApplied: true,
                failureCode: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        receipt = receipt.ValidateFor(request);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, cancellationToken)
            .ConfigureAwait(false);
        await DetachWorkspaceAfterSandboxReleaseAsync(
            context.Fence,
            cleanup.Lease,
            cancellationToken,
            beforeCommitCheck: CheckLateCleanupAuthorityAsync).ConfigureAwait(false);
        await CheckLateCleanupAuthorityAsync(cancellationToken).ConfigureAwait(false);
        await leaseStore.CompleteLateResourceCleanupAsync(
            context.Fence,
            cleanup,
            receipt,
            CancellationToken.None,
            attemptReservation.Attempt.AttemptId).ConfigureAwait(false);
        return ToResult(cleanup.Lease);

        Task<SandboxProviderAttemptSnapshot> RecordLateCleanupFailureAsync<T>(
            T outcome,
            bool effectMayHaveApplied,
            string failureCode) =>
            leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptReservation.Attempt.AttemptId,
                cleanup.Lease.Fence,
                JsonSerializer.SerializeToElement(outcome, JsonOptions),
                effectMayHaveApplied,
                failureCode,
                CancellationToken.None);

        async Task CheckLateCleanupAuthorityAsync(CancellationToken token)
        {
            await EnsureUnchangedAsync(caller, context, token).ConfigureAwait(false);
            await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, token).ConfigureAwait(false);
        }
    }

    private async Task<EnvironmentSandboxResult> ResumeProvisioningAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        lease = await RequireCurrentLeaseAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);
        var recovery = ReadProviderRecoveryIntent(lease);
        if (!JsonNode.DeepEquals(
                JsonNode.Parse(lease.ProvisionIntent.SelectionSnapshot.GetRawText()),
                JsonSerializer.SerializeToNode(context.Selection.RunSelection, JsonOptions)))
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_selection_changed",
                "The current run selection differs from the immutable Sandbox provision selection.");
        var candidate = ResolveSandboxCandidate(context.Selection.RunSelection);
        if (candidate.Seam != ProviderSeam.Sandbox ||
            !string.Equals(candidate.ProviderId, lease.ProvisionIntent.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(
                candidate.AdapterVersion.ToString(),
                lease.ProvisionIntent.AdapterVersion,
                StringComparison.Ordinal) ||
            candidate.OptionsSchemaVersion != lease.ProvisionIntent.OptionsSchemaVersion ||
            !string.Equals(candidate.OptionsRevision, lease.ProvisionIntent.OptionsRevision, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_binding_unavailable",
                "The exact selected Sandbox provider revision is unavailable for recovery.");
        var provisionRequest = new SandboxProvisionRequest(
            context.Fence,
            candidate,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId,
            recovery.EgressSelectorLabels,
            recovery.Workspace).Validate();
        var currentContext = await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        _ = await egressManager.VerifyNetworkForSandboxAsync(
            caller,
            context.Fence,
            currentContext.Selection,
            recovery.Request.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
        await EnsureSandboxWorkspaceAttachedAsync(
            context,
            recovery,
            provisionRequest,
            cancellationToken).ConfigureAwait(false);
        currentContext = await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        _ = await egressManager.VerifyNetworkForSandboxAsync(
            caller,
            context.Fence,
            currentContext.Selection,
            recovery.Request.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
        lease = await RequireCurrentLeaseAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);

        SandboxProvisionedResource provisioned;
        var attemptReservation = await leaseStore.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForProvision(lease),
            cancellationToken).ConfigureAwait(false);
        if (attemptReservation.Replayed ||
            attemptReservation.Attempt.State != SandboxProviderAttemptState.InProgress)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unresolved",
                "A durable Sandbox provider attempt already exists; another provision call was not started.",
                SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));
        var providerAttemptId = attemptReservation.Attempt.AttemptId;
        try
        {
            provisioned = await sandboxProvider.ProvisionAsync(
                provisionRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SandboxProviderException or IOException or HttpRequestException or TimeoutException)
        {
            _ = await leaseStore.CompleteProvisionAsync(
                lease.OperationId,
                context.Fence,
                provisionedResource: null,
                effectMayHaveApplied: true,
                cancellationToken: CancellationToken.None,
                providerAttemptId,
                providerErrorCode: exception is SandboxProviderException providerException
                    ? providerException.Code
                    : "provider_result_unknown").ConfigureAwait(false);
            throw;
        }

        lease = await leaseStore.CompleteProvisionAsync(
            lease.OperationId,
            context.Fence,
            provisioned,
            effectMayHaveApplied: true,
            cancellationToken: CancellationToken.None,
            providerAttemptId).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        if (lease.State == SandboxLeaseState.Releasing)
            return await ReleaseRetiringLeaseAsync(caller, context, lease, cancellationToken).ConfigureAwait(false);
        if (lease.State == SandboxLeaseState.Active && lease.IsCurrent && recovery.Request.NetworkPolicyGeneration > 0)
            return await ObserveAsync(
                caller,
                context,
                lease,
                recovery.Request.NetworkPolicyGeneration,
                cancellationToken).ConfigureAwait(false);
        return ToResult(lease);
    }

    private async Task EnsureSandboxWorkspaceAttachedAsync(
        AuthorizedSandboxContext context,
        SandboxProviderRecoveryIntent recovery,
        SandboxProvisionRequest provisionRequest,
        CancellationToken cancellationToken)
    {
        var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
            provisionRequest.Candidate.ProviderId,
            provisionRequest.Candidate.OptionsRevision,
            context.Fence,
            provisionRequest.ResourceGeneration,
            provisionRequest.FencingGeneration,
            provisionRequest.OperationId);
        var workspace = await ReadWorkspaceAsync(
            context.Fence,
            context.Selection.RunSelection,
            recovery.Request,
            cancellationToken).ConfigureAwait(false);
        var expectedAttachment = NegotiateWorkspace(
            context.Fence, recovery.Request, workspace, plannedResource);
        if (!SameSandboxWorkspaceAttachment(expectedAttachment, provisionRequest.Workspace))
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_intent_invalid",
                "The current Workspace does not match the immutable Sandbox attachment request.");

        var targetRevision = recovery.WorkspaceAttachmentTransitionRevision;
        if (workspace.Snapshot.Phase == EnvironmentWorkspaceVolumeState.Bound &&
            workspace.Snapshot.TransitionRevision + 1 == targetRevision)
        {
            await AttachWorkspaceForSandboxAsync(
                context.Fence,
                provisionRequest.OperationId,
                workspace.Snapshot,
                targetRevision,
                cancellationToken).ConfigureAwait(false);
        }
        else if (workspace.Snapshot.Phase != EnvironmentWorkspaceVolumeState.Attached ||
                 workspace.Snapshot.TransitionRevision != targetRevision)
        {
            throw new EnvironmentLifecycleException(
                "workspace_attachment_state_invalid",
                "The Workspace is not at the exact persisted Sandbox attachment revision.");
        }

        var attached = await ReadWorkspaceAsync(
            context.Fence,
            context.Selection.RunSelection,
            recovery.Request,
            cancellationToken).ConfigureAwait(false);
        if (attached.Snapshot.Phase != EnvironmentWorkspaceVolumeState.Attached ||
            attached.Snapshot.TransitionRevision != targetRevision ||
            !SameSandboxWorkspaceAttachment(
                NegotiateWorkspace(context.Fence, recovery.Request, attached, plannedResource),
                provisionRequest.Workspace))
            throw new EnvironmentLifecycleException(
                "workspace_attachment_completion_invalid",
                "The Workspace owner did not preserve the exact Sandbox attachment during recovery.");
    }

    private async Task<EnvironmentSandboxResult> ReleasePartialRetiringLeaseAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        lease = await RequireCurrentLeaseAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);
        var request = new SandboxPartialReleaseRequest(context.Fence, lease).Validate();
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var attemptReservation = await leaseStore.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForPartialRelease(lease),
            cancellationToken).ConfigureAwait(false);
        if (attemptReservation.Attempt.State is not (
                SandboxProviderAttemptState.InProgress or SandboxProviderAttemptState.ResultRecorded) ||
            attemptReservation.Attempt.State == SandboxProviderAttemptState.InProgress &&
                attemptReservation.Replayed)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unresolved",
                "A prior Sandbox partial-release attempt has no durable result; another call was not started.",
                SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));

        SandboxPartialReleaseReceipt receipt;
        if (attemptReservation.Replayed)
        {
            receipt = attemptReservation.Attempt.Outcome is { } savedOutcome
                ? JsonSerializer.Deserialize<SandboxPartialReleaseReceipt>(
                    savedOutcome.GetRawText(), JsonOptions)
                    ?? throw new InvalidOperationException("The saved Sandbox partial-release outcome is empty.")
                : throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_outcome_missing",
                    "The recorded Sandbox partial-release attempt has no receipt outcome.",
                    SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));
        }
        else
        {
            try
            {
                receipt = await sandboxProvider.ReleasePartialAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SandboxProviderException exception)
            {
                await RecordPartialReleaseFailureAsync(
                    new { errorCode = exception.Code, effectMayHaveApplied = exception.EffectMayHaveApplied },
                    exception.EffectMayHaveApplied,
                    exception.Code).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await RecordPartialReleaseFailureAsync(
                    new { errorCode = "provider_cancelled", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException or TimeoutException or
                    ArgumentException or InvalidOperationException or JsonException)
            {
                await RecordPartialReleaseFailureAsync(
                    new { errorCode = "provider_result_unknown", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_result_unknown").ConfigureAwait(false);
                throw;
            }

            try
            {
                receipt = receipt.ValidateFor(request);
            }
            catch (ArgumentException)
            {
                await RecordPartialReleaseFailureAsync(
                    JsonSerializer.SerializeToElement(receipt, JsonOptions),
                    effectMayHaveApplied: true,
                    failureCode: "provider_invalid_receipt").ConfigureAwait(false);
                throw;
            }
            await leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptReservation.Attempt.AttemptId,
                context.Fence,
                JsonSerializer.SerializeToElement(receipt, JsonOptions),
                effectMayHaveApplied: true,
                failureCode: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        receipt = receipt.ValidateFor(request);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var current = await RequireCurrentLeaseAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);
        _ = receipt.ValidateFor(new SandboxPartialReleaseRequest(context.Fence, current));
        await DetachWorkspaceAfterSandboxReleaseAsync(
            context.Fence,
            current,
            cancellationToken,
            allowUnprovisionedResource: true).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        _ = await RequireCurrentLeaseAsync(context.Fence, lease, cancellationToken).ConfigureAwait(false);
        var released = await leaseStore.CompletePartialReleaseAsync(
            new SandboxPartialReleaseCompletionRequest(context.Fence, receipt)
            {
                ProviderAttemptId = attemptReservation.Attempt.AttemptId
            },
            CancellationToken.None).ConfigureAwait(false);
        return ToResult(released);

        Task<SandboxProviderAttemptSnapshot> RecordPartialReleaseFailureAsync<T>(
            T outcome,
            bool effectMayHaveApplied,
            string failureCode) =>
            leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptReservation.Attempt.AttemptId,
                context.Fence,
                JsonSerializer.SerializeToElement(outcome, JsonOptions),
                effectMayHaveApplied,
                failureCode,
                CancellationToken.None);
    }

    private async Task<SandboxLeaseSnapshot> RequireCurrentLeaseAsync(
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot expected,
        CancellationToken cancellationToken)
    {
        var current = await leaseStore.GetCurrentAsync(fence, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.OperationId != expected.OperationId ||
            current.ResourceGeneration != expected.ResourceGeneration ||
            current.State != expected.State ||
            current.CurrentFencingGeneration != expected.CurrentFencingGeneration ||
            current.ProviderFencingGeneration != expected.ProviderFencingGeneration ||
            current.LeaseRevision != expected.LeaseRevision)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease changed before its recovery effect could proceed.");
        return current;
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
        if (observation.State is not (SandboxObservedState.Finished or SandboxObservedState.Absent))
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
        var detachment = await ReadWorkspaceDetachmentAsync(
            context.Fence, lease, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, cancellationToken).ConfigureAwait(false);
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
        var attemptReservation = await leaseStore.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForRelease(lease, provisionedResource),
            cancellationToken).ConfigureAwait(false);
        if (attemptReservation.Attempt.State is not (
                SandboxProviderAttemptState.InProgress or SandboxProviderAttemptState.ResultRecorded) ||
            attemptReservation.Attempt.State == SandboxProviderAttemptState.InProgress &&
                attemptReservation.Replayed)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unresolved",
                "A prior Sandbox release attempt has no durable result; another release call was not started.",
                SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));

        SandboxReleaseReceipt receipt;
        if (attemptReservation.Replayed)
        {
            receipt = attemptReservation.Attempt.Outcome is { } savedOutcome
                ? JsonSerializer.Deserialize<SandboxReleaseReceipt>(savedOutcome.GetRawText(), JsonOptions)
                    ?? throw new InvalidOperationException("The saved Sandbox release outcome is empty.")
                : throw new EnvironmentLifecycleException(
                    "sandbox_provider_attempt_outcome_missing",
                    "The recorded Sandbox release attempt has no receipt outcome.",
                    SandboxProviderAttemptReceipt.From(attemptReservation.Attempt));
        }
        else
        {
            try
            {
                receipt = await sandboxProvider.ReleaseAsync(releaseRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SandboxProviderException exception)
            {
                await RecordReleaseFailureAsync(
                    attemptReservation.Attempt.AttemptId,
                    new { errorCode = exception.Code, effectMayHaveApplied = exception.EffectMayHaveApplied },
                    exception.EffectMayHaveApplied,
                    exception.Code).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await RecordReleaseFailureAsync(
                    attemptReservation.Attempt.AttemptId,
                    new { errorCode = "provider_cancelled", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_cancelled").ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or HttpRequestException or TimeoutException or
                    ArgumentException or InvalidOperationException or JsonException)
            {
                await RecordReleaseFailureAsync(
                    attemptReservation.Attempt.AttemptId,
                    new { errorCode = "provider_result_unknown", effectMayHaveApplied = true },
                    effectMayHaveApplied: true,
                    failureCode: "provider_result_unknown").ConfigureAwait(false);
                throw;
            }

            try
            {
                receipt = receipt.ValidateFor(releaseRequest);
            }
            catch (ArgumentException)
            {
                await RecordReleaseFailureAsync(
                    attemptReservation.Attempt.AttemptId,
                    JsonSerializer.SerializeToElement(receipt, JsonOptions),
                    effectMayHaveApplied: true,
                    failureCode: "provider_invalid_receipt").ConfigureAwait(false);
                throw;
            }
            await leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptReservation.Attempt.AttemptId,
                lease.Fence,
                JsonSerializer.SerializeToElement(receipt, JsonOptions),
                effectMayHaveApplied: true,
                failureCode: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        receipt = receipt.ValidateFor(releaseRequest);
        var current = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.OperationId != lease.OperationId ||
            current.State != SandboxLeaseState.Releasing ||
            current.CurrentFencingGeneration != lease.CurrentFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease changed during provider release; the receipt was not accepted.");
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, cancellationToken).ConfigureAwait(false);
        await DetachWorkspaceAfterSandboxReleaseAsync(
            context.Fence,
            lease,
            cancellationToken,
            beforeCommitCheck: CheckReleaseAuthorityAsync).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, cancellationToken).ConfigureAwait(false);
        current = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.OperationId != lease.OperationId ||
            current.State != SandboxLeaseState.Releasing ||
            current.CurrentFencingGeneration != lease.CurrentFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The Sandbox lease changed while its Workspace attachment was being released.");
        var released = await leaseStore.CompleteReleaseAsync(
            lease.OperationId,
            context.Fence,
            lease.ProviderFencingGeneration,
            receipt,
            CancellationToken.None,
            CheckReleaseAuthorityAsync,
            attemptReservation.Attempt.AttemptId).ConfigureAwait(false);
        return ToResult(released);

        async Task CheckReleaseAuthorityAsync(CancellationToken token)
        {
            await EnsureUnchangedAsync(caller, context, token).ConfigureAwait(false);
            await RequireSuspendReleaseAsync(caller, detachment.SuspendRelease, token).ConfigureAwait(false);
        }

        Task<SandboxProviderAttemptSnapshot> RecordReleaseFailureAsync<T>(
            Guid attemptId,
            T outcome,
            bool effectMayHaveApplied,
            string failureCode) =>
            leaseStore.RecordProviderAttemptOutcomeAsync(
                attemptId,
                lease.Fence,
                JsonSerializer.SerializeToElement(outcome, JsonOptions),
                effectMayHaveApplied,
                failureCode,
                CancellationToken.None);
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
            snapshot.Phase is not (EnvironmentWorkspaceVolumeState.Bound or
                EnvironmentWorkspaceVolumeState.Attached) ||
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

    private async Task AttachWorkspaceForSandboxAsync(
        EnvironmentGenerationFence fence,
        Guid sandboxOperationId,
        EnvironmentWorkspaceVolumeSnapshot workspace,
        long targetTransitionRevision,
        CancellationToken cancellationToken)
    {
        var resource = workspace.Resource
            ?? throw new EnvironmentLifecycleException(
                "workspace_generation_mismatch",
                "The Workspace volume has no pinned Storage resource.");
        if (workspace.Phase is not (EnvironmentWorkspaceVolumeState.Bound or
                EnvironmentWorkspaceVolumeState.Attached) ||
            targetTransitionRevision < 2)
            throw new EnvironmentLifecycleException(
                "workspace_attachment_state_invalid",
                "The Workspace volume is not in the owner-bound state required for Sandbox attachment.");

        var reservation = await lifecycleStore.ReserveWorkspaceVolumeAttachAsync(
            fence,
            workspace.VolumeId,
            targetTransitionRevision - 1,
            workspace.ResourceGeneration,
            workspace.DataGeneration,
            WorkspaceAttachmentIdempotencyKey("attach", sandboxOperationId),
            cancellationToken).ConfigureAwait(false);
        if (reservation.EnvironmentFence != fence ||
            reservation.VolumeId != workspace.VolumeId ||
            reservation.Operation != EnvironmentWorkspaceVolumeOperation.Attach ||
            reservation.ExpectedTransitionRevision != targetTransitionRevision - 1 ||
            reservation.TargetTransitionRevision != targetTransitionRevision ||
            reservation.ExpectedResourceGeneration != workspace.ResourceGeneration ||
            reservation.ExpectedDataGeneration != workspace.DataGeneration ||
            reservation.CurrentResource != resource)
            throw new EnvironmentLifecycleException(
                "workspace_attachment_reservation_invalid",
                "The Workspace attachment reservation does not match the exact Sandbox mount.");

        var completed = await lifecycleStore.CompleteWorkspaceVolumeAttachAsync(
            reservation.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            effectVerified: true,
            cancellationToken).ConfigureAwait(false);
        if (completed.EnvironmentFence != fence ||
            completed.VolumeId != workspace.VolumeId ||
            completed.Operation != EnvironmentWorkspaceVolumeOperation.Attach ||
            completed.TargetTransitionRevision != targetTransitionRevision ||
            completed.TargetResourceGeneration != workspace.ResourceGeneration ||
            completed.TargetDataGeneration != workspace.DataGeneration ||
            completed.TargetResource != resource ||
            completed.TargetPhase != EnvironmentWorkspaceVolumeState.Attached ||
            completed.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Completed)
            throw new EnvironmentLifecycleException(
                "workspace_attachment_completion_invalid",
                "The Workspace owner did not commit the exact Sandbox attachment.");
    }

    private async Task EnsureWorkspaceAttachmentReleasedAsync(
        EnvironmentGenerationFence fence,
        Guid sandboxOperationId,
        EnvironmentWorkspaceVolumeSnapshot workspace,
        long targetTransitionRevision,
        CancellationToken cancellationToken)
    {
        await AttachWorkspaceForSandboxAsync(
            fence,
            sandboxOperationId,
            workspace,
            targetTransitionRevision,
            cancellationToken).ConfigureAwait(false);
        await DetachWorkspaceAttachmentAsync(
            fence,
            sandboxOperationId,
            new WorkspaceVolumeReference(fence.Owner.ProjectId, workspace.VolumeId, workspace.ResourceGeneration),
            workspace.Resource
                ?? throw new EnvironmentLifecycleException(
                    "workspace_generation_mismatch",
                    "The Workspace volume has no pinned Storage resource."),
            workspace.DataGeneration,
            targetTransitionRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DetachWorkspaceAfterSandboxReleaseAsync(
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken,
        bool allowUnprovisionedResource = false,
        Func<CancellationToken, Task>? beforeCommitCheck = null)
    {
        var detach = await ReadWorkspaceDetachmentAsync(
            fence, lease, cancellationToken, allowUnprovisionedResource).ConfigureAwait(false);
        var negotiation = ReadProviderRecoveryIntent(lease).Workspace.Negotiation;
        await DetachWorkspaceAttachmentAsync(
            fence, lease.OperationId, negotiation.Volume, negotiation.StorageResource,
            detach.DataGeneration, detach.TransitionRevision, cancellationToken, beforeCommitCheck)
            .ConfigureAwait(false);
    }

    private Task RequireSuspendReleaseAsync(
        CurrentCallerRequest caller, RuntimeSuspendPlacementReleaseRequest? request,
        CancellationToken cancellationToken) =>
        request is null ? Task.CompletedTask :
        (suspendReleaseAuthority ?? throw new RuntimeAuthorizationException(
            "runtime_suspend_release_owner_unavailable")).RequireCurrentAsync(caller, request, cancellationToken);

    private async Task<(long TransitionRevision, long DataGeneration,
        RuntimeSuspendPlacementReleaseRequest? SuspendRelease)> ReadWorkspaceDetachmentAsync(
        EnvironmentGenerationFence fence, SandboxLeaseSnapshot lease, CancellationToken cancellationToken,
        bool allowUnprovisionedResource = false)
    {
        var recovery = ReadProviderRecoveryIntent(lease);
        var negotiation = recovery.Workspace.Negotiation;
        var current = await lifecycleStore.GetWorkspaceVolumeAsync(fence, negotiation.Volume.VolumeId, cancellationToken)
            .ConfigureAwait(false) ??
            throw new EnvironmentLifecycleException("workspace_volume_unknown", "The retiring workspace is missing.");
        var evidence = await lifecycleStore.GetWorkspaceVolumeFlushEvidenceAsync(
            fence, negotiation.Volume.VolumeId, checked(recovery.WorkspaceAttachmentTransitionRevision + 1),
            cancellationToken).ConfigureAwait(false);
        return ResolveWorkspaceDetachment(fence, lease, current, evidence, allowUnprovisionedResource);
    }

    internal static (long TransitionRevision, long DataGeneration,
        RuntimeSuspendPlacementReleaseRequest? SuspendRelease) ResolveWorkspaceDetachment(
        EnvironmentGenerationFence fence, SandboxLeaseSnapshot lease,
        EnvironmentWorkspaceVolumeSnapshot current, WorkspaceVolumeFlushEvidence? evidence,
        bool allowUnprovisionedResource = false)
    {
        var recovery = ReadProviderRecoveryIntent(lease);
        var negotiation = recovery.Workspace.Negotiation;
        var resource = lease.ProvisionedResource?.Resource;
        var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
            lease.ProvisionIntent.ProviderId,
            lease.ProvisionIntent.OptionsRevision,
            fence,
            lease.ResourceGeneration,
            lease.ProviderFencingGeneration,
            lease.OperationId);
        var exactResource = resource is null
            ? allowUnprovisionedResource
            : resource.Generation == negotiation.SandboxResource.Generation &&
              string.Equals(resource.ProviderId, negotiation.SandboxResource.ProviderId, StringComparison.Ordinal);
        if (negotiation.EnvironmentFence != fence ||
            negotiation.Volume.ProjectId != fence.Owner.ProjectId ||
            negotiation.Volume.VolumeId != recovery.Request.VolumeId ||
            negotiation.Volume.ResourceGeneration != recovery.Request.VolumeResourceGeneration ||
            negotiation.DataGeneration != recovery.Request.DataGeneration ||
            negotiation.StorageResource.Generation != recovery.Request.VolumeResourceGeneration ||
            negotiation.SandboxResource.Generation != lease.ResourceGeneration ||
            !string.Equals(negotiation.SandboxResource.ProviderId, lease.ProvisionIntent.ProviderId, StringComparison.Ordinal) ||
            negotiation.SandboxResource != plannedResource ||
            !exactResource ||
            lease.Fence != fence ||
            current.EnvironmentFence != fence ||
            current.VolumeId != negotiation.Volume.VolumeId ||
            current.ResourceGeneration != negotiation.Volume.ResourceGeneration ||
            current.Resource != negotiation.StorageResource ||
            recovery.WorkspaceAttachmentTransitionRevision < 2)
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_intent_invalid",
                "The retiring Sandbox lease does not contain its exact Workspace attachment receipt.");

        var detachRevision = recovery.WorkspaceAttachmentTransitionRevision;
        var detachDataGeneration = negotiation.DataGeneration;
        RuntimeSuspendPlacementReleaseRequest? release = null;
        if (evidence is not null)
        {
            evidence.Validate();
            var host = evidence.RuntimeHostReceipt is { } saved
                ? saved.Deserialize<RuntimeHostSuspendReceipt>(JsonOptions) : null;
            var flush = evidence.Receipt;
            if (flush is null || host is null || host.WorkspaceFlush != flush ||
                host.Registration.Binding.TenantId != fence.Owner.TenantId ||
                host.Registration.Binding.ProjectId != fence.Owner.ProjectId ||
                host.Registration.Binding.RunId != fence.Owner.RunId ||
                host.Registration.Binding.EnvironmentId != fence.Owner.EnvironmentId ||
                host.Registration.Binding.EnvironmentLifecycleGeneration != fence.LifecycleGeneration ||
                host.Registration.Binding.PlacementUid != resource?.ResourceId ||
                host.Registration.Binding.PlacementGeneration != lease.ResourceGeneration ||
                host.Registration.Binding.PlacementProviderId != lease.ProvisionIntent.ProviderId ||
                host.Registration.Binding.EnvironmentProviderFencingGeneration != lease.ProviderFencingGeneration ||
                host.Registration.Binding.EnvironmentCurrentFencingGeneration !=
                    (lease.RetirementReason is null ? lease.CurrentFencingGeneration :
                        checked(lease.CurrentFencingGeneration - 1)) ||
                flush.EnvironmentFence != fence || flush.Volume != negotiation.Volume ||
                flush.Resource != negotiation.StorageResource ||
                flush.ExpectedDataGeneration != negotiation.DataGeneration ||
                flush.DataGeneration != checked(negotiation.DataGeneration + 1))
                throw new EnvironmentLifecycleException(
                    "workspace_detachment_flush_evidence_invalid",
                    "The changed workspace counters have no exact trusted suspend flush for this retiring lease.");
            detachRevision = checked(detachRevision + 1);
            detachDataGeneration = flush.DataGeneration;
            release = new RuntimeSuspendPlacementReleaseRequest(host, detachRevision).Validate();
        }
        if (current.DataGeneration != detachDataGeneration ||
            !(current.Phase == EnvironmentWorkspaceVolumeState.Attached &&
                current.TransitionRevision == detachRevision ||
              current.Phase == EnvironmentWorkspaceVolumeState.Bound &&
                current.TransitionRevision == checked(detachRevision + 1)))
            throw new EnvironmentLifecycleException(
                "workspace_detachment_flush_evidence_invalid",
                "The retiring Workspace is not the exact attachment or its completed detach replay.");
        return (detachRevision, detachDataGeneration, release);
    }

    private async Task DetachWorkspaceAttachmentAsync(
        EnvironmentGenerationFence fence,
        Guid sandboxOperationId,
        WorkspaceVolumeReference volume,
        ProviderResourceRef storageResource,
        long dataGeneration,
        long attachmentTransitionRevision,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null)
    {
        var reservation = await lifecycleStore.ReserveWorkspaceVolumeDetachAsync(
            fence,
            volume.VolumeId,
            attachmentTransitionRevision,
            volume.ResourceGeneration,
            dataGeneration,
            WorkspaceAttachmentIdempotencyKey("detach", sandboxOperationId),
            cancellationToken).ConfigureAwait(false);
        if (reservation.EnvironmentFence != fence ||
            reservation.VolumeId != volume.VolumeId ||
            reservation.Operation != EnvironmentWorkspaceVolumeOperation.Detach ||
            reservation.ExpectedTransitionRevision != attachmentTransitionRevision ||
            reservation.TargetTransitionRevision != attachmentTransitionRevision + 1 ||
            reservation.ExpectedResourceGeneration != volume.ResourceGeneration ||
            reservation.ExpectedDataGeneration != dataGeneration ||
            reservation.CurrentResource != storageResource)
            throw new EnvironmentLifecycleException(
                "workspace_detachment_reservation_invalid",
                "The Workspace detachment reservation does not match the exact Sandbox mount.");

        var completed = await lifecycleStore.CompleteWorkspaceVolumeDetachAsync(
            reservation.OperationId,
            fence,
            effectMayHaveApplied: true,
            storageResource,
            effectVerified: true,
            cancellationToken,
            beforeCommitCheck).ConfigureAwait(false);
        if (completed.EnvironmentFence != fence ||
            completed.VolumeId != volume.VolumeId ||
            completed.Operation != EnvironmentWorkspaceVolumeOperation.Detach ||
            completed.TargetTransitionRevision != attachmentTransitionRevision + 1 ||
            completed.TargetResourceGeneration != volume.ResourceGeneration ||
            completed.TargetDataGeneration != dataGeneration ||
            completed.TargetResource != storageResource ||
            completed.TargetPhase != EnvironmentWorkspaceVolumeState.Bound ||
            completed.TransitionState != EnvironmentWorkspaceVolumeTransitionState.Completed)
            throw new EnvironmentLifecycleException(
                "workspace_detachment_completion_invalid",
                "The Workspace owner did not commit the exact Sandbox detachment.");
    }

    internal static SandboxProvisionApiRequest ReadProvisionRequest(SandboxLeaseSnapshot lease) =>
        ReadProviderRecoveryIntent(lease).Request;

    internal static EnvironmentRuntimeWorkspaceEvidence ProjectRuntimeWorkspace(
        EnvironmentGenerationFence fence, SandboxLeaseSnapshot lease,
        EnvironmentWorkspaceVolumeSnapshot? snapshot)
    {
        var recovery = ReadProviderRecoveryIntent(lease);
        var attachment = recovery.Workspace.Negotiation;
        var request = recovery.Request;
        if (snapshot is null)
            throw new EnvironmentLifecycleException(
                "workspace_volume_unknown", "The exact Sandbox Workspace volume does not exist.");
        if (lease.Fence != fence || attachment.EnvironmentFence != fence ||
            attachment.SandboxResource != lease.ProvisionedResource?.Resource ||
            snapshot.EnvironmentFence != fence || snapshot.VolumeId != request.VolumeId ||
            attachment.Volume != new WorkspaceVolumeReference(
                fence.Owner.ProjectId, request.VolumeId, request.VolumeResourceGeneration) ||
            attachment.DataGeneration != request.DataGeneration ||
            attachment.MountPath != request.MountPath || attachment.ReadOnly != request.ReadOnly ||
            snapshot.Phase != EnvironmentWorkspaceVolumeState.Attached ||
            snapshot.TransitionRevision != recovery.WorkspaceAttachmentTransitionRevision ||
            snapshot.ResourceGeneration != request.VolumeResourceGeneration ||
            snapshot.DataGeneration != request.DataGeneration ||
            snapshot.Resource != attachment.StorageResource || snapshot.ProviderBinding is null)
            throw new EnvironmentLifecycleException(
                "workspace_attachment_stale", "The Workspace no longer matches the exact Sandbox attachment.");
        var spec = snapshot.Specification.Deserialize<WorkspaceVolumeSpec>(JsonOptions)
            ?? throw new EnvironmentLifecycleException(
                "workspace_specification_invalid", "The stored Workspace volume specification is invalid.");
        if (spec.ProjectId != fence.Owner.ProjectId || spec.VolumeId != request.VolumeId ||
            !spec.AllowsEnvironment(fence.Owner.ProjectId, fence.Owner.EnvironmentId) ||
            spec.Consistency != WorkspaceVolumeConsistency.Strict)
            throw new EnvironmentLifecycleException(
                "workspace_attachment_stale", "The Workspace specification does not match the Sandbox owner.");
        var binding = snapshot.ProviderBinding.ValidateFor(attachment.StorageResource);
        var descriptor = binding.ReleaseDescriptor;
        var currentAttachment = new SandboxWorkspaceAttachment(
            attachment, JsonSerializer.SerializeToElement(new AgentSandboxPersistentVolumeClaimAttachment(
                1, binding.ProviderId, RequiredJsonString(descriptor, "namespace"),
                RequiredJsonString(descriptor, "claimName"), RequiredJsonString(descriptor, "claimUid")), JsonOptions));
        if (!SameSandboxWorkspaceAttachment(currentAttachment, recovery.Workspace))
            throw new EnvironmentLifecycleException(
                "workspace_attachment_stale", "The pinned Workspace provider attachment has changed.");
        return new(attachment, snapshot.TransitionRevision);
    }

    private static SandboxProviderRecoveryIntent ReadProviderRecoveryIntent(SandboxLeaseSnapshot lease)
    {
        SandboxProviderRecoveryIntent recovery;
        try
        {
            recovery = JsonSerializer.Deserialize<SandboxProviderRecoveryIntent>(
                    lease.ProvisionIntent.ProviderRequest,
                    JsonOptions)
                ?? throw new JsonException("The stored Sandbox recovery intent is empty.");
        }
        catch (JsonException)
        {
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_intent_invalid",
                "The stored Sandbox recovery intent is invalid.");
        }

        _ = recovery.Request.Validate();
        _ = recovery.Workspace.Validate();
        if (recovery.ContractVersion != 1 ||
            recovery.WorkspaceAttachmentTransitionRevision < 2)
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_intent_invalid",
                "The stored Sandbox recovery intent does not match its current contract.");
        return recovery;
    }

    private static bool SameSandboxWorkspaceAttachment(
        SandboxWorkspaceAttachment left,
        SandboxWorkspaceAttachment right) =>
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left, JsonOptions),
            JsonSerializer.SerializeToNode(right, JsonOptions));

    private static long GetWorkspaceAttachmentTransitionRevision(
        EnvironmentWorkspaceVolumeSnapshot workspace) =>
        workspace.Phase switch
        {
            EnvironmentWorkspaceVolumeState.Bound =>
                checked(workspace.TransitionRevision + 1),
            EnvironmentWorkspaceVolumeState.Attached => workspace.TransitionRevision,
            _ => throw new EnvironmentLifecycleException(
                "workspace_attachment_state_invalid",
                "Sandbox attachment requires an owner-bound Workspace volume.")
        };

    private static string WorkspaceAttachmentIdempotencyKey(string operation, Guid sandboxOperationId) =>
        $"sandbox-{operation}-{sandboxOperationId:N}";

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
            lease.ProvisionedResource.ProviderBinding,
            lease.CreatedAt ?? throw new EnvironmentLifecycleException(
                "sandbox_lease_created_at_missing",
                "The Sandbox lease has no persisted creation timestamp for startup budgets."));

    private async Task<Guid> RequireProvisionAttemptIdAsync(
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        var attempts = await leaseStore.GetProviderAttemptsAsync(
            fence, lease.OperationId, cancellationToken).ConfigureAwait(false);
        var candidates = attempts.Where(attempt =>
                attempt.Request.EffectKind == SandboxProviderEffectKind.Provision &&
                attempt.Request.ResourceGeneration == lease.ResourceGeneration &&
                attempt.Request.Fence == lease.Fence &&
                attempt.State is SandboxProviderAttemptState.InProgress or
                    SandboxProviderAttemptState.ResultRecorded or
                    SandboxProviderAttemptState.Unknown or
                    SandboxProviderAttemptState.LegacyUnknown)
            .ToArray();
        if (candidates.Length != 1)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_attempt_unknown",
                "The recovered Sandbox resource does not have exactly one unresolved durable provision attempt.");
        return candidates[0].AttemptId;
    }

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
                observation.TerminalEvidence,
                observation.StartupFailure);
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

    internal static EnvironmentSandboxPlacementProjectionV1 ProjectCurrentPlacement(
        EnvironmentOwnerIdentity owner,
        EnvironmentGenerationFence fence,
        SandboxLeaseSnapshot lease,
        DateTimeOffset now)
    {
        if (lease.Fence != fence ||
            lease.Fence.Owner != owner ||
            !lease.IsCurrent ||
            lease.ProviderFencingGeneration != lease.CurrentFencingGeneration)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox lease does not match the active Environment owner fence.");
        if (lease.LeaseExpiresAt is not { } expiresAt || expiresAt <= now)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_expired",
                "The current Sandbox lease is expired.");
        if (lease.State != SandboxLeaseState.Active ||
            lease.ProvisionedResource is not { } provisionedResource)
            throw new EnvironmentLifecycleException(
                "sandbox_placement_unavailable",
                "The current Sandbox lease has no active provisioned placement.");

        _ = lease.Validate();
        _ = provisionedResource.Validate();
        if (provisionedResource.Resource.Generation != lease.ResourceGeneration ||
            !string.Equals(
                provisionedResource.Resource.ProviderId,
                lease.ProvisionIntent.ProviderId,
                StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_lease_stale",
                "The current Sandbox placement does not match its recorded lease.");

        SandboxImageIdentity? image = null;
        var binding = provisionedResource.ProviderBinding;
        if (binding.ProviderId == AgentSandboxProviderMetadata.ProviderId && binding.OptionsSchemaVersion >= 2)
        {
            var options = binding.OptionsSnapshot.Deserialize<AgentSandboxOptions>(JsonOptions)
                ?? throw new EnvironmentLifecycleException(
                    "sandbox_provider_binding_invalid", "The provisioned Sandbox options are missing.");
            options.Validate();
            if (options.OptionsSchemaVersion != binding.OptionsSchemaVersion ||
                options.OptionsRevision != binding.OptionsRevision)
                throw new EnvironmentLifecycleException(
                    "sandbox_provider_binding_invalid", "The provisioned Sandbox options do not match their binding.");
            image = new SandboxImageIdentity(options.ContainerImageDigest, options.ContainerImagePlatform,
                options.ContainerImageCompressedPullBytes).Validate();
        }

        return new(
            ContractVersion: 1,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            fence.LifecycleGeneration,
            lease.CurrentFencingGeneration,
            lease.ProviderFencingGeneration,
            lease.LeaseRevision,
            expiresAt,
            lease.IsCurrent,
            lease.State,
            provisionedResource.Resource,
            provisionedResource.Endpoint,
            provisionedResource.Placement)
        {
            Image = image,
            RuntimeResume = ReadRuntimeResume(lease),
            ProviderPin = new(1, binding.ProviderId, binding.AdapterVersion,
                binding.OptionsSchemaVersion, binding.OptionsRevision, provisionedResource.Resource,
                provisionedResource.NegotiatedCapabilities)
        };
    }

    internal static bool SameCurrentPlacementLease(
        SandboxLeaseSnapshot? left,
        SandboxLeaseSnapshot? right) =>
        left is null
            ? right is null
            : right is not null &&
              left.OperationId == right.OperationId &&
              left.Fence == right.Fence &&
              left.ResourceGeneration == right.ResourceGeneration &&
              left.LeaseRevision == right.LeaseRevision &&
              left.CurrentFencingGeneration == right.CurrentFencingGeneration &&
              left.ProviderFencingGeneration == right.ProviderFencingGeneration &&
              left.State == right.State &&
              left.IsCurrent == right.IsCurrent &&
              left.LeaseExpiresAt == right.LeaseExpiresAt;

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
        ImmutableDictionary<string, string> EgressSelectorLabels,
        long WorkspaceAttachmentTransitionRevision)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RuntimeWorkspaceResumeRequest? RuntimeResume { get; init; }
    }

    private static RuntimeWorkspaceResumeRequest? ReadRuntimeResume(SandboxLeaseSnapshot lease)
    {
        var request = lease.ProvisionIntent.ProviderRequest;
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("runtimeResume", out var resume) || resume.ValueKind == JsonValueKind.Null)
            return null;
        try
        {
            return (resume.Deserialize<RuntimeWorkspaceResumeRequest>(JsonOptions)
                ?? throw new JsonException("The retained resume request is empty.")).Validate();
        }
        catch (JsonException exception)
        {
            throw new EnvironmentLifecycleException("sandbox_resume_binding_invalid", exception.Message);
        }
    }

    private sealed record AuthorizedSandboxContext(
        EnvironmentGenerationFence Fence,
        EnvironmentEgressManager.AuthorizedSelection Selection);
}
