using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>HTTP input for creating a volume with its provider specification and idempotency key.</summary>
public sealed record CreateWorkspaceVolumeApiRequest(
    WorkspaceVolumeSpec Specification,
    string IdempotencyKey)
{
    /// <summary>Exact Core invocation expected for this P1-specific metadata creation.</summary>
    public P1InvocationBindingExpectation? P1Binding { get; init; }
}

/// <summary>
/// Applies caller authorization and runtime host evidence to workspace-volume lifecycle operations.
/// The underlying service and lifecycle store perform generation-fenced provider effects.
/// </summary>
public sealed class EnvironmentWorkspaceVolumeManager(
    IProjectsConfigClient projects,
    IEnvironmentLifecycleStore lifecycleStore,
    WorkspaceVolumeService workspaceVolumes,
    EnvironmentRuntimeBootstrapDelivery? runtimeDelivery = null,
    IEnvironmentP1InvocationBindingReader? p1BindingReader = null)
{
    /// <summary>Resolves the exact active volume flush request for a current suspend operation.</summary>
    public async Task<WorkspaceVolumeApiFlushRequest> ResolveSuspendAsync(
        CurrentCallerRequest caller,
        RuntimeHostSuspendRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Proof);
        ArgumentNullException.ThrowIfNull(request.Proof.Registration);
        if (request.WorkspaceFlush is not null)
            throw new RuntimeAuthorizationException("runtime_suspend_resolve_evidence_not_allowed");
        var delivery = runtimeDelivery ??
            throw new RuntimeAuthorizationException("workspace_flush_host_unavailable");
        var binding = request.Proof.Registration.Binding;
        var (fence, checkAuthorization, _) = await AuthorizeMutationWithProjectCheckAsync(
            caller, binding.ProjectId, binding.RunId, binding.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);
        var context = await delivery.ReadSuspendWorkspaceAsync(request, actor, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await workspaceVolumes.InspectAsync(
            fence, context.Workspace.Volume.VolumeId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_suspend_workspace_unavailable");
        var flush = workspaceVolumes.ResolveFlushRequest(
            snapshot, context, $"suspend-{request.OperationId:D}-{request.ManifestId:D}");
        var resolved = new WorkspaceVolumeApiFlushRequest(
            new WorkspaceVolumeApiTransitionRequest(
                snapshot.TransitionRevision, snapshot.ResourceGeneration,
                snapshot.DataGeneration, flush.IdempotencyKey, checked(snapshot.DataGeneration + 1)),
            request with { WorkspaceFlush = flush }).Validate();
        await delivery.RequireCurrentSuspendAsync(resolved.Suspend, actor, cancellationToken)
            .ConfigureAwait(false);
        await checkAuthorization(cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>Flushes a workspace only after host evidence, project authority, and the Environment fence are rechecked.</summary>
    public async Task<WorkspaceVolumeLifecycleResult> FlushAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiFlushRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Transition);
        ArgumentNullException.ThrowIfNull(request.Suspend);
        request.Validate();
        var delivery = runtimeDelivery ??
            throw new RuntimeAuthorizationException("workspace_flush_host_unavailable");
        var (fence, checkAuthorization, checkProjectAuthorization) =
            await AuthorizeMutationWithProjectCheckAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        return await workspaceVolumes.FlushAsync(
            fence,
            request.Transition.ToTransition(volumeId, WorkspaceVolumeTransitionKind.Flush),
            (workspace, token) => delivery.SuspendWorkspaceAsync(request.Suspend, workspace, actor, token),
            cancellationToken,
            checkAuthorization,
            async token =>
            {
                await checkProjectAuthorization(token).ConfigureAwait(false);
                await delivery.RequireCurrentSuspendAsync(request.Suspend, actor, token).ConfigureAwait(false);
                await checkProjectAuthorization(token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>Creates a volume after requiring WriteProjects authority for its project and run.</summary>
    public async Task<EnvironmentWorkspaceVolumeSnapshot> CreateAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        CreateWorkspaceVolumeApiRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.P1Binding is not null)
            throw new RuntimeAuthorizationException("p1_internal_route_required");
        ArgumentNullException.ThrowIfNull(request.Specification);
        ArgumentNullException.ThrowIfNull(request.IdempotencyKey);
        var (fence, checkAuthorization) = await AuthorizeMutationAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.Specification.VolumeId, volumeId, StringComparison.Ordinal))
            throw new ArgumentException("The Create specification volume ID must match the route.", nameof(request));
        return await workspaceVolumes.CreateAsync(
            fence,
            new WorkspaceVolumeTransitionRequest(
                volumeId,
                WorkspaceVolumeTransitionKind.Create,
                0,
                0,
                0,
                null,
                request.IdempotencyKey,
                request.Specification).ValidateFor(WorkspaceVolumeTransitionKind.Create),
            cancellationToken,
            checkAuthorization).ConfigureAwait(false);
    }

    /// <summary>Creates P1 volume metadata only under a retained Environment binding and exact run-bound authority.</summary>
    public async Task<EnvironmentWorkspaceVolumeSnapshot> CreateP1Async(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        CreateWorkspaceVolumeApiRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Specification);
        ArgumentNullException.ThrowIfNull(actor);
        var expectation = request.P1Binding?.Validate() ??
            throw new RuntimeAuthorizationException("p1_invocation_binding_expectation_required");
        ArgumentNullException.ThrowIfNull(request.IdempotencyKey);
        if (!string.Equals(request.Specification.VolumeId, volumeId, StringComparison.Ordinal))
            throw new ArgumentException("The Create specification volume ID must match the route.", nameof(request));

        var p1Context = await AuthorizeP1MutationAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        var bindingReader = p1BindingReader ??
            throw new RuntimeAuthorizationException("p1_invocation_binding_unavailable");
        var binding = await EnvironmentP1InvocationBindingVerifier.ReadAndValidateAsync(
            bindingReader,
            actor,
            p1Context.Fence.Owner,
            p1Context.Authorization,
            expectation,
            cancellationToken).ConfigureAwait(false);

        async Task CheckP1BindingAsync(CancellationToken token)
        {
            try
            {
                await p1Context.CheckAuthorization(token).ConfigureAwait(false);
                var currentAuthorization = await projects.GetAuthorizationContextAsync(caller, token)
                    .ConfigureAwait(false);
                var current = await EnvironmentP1InvocationBindingVerifier.ReadAndValidateAsync(
                    bindingReader,
                    actor,
                    p1Context.Fence.Owner,
                    currentAuthorization,
                    expectation,
                    token).ConfigureAwait(false);
                if (current != binding)
                    throw new RuntimeAuthorizationException("p1_invocation_binding_changed");
            }
            catch (RuntimeAuthorizationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RuntimeAuthorizationException(
                    "p1_invocation_binding_unavailable", null, exception);
            }
        }

        return await workspaceVolumes.CreateP1Async(
            p1Context.Fence,
            new WorkspaceVolumeTransitionRequest(
                volumeId,
                WorkspaceVolumeTransitionKind.Create,
                0,
                0,
                0,
                null,
                request.IdempotencyKey,
                request.Specification).ValidateFor(WorkspaceVolumeTransitionKind.Create),
            binding,
            cancellationToken,
            p1Context.CheckAuthorization,
            CheckP1BindingAsync).ConfigureAwait(false);
    }

    /// <summary>Provisioning wrapper that applies current authorization around the provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ProvisionAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        CancellationToken cancellationToken,
        RuntimeActorAuthorization? actor = null) =>
        ApplyAsync(
            caller, projectId, runId, environmentId, volumeId, request,
            WorkspaceVolumeTransitionKind.Provision,
            static (service, fence, transition, token, check) =>
                service.ProvisionAsync(fence, transition, token, check),
            cancellationToken,
            actor);

    /// <summary>Replacement wrapper that applies current authorization around the provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ReplaceAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            caller, projectId, runId, environmentId, volumeId, request,
            WorkspaceVolumeTransitionKind.Replace,
            static (service, fence, transition, token, check) =>
                service.ReplaceAsync(fence, transition, token, check),
            cancellationToken);

    /// <summary>Binding wrapper that applies current authorization around the provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> BindVolumeAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            caller, projectId, runId, environmentId, volumeId, request,
            WorkspaceVolumeTransitionKind.Bind,
            static (service, fence, transition, token, check) =>
                service.BindAsync(fence, transition, token, check),
            cancellationToken);

    /// <summary>Unbinding wrapper that applies current authorization around the provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> UnbindAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            caller, projectId, runId, environmentId, volumeId, request,
            WorkspaceVolumeTransitionKind.Unbind,
            static (service, fence, transition, token, check) =>
                service.UnbindAsync(fence, transition, token, check),
            cancellationToken);

    /// <summary>Release wrapper that applies current authorization around the provider operation.</summary>
    public Task<WorkspaceVolumeLifecycleResult> ReleaseAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        CancellationToken cancellationToken) =>
        ApplyAsync(
            caller, projectId, runId, environmentId, volumeId, request,
            WorkspaceVolumeTransitionKind.Release,
            static (service, fence, transition, token, check) =>
                service.ReleaseAsync(fence, transition, token, check),
            cancellationToken);

    /// <summary>Retries pending provider cleanup after rechecking the current Environment owner authority.</summary>
    public async Task<EnvironmentWorkspaceVolumeCleanupStatus?> RetryCleanupAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        var (fence, checkAuthorization) = await AuthorizeMutationAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        return await workspaceVolumes.RetryCleanupAsync(
            fence, cancellationToken, checkAuthorization).ConfigureAwait(false);
    }

    /// <summary>Reconciles cleanup created by a specific replacement operation after checking current authority.</summary>
    public async Task<EnvironmentWorkspaceVolumeCleanupStatus?> ReconcileCleanupAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        Guid sourceReplaceOperationId,
        CancellationToken cancellationToken)
    {
        if (sourceReplaceOperationId == Guid.Empty)
            throw new ArgumentException("A source Replace operation ID is required.", nameof(sourceReplaceOperationId));
        var (fence, checkAuthorization) = await AuthorizeMutationAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        return await workspaceVolumes.ReconcileCleanupAsync(
            fence,
            sourceReplaceOperationId,
            cancellationToken,
            checkAuthorization).ConfigureAwait(false);
    }

    /// <summary>Reads volume metadata after checking fresh ReadProjects authority before and after the read.</summary>
    public async Task<EnvironmentWorkspaceVolumeSnapshot?> InspectAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateRouteIdentity(projectId, runId, environmentId);
        _ = new WorkspaceVolumeReference(projectId, volumeId, 1).Validate();
        var initial = await ReadCurrentAuthorizationContextAsync(
            caller, projectId, runId, tenantId: null, cancellationToken).ConfigureAwait(false);
        RequirePermission(
            initial, projectId, ProjectAuthorizationPermission.ReadProjects,
            "project_read_not_authorized",
            "The current caller lacks fresh ReadProjects authority for this project.");
        var owner = new EnvironmentOwnerIdentity(initial.TenantId, projectId, runId, environmentId);
        await EnsureReadAuthorizationUnchangedAsync(
            caller, owner, initial, cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false);
        var snapshot = lifecycle is null
            ? null
            : await workspaceVolumes.InspectAsync(
                lifecycle.Fence, volumeId, cancellationToken).ConfigureAwait(false);
        await EnsureReadAuthorizationUnchangedAsync(
            caller, owner, initial, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Dispatches a validated transition to the matching provider-backed workspace-volume operation.</summary>
    private async Task<WorkspaceVolumeLifecycleResult> ApplyAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string volumeId,
        WorkspaceVolumeApiTransitionRequest request,
        WorkspaceVolumeTransitionKind operation,
        Func<WorkspaceVolumeService, EnvironmentGenerationFence, WorkspaceVolumeTransitionRequest,
            CancellationToken, Func<CancellationToken, Task>, Task<WorkspaceVolumeLifecycleResult>> execute,
        CancellationToken cancellationToken,
        RuntimeActorAuthorization? actor = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.P1Binding is { } expectation)
        {
            if (operation != WorkspaceVolumeTransitionKind.Provision)
                throw new RuntimeAuthorizationException("p1_invocation_binding_operation_unsupported");
            if (actor is null)
                throw new RuntimeAuthorizationException("p1_invocation_binding_actor_required");
            var p1Context = await AuthorizeP1MutationAsync(
                caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
            var bindingReader = p1BindingReader ??
                throw new RuntimeAuthorizationException("p1_invocation_binding_unavailable");
            var binding = await EnvironmentP1InvocationBindingVerifier.ReadAndValidateAsync(
                bindingReader,
                actor,
                p1Context.Fence.Owner,
                p1Context.Authorization,
                expectation,
                cancellationToken).ConfigureAwait(false);

            async Task CheckP1Binding(CancellationToken token)
            {
                try
                {
                    await p1Context.CheckAuthorization(token).ConfigureAwait(false);
                    var currentAuthorization = await projects.GetAuthorizationContextAsync(caller, token)
                        .ConfigureAwait(false);
                    var current = await EnvironmentP1InvocationBindingVerifier.ReadAndValidateAsync(
                        bindingReader,
                        actor,
                        p1Context.Fence.Owner,
                        currentAuthorization,
                        expectation,
                        token).ConfigureAwait(false);
                    if (current != binding)
                        throw new RuntimeAuthorizationException("p1_invocation_binding_changed");
                }
                catch (RuntimeAuthorizationException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new RuntimeAuthorizationException(
                        "p1_invocation_binding_unavailable", null, exception);
                }
            }

            return await workspaceVolumes.ProvisionP1Async(
                p1Context.Fence,
                request.ToTransition(volumeId, operation),
                binding,
                cancellationToken,
                p1Context.CheckAuthorization,
                CheckP1Binding).ConfigureAwait(false);
        }

        if (actor is not null)
            throw new RuntimeAuthorizationException("p1_invocation_binding_request_invalid");
        var (fence, checkAuthorization, _) = await AuthorizeMutationWithProjectCheckAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        return await execute(
            workspaceVolumes,
            fence,
            request.ToTransition(volumeId, operation),
            cancellationToken,
            checkAuthorization).ConfigureAwait(false);
    }

    private async Task<(EnvironmentGenerationFence Fence,
        ProjectAuthorizationContextResponse Authorization,
        Func<CancellationToken, Task> CheckAuthorization)> AuthorizeP1MutationAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            string environmentId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateRouteIdentity(projectId, runId, environmentId);
        var initial = await ReadCurrentAuthorizationContextAsync(
            caller, projectId, runId, tenantId: null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(initial.BoundProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(initial.BoundRunId, runId, StringComparison.Ordinal) ||
            !HasPermission(initial, projectId, ProjectAuthorizationPermission.ReadRunSelection))
            throw new ProjectsConfigApiException(
                "run_selection_not_authorized",
                "P1 Environment creation requires ReadRunSelection authority bound to the exact project and run.");
        var owner = new EnvironmentOwnerIdentity(
            initial.TenantId, projectId, runId, environmentId);
        var lifecycle = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);

        async Task CheckAuthorization(CancellationToken token)
        {
            var current = await ReadCurrentAuthorizationContextAsync(
                caller, projectId, runId, owner.TenantId, token).ConfigureAwait(false);
            if (!EnvironmentEgressManager.SameAuthorizationContext(initial, current) ||
                !string.Equals(current.BoundProjectId, projectId, StringComparison.Ordinal) ||
                !string.Equals(current.BoundRunId, runId, StringComparison.Ordinal) ||
                !HasPermission(current, projectId, ProjectAuthorizationPermission.ReadRunSelection))
                throw new ProjectsConfigApiException(
                    "authorization_changed",
                    "The run-bound ReadRunSelection authority changed during the P1 Environment operation.");
        }

        return (lifecycle.Fence, initial, CheckAuthorization);
    }

    private async Task<(EnvironmentGenerationFence Fence, Func<CancellationToken, Task> CheckAuthorization)>
        AuthorizeMutationAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            string environmentId,
            CancellationToken cancellationToken)
    {
        var (fence, checkAuthorization, _) = await AuthorizeMutationWithProjectCheckAsync(
            caller, projectId, runId, environmentId, cancellationToken).ConfigureAwait(false);
        return (fence, checkAuthorization);
    }

    private async Task<(EnvironmentGenerationFence Fence, Func<CancellationToken, Task> CheckAuthorization,
        Func<CancellationToken, Task> CheckProjectAuthorization)>
        AuthorizeMutationWithProjectCheckAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            string environmentId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ValidateRouteIdentity(projectId, runId, environmentId);
        var initial = await ReadCurrentAuthorizationContextAsync(
            caller, projectId, runId, tenantId: null, cancellationToken).ConfigureAwait(false);
        RequirePermission(
            initial, projectId, ProjectAuthorizationPermission.WriteProjects,
            "project_write_not_authorized",
            "The current caller lacks fresh WriteProjects authority for this target project.");
        RequirePermission(
            initial, projectId, ProjectAuthorizationPermission.ReadRunSelection,
            "run_selection_not_authorized",
            "The current caller lacks the separately granted ReadRunSelection permission.");

        var selection = await projects.GetRunSelectionAsync(
            caller, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(selection.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(selection.RunId, runId, StringComparison.Ordinal) ||
            selection.ProjectRevision < 1 ||
            selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(selection.ContextRevision))
            throw new ProjectsConfigApiException(
                "invalid_run_selection",
                "Projects & Config returned an incomplete or mismatched immutable run selection.");

        var owner = new EnvironmentOwnerIdentity(initial.TenantId, projectId, runId, environmentId);
        await EnsureMutationAuthorizationUnchangedAsync(
            caller, owner, initial, cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        if (lifecycle.State == EnvironmentLifecycleState.Released)
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot accept workspace-volume operations.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        await EnsureMutationAuthorizationUnchangedAsync(
            caller, owner, initial, cancellationToken).ConfigureAwait(false);

        async Task CheckAuthorization(CancellationToken token)
        {
            await lifecycleStore.RequireActiveAsync(lifecycle.Fence, token).ConfigureAwait(false);
            await EnsureMutationAuthorizationUnchangedAsync(caller, owner, initial, token)
                .ConfigureAwait(false);
            await lifecycleStore.RequireActiveAsync(lifecycle.Fence, token).ConfigureAwait(false);
        }

        return (lifecycle.Fence, CheckAuthorization,
            token => EnsureMutationAuthorizationUnchangedAsync(caller, owner, initial, token));
    }

    private async Task EnsureMutationAuthorizationUnchangedAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse original,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAuthorizationContextAsync(
            caller, owner.ProjectId, owner.RunId, owner.TenantId, cancellationToken).ConfigureAwait(false);
        if (!SameAuthorizationContext(original, current) ||
            !HasPermission(current, owner.ProjectId, ProjectAuthorizationPermission.WriteProjects) ||
            !HasPermission(current, owner.ProjectId, ProjectAuthorizationPermission.ReadRunSelection))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the operation; its result is not accepted.");
    }

    private async Task EnsureReadAuthorizationUnchangedAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse original,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAuthorizationContextAsync(
            caller, owner.ProjectId, owner.RunId, owner.TenantId, cancellationToken).ConfigureAwait(false);
        if (!SameAuthorizationContext(original, current) ||
            !HasPermission(current, owner.ProjectId, ProjectAuthorizationPermission.ReadProjects))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the metadata read.");
    }

    private async Task<ProjectAuthorizationContextResponse> ReadCurrentAuthorizationContextAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var authorization = await projects.GetAuthorizationContextAsync(caller, cancellationToken)
            .ConfigureAwait(false);
        var boundProjectMatches = authorization.BoundProjectId is null ||
            string.Equals(authorization.BoundProjectId, projectId, StringComparison.Ordinal);
        var boundRunMatches = authorization.BoundRunId is null ||
            string.Equals(authorization.BoundRunId, runId, StringComparison.Ordinal);
        var bindingIsConsistent = authorization.BoundRunId is null ||
            string.Equals(authorization.BoundProjectId, projectId, StringComparison.Ordinal);
        if (authorization.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            string.IsNullOrWhiteSpace(authorization.Issuer) ||
            string.IsNullOrWhiteSpace(authorization.ActorId) ||
            string.IsNullOrWhiteSpace(authorization.TenantId) ||
            (tenantId is not null &&
                !string.Equals(authorization.TenantId, tenantId, StringComparison.Ordinal)) ||
            authorization.MembershipRevision < 1 ||
            authorization.EffectiveAuthority.IsDefault ||
            authorization.EffectiveAuthority.Any(resource =>
                resource is null ||
                !Enum.IsDefined(resource.ResourceType) ||
                string.IsNullOrWhiteSpace(resource.ResourceId) ||
                resource.Permissions.IsDefault ||
                resource.Permissions.Any(grant =>
                    grant is null ||
                    !Enum.IsDefined(grant.Permission) ||
                    grant.RoleRevision < 1)) ||
            !boundProjectMatches ||
            !boundRunMatches ||
            !bindingIsConsistent ||
            (caller.TenantSelector is not null &&
                !string.Equals(caller.TenantSelector, authorization.TenantId, StringComparison.Ordinal)))
            throw new ProjectsConfigApiException(
                "authorization_context_mismatch",
                "The fresh Projects & Config context does not match the current tenant/project/run owner.");
        return authorization;
    }

    private static void RequirePermission(
        ProjectAuthorizationContextResponse authorization,
        string projectId,
        ProjectAuthorizationPermission permission,
        string code,
        string message)
    {
        if (!HasPermission(authorization, projectId, permission))
            throw new ProjectsConfigApiException(code, message);
    }

    private static bool HasPermission(
        ProjectAuthorizationContextResponse authorization,
        string projectId,
        ProjectAuthorizationPermission permission) =>
        authorization.EffectiveAuthority.Any(resource =>
            resource.ResourceType == ProjectAuthorityResourceType.Project &&
            string.Equals(resource.ResourceId, projectId, StringComparison.Ordinal) &&
            resource.Permissions.Any(grant =>
                grant.Permission == permission && grant.RoleRevision > 0));

    private static bool SameAuthorizationContext(
        ProjectAuthorizationContextResponse left,
        ProjectAuthorizationContextResponse right)
    {
        if (left.ContractVersion != right.ContractVersion ||
            !string.Equals(left.Issuer, right.Issuer, StringComparison.Ordinal) ||
            !string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) ||
            !string.Equals(left.TenantId, right.TenantId, StringComparison.Ordinal) ||
            left.MembershipRevision != right.MembershipRevision ||
            !string.Equals(left.BoundProjectId, right.BoundProjectId, StringComparison.Ordinal) ||
            !string.Equals(left.BoundRunId, right.BoundRunId, StringComparison.Ordinal) ||
            left.EffectiveAuthority.Length != right.EffectiveAuthority.Length)
            return false;

        var leftAuthority = left.EffectiveAuthority
            .OrderBy(resource => resource.ResourceType)
            .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
        var rightAuthority = right.EffectiveAuthority
            .OrderBy(resource => resource.ResourceType)
            .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < leftAuthority.Length; index++)
        {
            var leftResource = leftAuthority[index];
            var rightResource = rightAuthority[index];
            if (leftResource.ResourceType != rightResource.ResourceType ||
                !string.Equals(leftResource.ResourceId, rightResource.ResourceId, StringComparison.Ordinal) ||
                leftResource.Permissions.Length != rightResource.Permissions.Length ||
                !leftResource.Permissions
                    .OrderBy(grant => grant.Permission)
                    .ThenBy(grant => grant.RoleRevision)
                    .SequenceEqual(rightResource.Permissions
                        .OrderBy(grant => grant.Permission)
                        .ThenBy(grant => grant.RoleRevision)))
                return false;
        }
        return true;
    }

    private static void ValidateRouteIdentity(string projectId, string runId, string environmentId)
    {
        _ = new EnvironmentOwnerIdentity("tenant-validation", projectId, runId, environmentId);
    }
}
