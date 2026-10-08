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
    SandboxTerminalEvidence? TerminalEvidence,
    SandboxStartupBudgetFailure? StartupFailure);

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
    public EnvironmentSandboxConsumerPinV1? ProviderPin { get; init; }
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
        var workspaceAttachmentAttempted = false;
        long? workspaceAttachmentTransitionRevision = null;
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
                    workspaceAttachmentTransitionRevision.Value),
                JsonOptions);
            lease = await leaseStore.SaveProviderRequestAsync(
                lease.OperationId,
                context.Fence,
                recoveryIntent,
                cancellationToken).ConfigureAwait(false);
            if (lease.State != SandboxLeaseState.Provisioning)
                return ToResult(lease);

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
            if (!providerCallStarted && workspaceAttachmentAttempted)
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
        bool includeRuntimeReadiness = false)
    {
        if (includeRuntimeReadiness && !runBoundRead)
            throw new ArgumentException("Runtime readiness requires run-bound placement-read authority.");
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
        return await leaseStore.GetCurrentAsync(
            lifecycle.Fence,
            async (currentLease, callbackCancellationToken) =>
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
                if (projection is not null && includeRuntimeReadiness)
                {
                    var pinnedLease = currentLease!;
                    var selection = pinnedLease.ProvisionIntent.SelectionSnapshot
                        .Deserialize<EffectiveNetworkPolicySelection>(JsonOptions)
                        ?? throw new EnvironmentLifecycleException(
                            "sandbox_selection_unavailable", "The Sandbox lease has no retained run selection.");
                    var provision = pinnedLease.ProvisionIntent.ProviderRequest
                        .Deserialize<SandboxProvisionApiRequest>(JsonOptions)
                        ?? throw new EnvironmentLifecycleException(
                            "sandbox_provision_intent_unavailable", "The Sandbox lease has no recorded provision request.");
                    provision.Validate();
                    if (selection.ProjectId != projectId || selection.RunId != runId)
                        throw new EnvironmentLifecycleException(
                            "sandbox_selection_stale", "The retained Sandbox selection has a different owner.");
                    var selected = new EnvironmentEgressManager.AuthorizedSelection(
                        authorization.Authorization, selection);
                    await egressManager.VerifyNetworkForSandboxAsync(
                        caller, lifecycle.Fence, selected, provision.NetworkPolicyGeneration,
                        callbackCancellationToken, runBoundRead).ConfigureAwait(false);
                    var describe = CreateDescribeRequest(pinnedLease);
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
            },
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
        var result = await ReconcileCurrentLeaseAsync(
            caller, context, networkPolicyGeneration, cancellationToken).ConfigureAwait(false);
        await ReconcileNextLateResourceCleanupAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return result;
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

    private async Task ReconcileNextLateResourceCleanupAsync(
        CurrentCallerRequest caller,
        AuthorizedSandboxContext context,
        CancellationToken cancellationToken)
    {
        var cleanup = await leaseStore.ClaimNextLateResourceCleanupAsync(
            context.Fence,
            cancellationToken).ConfigureAwait(false);
        if (cleanup is null)
            return;

        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var request = new SandboxReleaseRequest(
            cleanup.Lease.Fence,
            cleanup.ProvisionedResource.Resource,
            cleanup.Lease.ProviderFencingGeneration,
            cleanup.ProvisionedResource.ProviderBinding,
            cleanup.IdempotencyKey).Validate();
        var receipt = await sandboxProvider.ReleaseAsync(request, cancellationToken).ConfigureAwait(false);
        receipt = receipt.ValidateFor(request);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        await leaseStore.CompleteLateResourceCleanupAsync(
            context.Fence,
            cleanup,
            receipt,
            cancellationToken).ConfigureAwait(false);
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
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        lease = await leaseStore.CompleteProvisionAsync(
            lease.OperationId,
            context.Fence,
            provisioned,
            effectMayHaveApplied: true,
            cancellationToken).ConfigureAwait(false);
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
        var receipt = (await sandboxProvider.ReleasePartialAsync(
            request, cancellationToken).ConfigureAwait(false)).ValidateFor(request);
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
            new SandboxPartialReleaseCompletionRequest(context.Fence, receipt),
            cancellationToken).ConfigureAwait(false);
        return ToResult(released);
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
        await DetachWorkspaceAfterSandboxReleaseAsync(
            context.Fence,
            lease,
            cancellationToken).ConfigureAwait(false);
        await EnsureUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
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
            recovery.WorkspaceAttachmentTransitionRevision < 2)
            throw new EnvironmentLifecycleException(
                "sandbox_recovery_intent_invalid",
                "The retiring Sandbox lease does not contain its exact Workspace attachment receipt.");

        await DetachWorkspaceAttachmentAsync(
            fence,
            lease.OperationId,
            negotiation.Volume,
            negotiation.StorageResource,
            negotiation.DataGeneration,
            recovery.WorkspaceAttachmentTransitionRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DetachWorkspaceAttachmentAsync(
        EnvironmentGenerationFence fence,
        Guid sandboxOperationId,
        WorkspaceVolumeReference volume,
        ProviderResourceRef storageResource,
        long dataGeneration,
        long attachmentTransitionRevision,
        CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
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
        long WorkspaceAttachmentTransitionRevision);

    private sealed record AuthorizedSandboxContext(
        EnvironmentGenerationFence Fence,
        EnvironmentEgressManager.AuthorizedSelection Selection);
}
