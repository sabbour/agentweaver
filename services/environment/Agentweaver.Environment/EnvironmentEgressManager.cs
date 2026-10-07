using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Providers;

namespace Agentweaver.Environment;

public sealed record ApplyEnvironmentEgressRequest(
    EnvironmentGenerationFence Fence,
    long PolicyGeneration,
    long ExpectedPreviousPolicyGeneration,
    string IdempotencyKey);

public sealed record ReconcileEnvironmentEgressRequest(
    EnvironmentGenerationFence Fence,
    Guid OperationId);

public sealed record EnvironmentEgressOperationResult(
    bool ReadyForDispatch,
    bool Revoked,
    CiliumPolicyObservation? AppliedState,
    PinnedNetworkPolicyBinding? Binding,
    string? FailureCode,
    string? FailureMessage);

public sealed class EnvironmentEgressManager(
    IProjectsConfigClient projects,
    CiliumEgressPolicyAdapter cilium,
    CiliumEgressProviderOptions providerOptions,
    IEnvironmentLifecycleStore lifecycleStore)
{
    public async Task<EnvironmentEgressOperationResult> ApplyAndVerifyAsync(
        CurrentCallerRequest caller,
        ApplyEnvironmentEgressRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            var trace = TraceOperation("apply", request.PolicyGeneration);
            await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
            var selection = await GetAuthorizedSelectionAsync(
                caller, request.Fence.Owner, cancellationToken).ConfigureAwait(false);
            var intent = Compile(selection);
            var network = ResolveNetworkProvider(selection, providerOptions, cilium, intent);
            TraceProviderResolution(trace, network.Resolution);
            var selector = EnvironmentEgressSelector.Create(
                request.Fence.Owner.EnvironmentId,
                selection.TenantId,
                request.Fence.Owner.ProjectId,
                request.Fence.Owner.RunId,
                providerOptions.Namespace);
            var reservation = await lifecycleStore.ReserveNetworkEffectAsync(
                request.Fence,
                $"{selector.Namespace}/{selector.PolicyName}",
                request.PolicyGeneration,
                request.ExpectedPreviousPolicyGeneration,
                EnvironmentNetworkEffectKind.Apply,
                EffectIdempotencyKey(request.IdempotencyKey, EnvironmentNetworkEffectKind.Apply),
                cancellationToken).ConfigureAwait(false);
            if (reservation.State is EnvironmentNetworkEffectState.ReconciliationRequired or
                EnvironmentNetworkEffectState.Reconciled)
                return Failure(
                    "environment_effect_reconciliation_required",
                    "The Environment owner has a prior unresolved network-policy effect; no new policy may be applied.");
            if (reservation.State is EnvironmentNetworkEffectState.Failed or EnvironmentNetworkEffectState.Stale)
                return Failure(
                    "environment_effect_not_reusable",
                    "The Environment owner recorded this network-policy operation as non-reusable.");

            var providerCallStarted = false;
            CiliumPolicyObservation? observed = null;
            try
            {
                await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
                await EnsureAuthorizationUnchangedAsync(
                    caller,
                    request.Fence.Owner,
                    selection.Authorization,
                    requireRunSelection: true,
                    cancellationToken).ConfigureAwait(false);

                providerCallStarted = true;
                await cilium.ApplyAsync(
                    selector,
                    intent,
                    request.PolicyGeneration,
                    request.ExpectedPreviousPolicyGeneration,
                    cancellationToken).ConfigureAwait(false);
                observed = await cilium.VerifyAsync(
                    selector,
                    intent,
                    request.PolicyGeneration,
                    cancellationToken).ConfigureAwait(false);
                TracePolicyObservation(trace, observed);
                var completed = await lifecycleStore.CompleteNetworkEffectAsync(
                    reservation.OperationId,
                    request.Fence,
                    effectMayHaveApplied: true,
                    exactGenerationVerified: observed.ObjectVerified,
                    cancellationToken).ConfigureAwait(false);
                if (!observed.ObjectVerified)
                    return new(
                        false,
                        false,
                        observed,
                        null,
                        "policy_generation_unverified",
                        "The exact Cilium policy object was not observed after apply; the environment is not ready.");
                if (completed.State != EnvironmentNetworkEffectState.Completed)
                    return new(
                        false,
                        false,
                        observed,
                        null,
                        "environment_fence_changed",
                        "The Environment owner fence changed during policy apply; readiness is withheld pending owner reconciliation.");

                try
                {
                    await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
                    await EnsureAuthorizationUnchangedAsync(
                        caller,
                        request.Fence.Owner,
                        selection.Authorization,
                        requireRunSelection: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ProjectsConfigApiException exception)
                {
                    return new(
                        false,
                        false,
                        observed,
                        null,
                        GetPostcheckFailureCode(exception),
                        exception.Message);
                }
                catch (EnvironmentLifecycleException exception)
                {
                    return new(false, false, observed, null, exception.Code, exception.Message);
                }
                var pin = PinNetworkPolicy(
                    network.Resolver,
                    network.Resolution,
                    selector,
                    intent,
                    observed,
                    request.PolicyGeneration);
                if (!pin.IsSuccess)
                    return new(false, false, observed, null, pin.Error!.Code.ToString(), pin.Error.Message);
                return new(true, false, observed, pin.Value, null, null);
            }
            catch (Exception providerException)
            {
                try
                {
                    await lifecycleStore.CompleteNetworkEffectAsync(
                        reservation.OperationId,
                        request.Fence,
                        effectMayHaveApplied: providerCallStarted,
                        exactGenerationVerified: providerCallStarted && observed?.ObjectVerified == true,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception completionException) when (completionException is not OutOfMemoryException)
                {
                    throw new AggregateException(
                        "The provider operation failed and the Environment owner could not record its outcome.",
                        providerException,
                        completionException);
                }
                throw;
            }
        }
        catch (ProjectsConfigApiException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EnvironmentLifecycleException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EgressCompilationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (CiliumPolicyException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_environment_request", exception.Message);
        }
        catch (HttpRequestException)
        {
            return Failure(
                "upstream_unavailable",
                "Projects & Config or Kubernetes is unavailable; the environment remains not ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "upstream_timeout",
                "Projects & Config or Kubernetes did not complete the operation; the environment remains not ready.");
        }
    }

    public async Task<EnvironmentEgressOperationResult> VerifyAsync(
        CurrentCallerRequest caller,
        ApplyEnvironmentEgressRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            var trace = TraceOperation("verify", request.PolicyGeneration);
            await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
            var selection = await GetAuthorizedSelectionAsync(
                caller, request.Fence.Owner, cancellationToken).ConfigureAwait(false);
            var intent = Compile(selection);
            var network = ResolveNetworkProvider(selection, providerOptions, cilium, intent);
            TraceProviderResolution(trace, network.Resolution);
            var selector = EnvironmentEgressSelector.Create(
                request.Fence.Owner.EnvironmentId,
                selection.TenantId,
                request.Fence.Owner.ProjectId,
                request.Fence.Owner.RunId,
                providerOptions.Namespace);
            await lifecycleStore.RequireVerifiedNetworkPolicyGenerationAsync(
                request.Fence,
                $"{selector.Namespace}/{selector.PolicyName}",
                request.PolicyGeneration,
                cancellationToken).ConfigureAwait(false);
            var observed = await cilium.VerifyAsync(
                selector,
                intent,
                request.PolicyGeneration,
                cancellationToken).ConfigureAwait(false);
            TracePolicyObservation(trace, observed);
            if (!observed.ObjectVerified)
                return new(
                    false,
                    false,
                    observed,
                    null,
                    "policy_generation_unverified",
                    "The exact Cilium policy generation is not present; the environment is not ready.");

            await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureAuthorizationUnchangedAsync(
                    caller,
                    request.Fence.Owner,
                    selection.Authorization,
                    requireRunSelection: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProjectsConfigApiException exception)
            {
                return new(
                    false,
                    false,
                    observed,
                    null,
                    GetPostcheckFailureCode(exception),
                    exception.Message);
            }
            var pin = PinNetworkPolicy(
                network.Resolver,
                network.Resolution,
                selector,
                intent,
                observed,
                request.PolicyGeneration);
            if (!pin.IsSuccess)
                return new(false, false, observed, null, pin.Error!.Code.ToString(), pin.Error.Message);
            return new(true, false, observed, pin.Value, null, null);
        }
        catch (ProjectsConfigApiException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EnvironmentLifecycleException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EgressCompilationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (CiliumPolicyException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_environment_request", exception.Message);
        }
        catch (HttpRequestException)
        {
            return Failure(
                "upstream_unavailable",
                "Projects & Config or Kubernetes is unavailable; the environment remains not ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "upstream_timeout",
                "Projects & Config or Kubernetes did not complete the operation; the environment remains not ready.");
        }
    }

    public async Task<EnvironmentLifecycleSnapshot?> InspectLifecycleAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var initial = await projects.GetAuthorizationContextAsync(caller, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(initial.TenantId) ||
            initial.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            initial.MembershipRevision < 1 ||
            (initial.BoundProjectId is not null &&
                !string.Equals(initial.BoundProjectId, projectId, StringComparison.Ordinal)) ||
            (initial.BoundRunId is not null &&
                !string.Equals(initial.BoundRunId, runId, StringComparison.Ordinal)))
            throw new ProjectsConfigApiException(
                "authorization_context_mismatch",
                "The fresh Projects & Config context does not match the requested project/run.");
        RequirePermission(
            initial,
            projectId,
            ProjectAuthorizationPermission.ReadProjects,
            "project_read_not_authorized",
            "The current caller lacks fresh ReadProjects authority for this project.");

        var owner = new EnvironmentOwnerIdentity(initial.TenantId, projectId, runId, environmentId);
        var validated = await ReadCurrentAuthorizationContextAsync(caller, owner, cancellationToken)
            .ConfigureAwait(false);
        if (!SameAuthorizationContext(initial, validated) ||
            !HasPermission(validated, projectId, ProjectAuthorizationPermission.ReadProjects))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the metadata read.");
        var snapshot = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false);
        var postcheck = await ReadCurrentAuthorizationContextAsync(caller, owner, cancellationToken)
            .ConfigureAwait(false);
        if (!SameAuthorizationContext(validated, postcheck) ||
            !HasPermission(postcheck, projectId, ProjectAuthorizationPermission.ReadProjects))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the metadata read.");
        return snapshot;
    }

    public async Task<EnvironmentEgressOperationResult> RevokeAsync(
        CurrentCallerRequest caller,
        ApplyEnvironmentEgressRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            var trace = TraceOperation("revoke", request.PolicyGeneration);
            await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
            var authorization = await GetAuthorizedProjectContextAsync(
                caller, request.Fence.Owner, cancellationToken).ConfigureAwait(false);
            var selector = EnvironmentEgressSelector.Create(
                request.Fence.Owner.EnvironmentId,
                request.Fence.Owner.TenantId,
                request.Fence.Owner.ProjectId,
                request.Fence.Owner.RunId,
                providerOptions.Namespace);
            var reservation = await lifecycleStore.ReserveNetworkEffectAsync(
                request.Fence,
                $"{selector.Namespace}/{selector.PolicyName}",
                request.PolicyGeneration,
                request.ExpectedPreviousPolicyGeneration,
                EnvironmentNetworkEffectKind.Revoke,
                EffectIdempotencyKey(request.IdempotencyKey, EnvironmentNetworkEffectKind.Revoke),
                cancellationToken).ConfigureAwait(false);
            if (reservation.State is EnvironmentNetworkEffectState.ReconciliationRequired or
                EnvironmentNetworkEffectState.Reconciled)
                return Failure(
                    "environment_effect_reconciliation_required",
                    "The Environment owner has a prior unresolved network-policy effect; no revoke may be started.");
            if (reservation.State is EnvironmentNetworkEffectState.Failed or EnvironmentNetworkEffectState.Stale)
                return Failure(
                    "environment_effect_not_reusable",
                    "The Environment owner recorded this network-policy operation as non-reusable.");

            var providerCallStarted = false;
            CiliumPolicyObservation? observed = null;
            try
            {
                await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
                await EnsureAuthorizationUnchangedAsync(
                    caller,
                    request.Fence.Owner,
                    authorization,
                    requireRunSelection: false,
                    cancellationToken).ConfigureAwait(false);

                TraceProvider(
                    trace,
                    NetworkPolicyLayer.L3L4,
                    CiliumEgressPolicyAdapter.ProviderId,
                    providerOptions.AdapterVersion);
                providerCallStarted = true;
                observed = await cilium.RevokeAsync(
                    selector,
                    request.PolicyGeneration,
                    request.ExpectedPreviousPolicyGeneration,
                    cancellationToken).ConfigureAwait(false);
                TracePolicyObservation(trace, observed);
                var verified = observed.Exists && observed.ObjectVerified && observed.Revoked;
                var completed = await lifecycleStore.CompleteNetworkEffectAsync(
                    reservation.OperationId,
                    request.Fence,
                    effectMayHaveApplied: true,
                    exactGenerationVerified: verified,
                    cancellationToken).ConfigureAwait(false);
                if (!verified)
                    return new(
                        false,
                        observed.Revoked && observed.ObjectVerified,
                        observed,
                        null,
                        "policy_revoke_unverified",
                        "The generation-bound deny-all Cilium tombstone was not observed after revoke.");
                if (completed.State != EnvironmentNetworkEffectState.Completed)
                    return new(
                        false,
                        true,
                        observed,
                        null,
                        "environment_fence_changed",
                        "The Environment owner fence changed during revoke; the observed tombstone is retained and owner reconciliation is required.");

                try
                {
                    await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
                    await EnsureAuthorizationUnchangedAsync(
                        caller,
                        request.Fence.Owner,
                        authorization,
                        requireRunSelection: false,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ProjectsConfigApiException exception)
                {
                    return new(
                        false,
                        true,
                        observed,
                        null,
                        GetPostcheckFailureCode(exception),
                        exception.Message);
                }
                catch (EnvironmentLifecycleException exception)
                {
                    return new(false, true, observed, null, exception.Code, exception.Message);
                }
                return new(false, true, observed, null, null, null);
            }
            catch (Exception providerException)
            {
                try
                {
                    await lifecycleStore.CompleteNetworkEffectAsync(
                        reservation.OperationId,
                        request.Fence,
                        effectMayHaveApplied: providerCallStarted,
                        exactGenerationVerified: providerCallStarted &&
                            observed is { ObjectVerified: true, Revoked: true },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception completionException) when (completionException is not OutOfMemoryException)
                {
                    throw new AggregateException(
                        "The provider operation failed and the Environment owner could not record its outcome.",
                        providerException,
                        completionException);
                }
                throw;
            }
        }
        catch (ProjectsConfigApiException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EnvironmentLifecycleException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EgressCompilationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (CiliumPolicyException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_environment_request", exception.Message);
        }
        catch (HttpRequestException)
        {
            return Failure(
                "upstream_unavailable",
                "Projects & Config or Kubernetes is unavailable; revoke state is unknown.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "upstream_timeout",
                "Projects & Config or Kubernetes did not complete revoke; revoke state is unknown.");
        }
    }

    public async Task<EnvironmentEgressOperationResult> ReconcileAsync(
        CurrentCallerRequest caller,
        ReconcileEnvironmentEgressRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Fence);
            if (request.OperationId == Guid.Empty)
                throw new ArgumentException("A provider operation ID is required.", nameof(request));
            var trace = TraceOperation("reconcile");
            await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
            var selection = await GetAuthorizedSelectionAsync(
                caller, request.Fence.Owner, cancellationToken).ConfigureAwait(false);
            var reservation = await lifecycleStore.GetNetworkEffectAsync(
                request.OperationId,
                request.Fence,
                cancellationToken).ConfigureAwait(false);
            TraceRequestedGeneration(trace, reservation.PolicyGeneration);
            if (reservation.State is not (EnvironmentNetworkEffectState.ReconciliationRequired or
                EnvironmentNetworkEffectState.Reconciled))
                return Failure(
                    "environment_effect_not_reconcilable",
                    "Only an owner-recorded unresolved provider effect can be reconciled.");

            var intent = Compile(selection);
            var network = ResolveNetworkProvider(selection, providerOptions, cilium, intent);
            TraceProviderResolution(trace, network.Resolution);
            var selector = EnvironmentEgressSelector.Create(
                request.Fence.Owner.EnvironmentId,
                selection.TenantId,
                request.Fence.Owner.ProjectId,
                request.Fence.Owner.RunId,
                providerOptions.Namespace);
            var expectedResourceId = $"{selector.Namespace}/{selector.PolicyName}";
            if (!string.Equals(reservation.ResourceId, expectedResourceId, StringComparison.Ordinal))
                return Failure(
                    "environment_effect_owner_mismatch",
                    "The unresolved operation is not bound to this exact Environment policy resource.");

            var observed = reservation.Kind == EnvironmentNetworkEffectKind.Revoke
                ? await cilium.VerifyRevocationAsync(
                    selector,
                    reservation.PolicyGeneration,
                    cancellationToken).ConfigureAwait(false)
                : await cilium.VerifyAsync(
                    selector,
                    intent,
                    reservation.PolicyGeneration,
                    cancellationToken).ConfigureAwait(false);
            TracePolicyObservation(trace, observed);
            if (!observed.ObjectVerified)
                return new(
                    false,
                    false,
                    observed,
                    null,
                    "environment_effect_observation_mismatch",
                    "The current provider object does not exactly match the owner-recorded operation; reconciliation is withheld.");

            try
            {
                await lifecycleStore.RequireActiveAsync(request.Fence, cancellationToken).ConfigureAwait(false);
                await EnsureAuthorizationUnchangedAsync(
                    caller,
                    request.Fence.Owner,
                    selection.Authorization,
                    requireRunSelection: true,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProjectsConfigApiException exception)
            {
                return new(
                    false,
                    observed.Revoked,
                    observed,
                    null,
                    GetPostcheckFailureCode(exception),
                    exception.Message);
            }
            catch (EnvironmentLifecycleException exception)
            {
                return new(false, observed.Revoked, observed, null, exception.Code, exception.Message);
            }

            var reconciled = await lifecycleStore.MarkNetworkEffectReconciledAsync(
                reservation.OperationId,
                request.Fence,
                new EnvironmentNetworkEffectObservation(
                    observed.ObjectVerified,
                    observed.AppliedIntentGeneration,
                    observed.Revoked,
                    observed.IntentHash),
                cancellationToken).ConfigureAwait(false);
            if (reconciled.State != EnvironmentNetworkEffectState.Reconciled)
                return new(
                    false,
                    observed.Revoked,
                    observed,
                    null,
                    "environment_effect_reconciliation_conflict",
                    "The Environment owner did not accept the exact observed provider result.");
            if (reservation.Kind == EnvironmentNetworkEffectKind.Revoke)
                return new(false, true, observed, null, null, null);

            var pin = PinNetworkPolicy(
                network.Resolver,
                network.Resolution,
                selector,
                intent,
                observed,
                reservation.PolicyGeneration);
            if (!pin.IsSuccess)
                return new(false, false, observed, null, pin.Error!.Code.ToString(), pin.Error.Message);
            return new(true, false, observed, pin.Value, null, null);
        }
        catch (ProjectsConfigApiException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EnvironmentLifecycleException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (EgressCompilationException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (CiliumPolicyException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failure("invalid_environment_request", exception.Message);
        }
        catch (HttpRequestException)
        {
            return Failure(
                "upstream_unavailable",
                "Projects & Config or Kubernetes is unavailable; the environment remains not ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "upstream_timeout",
                "Projects & Config or Kubernetes did not complete reconciliation; the environment remains not ready.");
        }
    }

    private async Task<AuthorizedSelection> GetAuthorizedSelectionAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        var authorization = await GetAuthorizedProjectContextAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        RequirePermission(
            authorization,
            owner.ProjectId,
            ProjectAuthorizationPermission.ReadRunSelection,
            "run_selection_not_authorized",
            "The current caller lacks the separately granted permission to read the admitted project/run selection.");

        var selection = await projects.GetRunSelectionAsync(
            caller, owner.ProjectId, owner.RunId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(selection.ProjectId, owner.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(selection.RunId, owner.RunId, StringComparison.Ordinal) ||
            selection.ProjectRevision < 1 ||
            selection.ProjectConfigurationRevision < 1 ||
            selection.PlatformRuntimeRevision < 1 ||
            string.IsNullOrWhiteSpace(selection.ContextRevision))
            throw new ProjectsConfigApiException(
                "invalid_run_selection",
                "Projects & Config returned an incomplete or mismatched immutable run selection.");
        await EnsureAuthorizationUnchangedAsync(
            caller,
            owner,
            authorization,
            requireRunSelection: true,
            cancellationToken).ConfigureAwait(false);
        return new AuthorizedSelection(authorization, selection);
    }

    internal Task<AuthorizedSelection> GetAuthorizedRunSelectionAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken) =>
        GetAuthorizedSelectionAsync(caller, owner, cancellationToken);

    internal async Task<(EnvironmentOwnerIdentity Owner, ProjectAuthorizationContextResponse Authorization)>
        GetAuthorizedRunEnvironmentControlAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            string environmentId,
            CancellationToken cancellationToken)
    {
        var authorization = await ReadCurrentAuthorizationContextAsync(
            caller, projectId, runId, tenantId: null, cancellationToken).ConfigureAwait(false);
        RequirePermission(
            authorization,
            projectId,
            ProjectAuthorizationPermission.WriteProjects,
            "project_write_not_authorized",
            "The current caller lacks fresh WriteProjects authority for this target project.");
        return (
            new EnvironmentOwnerIdentity(authorization.TenantId, projectId, runId, environmentId),
            authorization);
    }

    internal Task EnsureRunEnvironmentControlAuthorizationUnchangedAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse authorization,
        CancellationToken cancellationToken) =>
        EnsureAuthorizationUnchangedAsync(
            caller, owner, authorization, requireRunSelection: false, cancellationToken);

    internal async Task<(EnvironmentOwnerIdentity Owner, ProjectAuthorizationContextResponse Authorization)>
        GetAuthorizedRunEnvironmentPlacementReadAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            string environmentId,
            CancellationToken cancellationToken)
    {
        var authorization = await ReadCurrentAuthorizationContextAsync(
            caller, projectId, runId, tenantId: null, cancellationToken).ConfigureAwait(false);
        if (!HasRunBoundPlacementReadAuthority(authorization, projectId, runId))
            throw new ProjectsConfigApiException(
                "run_selection_not_authorized",
                "The current caller must have ReadRunSelection authority bound to this exact project and run.");
        return (
            new EnvironmentOwnerIdentity(authorization.TenantId, projectId, runId, environmentId),
            authorization);
    }

    internal async Task EnsureRunEnvironmentPlacementReadAuthorizationUnchangedAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse authorization,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAuthorizationContextAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        if (!SameAuthorizationContext(authorization, current) ||
            !HasRunBoundPlacementReadAuthority(current, owner.ProjectId, owner.RunId))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's run-bound read authority changed during placement resolution.");
    }

    internal async Task<CiliumPolicyObservation> VerifyNetworkForSandboxAsync(
        CurrentCallerRequest caller,
        EnvironmentGenerationFence fence,
        AuthorizedSelection selection,
        long policyGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(fence);
        ArgumentNullException.ThrowIfNull(selection);
        if (policyGeneration < 1 ||
            selection.Authorization.TenantId != fence.Owner.TenantId ||
            selection.ProjectId != fence.Owner.ProjectId ||
            selection.RunId != fence.Owner.RunId)
            throw new ArgumentException("Sandbox Network verification scope is invalid.");

        await lifecycleStore.RequireActiveAsync(fence, cancellationToken).ConfigureAwait(false);
        CompiledEgressIntent intent;
        try
        {
            intent = Compile(selection);
        }
        catch (EgressCompilationException exception)
        {
            throw new CiliumPolicyException(exception.Code, exception.Message);
        }
        _ = ResolveNetworkProvider(selection, providerOptions, cilium, intent);
        var selector = EnvironmentEgressSelector.Create(
            fence.Owner.EnvironmentId,
            selection.TenantId,
            fence.Owner.ProjectId,
            fence.Owner.RunId,
            providerOptions.Namespace);
        await lifecycleStore.RequireVerifiedNetworkPolicyGenerationAsync(
            fence,
            $"{selector.Namespace}/{selector.PolicyName}",
            policyGeneration,
            cancellationToken).ConfigureAwait(false);
        var observation = await cilium.VerifyAsync(
            selector, intent, policyGeneration, cancellationToken).ConfigureAwait(false);
        if (!observation.ObjectVerified)
            throw new CiliumPolicyException(
                "policy_generation_unverified",
                "The exact Cilium policy generation is not present; Sandbox readiness is withheld.");
        await lifecycleStore.RequireActiveAsync(fence, cancellationToken).ConfigureAwait(false);
        await EnsureAuthorizationUnchangedAsync(
            caller,
            fence.Owner,
            selection.Authorization,
            requireRunSelection: true,
            cancellationToken).ConfigureAwait(false);
        return observation;
    }

    private async Task<ProjectAuthorizationContextResponse> GetAuthorizedProjectContextAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken)
    {
        var authorization = await ReadCurrentAuthorizationContextAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        RequirePermission(
            authorization,
            owner.ProjectId,
            ProjectAuthorizationPermission.WriteProjects,
            "project_write_not_authorized",
            "The current caller lacks fresh WriteProjects authority for this target project.");
        return authorization;
    }

    internal async Task EnsureAuthorizationUnchangedAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        ProjectAuthorizationContextResponse original,
        bool requireRunSelection,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAuthorizationContextAsync(
            caller, owner, cancellationToken).ConfigureAwait(false);
        if (!SameAuthorizationContext(original, current) ||
            !HasPermission(
                current,
                owner.ProjectId,
                ProjectAuthorizationPermission.WriteProjects) ||
            (requireRunSelection &&
                !HasPermission(
                    current,
                    owner.ProjectId,
                    ProjectAuthorizationPermission.ReadRunSelection)))
            throw new ProjectsConfigApiException(
                "authorization_changed",
                "The current caller's project authority changed during the operation; the result is not accepted.");
    }

    private async Task<ProjectAuthorizationContextResponse> ReadCurrentAuthorizationContextAsync(
        CurrentCallerRequest caller,
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken) =>
        await ReadCurrentAuthorizationContextAsync(
            caller,
            owner.ProjectId,
            owner.RunId,
            owner.TenantId,
            cancellationToken).ConfigureAwait(false);

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
        string failureCode,
        string failureMessage)
    {
        if (!HasPermission(authorization, projectId, permission))
            throw new ProjectsConfigApiException(failureCode, failureMessage);
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

    private static bool HasRunBoundPlacementReadAuthority(
        ProjectAuthorizationContextResponse authorization,
        string projectId,
        string runId) =>
        string.Equals(authorization.BoundProjectId, projectId, StringComparison.Ordinal) &&
        string.Equals(authorization.BoundRunId, runId, StringComparison.Ordinal) &&
        HasPermission(
            authorization,
            projectId,
            ProjectAuthorizationPermission.ReadRunSelection);

    private static string GetPostcheckFailureCode(ProjectsConfigApiException exception) =>
        exception.Code is "authorization_context_mismatch" or
            "authorization_context_denied" or
            "project_write_not_authorized" or
            "run_selection_not_authorized"
            ? "authorization_changed"
            : exception.Code;

    internal static bool SameAuthorizationContext(
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
                leftResource.Permissions.Length != rightResource.Permissions.Length)
                return false;

            var leftPermissions = leftResource.Permissions
                .OrderBy(grant => grant.Permission)
                .ThenBy(grant => grant.RoleRevision)
                .ToArray();
            var rightPermissions = rightResource.Permissions
                .OrderBy(grant => grant.Permission)
                .ThenBy(grant => grant.RoleRevision)
                .ToArray();
            if (!leftPermissions.SequenceEqual(rightPermissions))
                return false;
        }

        return true;
    }

    private static CompiledEgressIntent Compile(AuthorizedSelection selection)
    {
        var result = EgressIntentCompiler.Compile(selection.RunSelection);
        if (!result.IsSuccess)
            throw new EgressCompilationException(result.Failure!.Code, result.Failure.Message);
        return result.Intent!;
    }

    private static (ProviderResolver Resolver, NetworkPolicyResolution Resolution) ResolveNetworkProvider(
        AuthorizedSelection selection,
        CiliumEgressProviderOptions options,
        CiliumEgressPolicyAdapter cilium,
        CompiledEgressIntent intent)
    {
        var providerSelections = selection.RunSelection.Providers
            .Where(provider => provider.Seam == ProviderSeam.NetworkPolicy)
            .ToArray();
        if (providerSelections.Length != 1 ||
            providerSelections[0].Cardinality != ProviderCardinality.Layered ||
            providerSelections[0].Candidates.IsDefaultOrEmpty)
            throw new CiliumPolicyException(
                "network_provider_unavailable",
                "The admitted run selection has no layered Network Policy provider.");

        var candidates = providerSelections[0].Candidates;
        if (candidates.Any(candidate => candidate.Layer == NetworkPolicyLayer.L7))
            throw new CiliumPolicyException(
                "l7_provider_unavailable",
                "The selected Layer 7 provider is not available in this Cilium-only adapter slice.");
        var l3 = candidates.Where(candidate => candidate.Layer == NetworkPolicyLayer.L3L4).ToArray();
        if (l3.Length != 1)
            throw new CiliumPolicyException(
                "invalid_network_provider_selection",
                "The admitted run selection must contain exactly one L3/L4 Network Policy provider.");
        _ = cilium.ValidateOptions(l3[0]);
        var requiredCapabilities = CiliumEgressPolicyAdapter.GetVerifiedCapabilities(intent);
        var advertisedCapabilities = l3[0].AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal);
        if (!requiredCapabilities.IsSubsetOf(advertisedCapabilities))
            throw new CiliumPolicyException(
                "network_provider_capability_unavailable",
                "The selected Cilium provider does not advertise every capability required by the compiled egress intent.");

        var registrations = candidates.Select(candidate =>
        {
            if (candidate.Layer is null || candidate.Seam != ProviderSeam.NetworkPolicy ||
                !Version.TryParse(candidate.AdapterVersion, out var version))
                throw new CiliumPolicyException(
                    "invalid_network_provider_selection",
                    "The admitted Network Policy candidate is missing its layer or valid adapter version.");
            var descriptor = new ProviderDescriptor(
                candidate.Seam,
                candidate.ProviderId,
                version,
                candidate.OptionsSchemaVersion,
                candidate.Hosting,
                candidate.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal));
            return new ProviderRegistration(
                descriptor,
                true,
                candidate.OptionsRevision,
                candidate.OptionsSchemaVersion);
        }).ToArray();
        var layerSelections = candidates.Select(candidate =>
            new ProviderLayerSelection(candidate.Layer!.Value, candidate.ProviderId)).ToArray();
        var catalogResult = ProviderCatalog.Create(
            registrations,
            [],
            [],
            layerSelections: layerSelections);
        if (!catalogResult.IsSuccess)
            throw new CiliumPolicyException(
                "invalid_network_provider_catalog",
                $"The selected Network Policy catalog is invalid: {catalogResult.Error!.Code}.");

        var resolver = new ProviderResolver(catalogResult.Value!);
        var l3Candidate = l3[0];
        if (!Version.TryParse(l3Candidate.AdapterVersion, out var requiredVersion))
            throw new CiliumPolicyException(
                "invalid_network_provider_selection",
                "The admitted L3/L4 adapter version is invalid.");
        var resolution = resolver.ResolveNetworkPolicy(new NetworkPolicyResolutionRequest(
            requiredVersion,
            l3Candidate.OptionsSchemaVersion,
            l3Candidate.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal),
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal)));
        if (!resolution.IsSuccess)
            throw new CiliumPolicyException(
                "network_provider_resolution_failed",
                $"The admitted Network Policy provider failed capability resolution: {resolution.Error!.Code}.");
        return (resolver, resolution.Value!);
    }

    private static ProviderResult<PinnedNetworkPolicyBinding> PinNetworkPolicy(
        ProviderResolver resolver,
        NetworkPolicyResolution resolution,
        EnvironmentEgressSelector selector,
        CompiledEgressIntent intent,
        CiliumPolicyObservation observed,
        long generation)
    {
        if (!observed.ObjectVerified || observed.ResourceGeneration is not > 0)
            return ProviderResult<PinnedNetworkPolicyBinding>.Failure(
                ProviderErrorCode.InvalidNegotiation,
                "A verified Kubernetes resource generation is required before network-policy pinning.");
        var candidate = resolution.Layers.Single(layer => layer.Layer == NetworkPolicyLayer.L3L4).Candidate;
        var resourceId = $"{selector.Namespace}/{selector.PolicyName}";
        var capabilities = CiliumEgressPolicyAdapter.GetVerifiedCapabilities(intent);
        return resolver.PinNetworkPolicy(
            intent.RunId,
            resolution,
            [
                new ProviderPinInput(
                    resourceId,
                    new ResourceNegotiation(
                        new ProviderResourceRef(
                            ProviderSeam.NetworkPolicy,
                            candidate.ProviderId,
                            resourceId,
                            observed.ResourceGeneration.Value),
                        capabilities))
            ],
            generation,
            observed.AppliedIntentGeneration ?? 0);
    }

    private static EnvironmentEgressOperationResult Failure(string code, string message) =>
        new(false, false, null, null, code, message);

    private static Activity? TraceOperation(string operation, long? requestedGeneration = null)
    {
        var activity = Activity.Current;
        activity?.SetTag("agentweaver.network_policy.operation", operation);
        if (requestedGeneration is { } generation)
            TraceRequestedGeneration(activity, generation);
        return activity;
    }

    private static void TraceRequestedGeneration(Activity? activity, long generation) =>
        activity?.SetTag("agentweaver.network_policy.requested_intent_generation", generation);

    private static void TraceProviderResolution(
        Activity? activity,
        NetworkPolicyResolution resolution)
    {
        for (var index = 0; index < resolution.Layers.Length; index++)
        {
            var layer = resolution.Layers[index];
            TraceProvider(
                activity,
                layer.Layer,
                layer.Candidate.ProviderId,
                layer.Candidate.AdapterVersion);
        }
    }

    private static void TraceProvider(
        Activity? activity,
        NetworkPolicyLayer layer,
        string providerId,
        Version adapterVersion)
    {
        if (activity is null)
            return;
        var prefix = $"agentweaver.network_policy.layer.{layer}";
        activity.SetTag($"{prefix}.name", layer.ToString());
        activity.SetTag($"{prefix}.provider_id", providerId);
        activity.SetTag($"{prefix}.adapter_version", adapterVersion.ToString());
    }

    private static void TracePolicyObservation(Activity? activity, CiliumPolicyObservation observation)
    {
        activity?.SetTag("agentweaver.network_policy.object_verified", observation.ObjectVerified);
        activity?.SetTag("agentweaver.network_policy.revoked", observation.Revoked);
        if (observation.AppliedIntentGeneration is { } intentGeneration)
            activity?.SetTag("agentweaver.network_policy.applied_intent_generation", intentGeneration);
        if (observation.ResourceGeneration is { } resourceGeneration)
            activity?.SetTag("agentweaver.network_policy.resource_generation", resourceGeneration);
    }

    private static void ValidateRequest(ApplyEnvironmentEgressRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Fence);
        ArgumentNullException.ThrowIfNull(request.Fence.Owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException(
                "Network-policy idempotency keys must be bounded and contain no control characters.",
                nameof(request.IdempotencyKey));
    }

    private static string EffectIdempotencyKey(
        string requestIdempotencyKey,
        EnvironmentNetworkEffectKind kind) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{kind}\0{requestIdempotencyKey}"))).ToLowerInvariant();

    internal sealed record AuthorizedSelection(
        ProjectAuthorizationContextResponse Authorization,
        EffectiveNetworkPolicySelection RunSelection)
    {
        public string TenantId => Authorization.TenantId;
        public string ProjectId => RunSelection.ProjectId;
        public string RunId => RunSelection.RunId;
    }

    private sealed class EgressCompilationException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
