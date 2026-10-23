using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Configures the owner used to resolve environment bootstrap profiles.
/// </summary>
public sealed record RuntimeRegistrationOwnerOptions(Uri EnvironmentOwnerAddress);

/// <summary>
/// Identifies the requested environment and profile for a runtime registration.
/// </summary>
public sealed record RegisterRuntimeRequest(string EnvironmentId, string ProfileId);

/// <summary>
/// Reads profile resolution receipts and runtime bootstrap context from the environment owner.
/// </summary>
internal sealed class RuntimeEnvironmentContextClient(
    HttpClient client, RuntimeRegistrationOwnerOptions options)
{
    /// <summary>
    /// Resolves a source profile to the canonical profile ID for this runtime placement.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the environment owner response is unavailable or invalid.</exception>
    public async Task<string> ResolveProfileIdAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        string runId,
        string sessionId,
        string environmentId,
        string sourceProfileId,
        CancellationToken cancellationToken)
    {
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapProfileResolveReceipt>(
            client, options.EnvironmentOwnerAddress,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/environments/{Uri.EscapeDataString(environmentId)}/coordination/sessions/{Uri.EscapeDataString(sessionId)}" +
            $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(sourceProfileId)}/resolve",
            actor, new RuntimeBootstrapProfileResolveRequest(1), cancellationToken).ConfigureAwait(false);
        return receipt.ValidateFor(sourceProfileId).ProfileId;
    }

    /// <summary>
    /// Reads the bootstrap placement and owner context for a selected environment profile.
    /// </summary>
    public Task<EnvironmentRuntimeBootstrapContext> ReadAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId,
        string environmentId, string profileId, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<EnvironmentRuntimeBootstrapContext>(
            client, options.EnvironmentOwnerAddress,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/environments/{Uri.EscapeDataString(environmentId)}/coordination/sessions/{Uri.EscapeDataString(sessionId)}" +
            $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(profileId)}",
            actor, null, cancellationToken);
}

/// <summary>
/// Creates and validates runtime registrations from current orchestrator, Projects, and environment-owner state.
/// Registration writes are bounded by repeated authority derivation to close changes across the owner calls.
/// </summary>
internal sealed class RuntimeRegistrationOwner(
    OrchestratorOptions options,
    ProjectsRunSelectionClient projects,
    CoordinationOwnerStore sessions,
    CoordinatorDecisionOwnerStore decisions,
    CoordinatorRunSelectionContextStore selectionContexts,
    PostgresMafCheckpointStore checkpoints,
    RuntimeRegistrationStore registrations,
    RuntimeEnvironmentContextClient environment,
    TimeProvider timeProvider)
{
    private const int MaximumRegistrationCandidatesPerSession = 64;

    /// <summary>
    /// Resolves the profile, derives a current server-side binding, and registers it after authority revalidation.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when actor, selection, placement, registration, or expiry checks fail.</exception>
    public async Task<RuntimeRegistration> RegisterAsync(
        HttpContext context, string projectId, string runId, string sessionId,
        RegisterRuntimeRequest request, CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(request.EnvironmentId);
        RuntimeContractValidation.ValidateIdentifier(request.ProfileId);
        var resolvedRequest = request with
        {
            ProfileId = await ResolveProfileIdAsync(
                context, projectId, runId, sessionId, request, cancellationToken).ConfigureAwait(false)
        };
        var derived = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, resolvedRequest, cancellationToken).ConfigureAwait(false);
        var registration = await registrations.RegisterAsync(
            derived.Binding, derived.ExpiresAt, cancellationToken,
            async token =>
            {
                var current = await DeriveCurrentBindingAsync(
                    context, projectId, runId, sessionId, resolvedRequest, token).ConfigureAwait(false);
                RequireSame(derived, current);
            }).ConfigureAwait(false);
        var final = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, resolvedRequest, cancellationToken).ConfigureAwait(false);
        RequireSame(derived, final);
        return registration;
    }

    /// <summary>
    /// Revokes a registration after the environment placement has been released.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the expected registration is invalid or stale.</exception>
    internal async Task<RuntimeRegistration> RevokeAfterPlacementReleaseAsync(
        RuntimeRegistration expected,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.Validate(expected);
        if (expected.State != RuntimeRegistrationState.Active ||
            expected.Revision == long.MaxValue)
            throw new RuntimeAuthorizationException("runtime_registration_stale");

        return await registrations.RevokeAsync(
            expected.RuntimeInstanceId, expected.Revision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a released registration after resume, requiring the execution fence to advance exactly once.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the released registration or replacement binding is stale.</exception>
    internal async Task<RuntimeRegistration> ReplaceAfterResumeFenceAsync(
        HttpContext context,
        RuntimeRegistration releasedRegistration,
        Guid operationId,
        string projectId,
        string runId,
        string sessionId,
        RegisterRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.Validate(releasedRegistration);
        if (operationId == Guid.Empty ||
            releasedRegistration.State != RuntimeRegistrationState.Active ||
            releasedRegistration.Revision == long.MaxValue ||
            releasedRegistration.Binding.ExecutionFence == long.MaxValue ||
            releasedRegistration.Binding.ProjectId != projectId ||
            releasedRegistration.Binding.RunId != runId ||
            releasedRegistration.Binding.SessionId != sessionId)
            throw new RuntimeAuthorizationException("runtime_registration_stale");

        var expectedExecutionFence = checked(releasedRegistration.Binding.ExecutionFence + 1);
        var resolvedRequest = request with
        {
            ProfileId = await ResolveProfileIdAsync(
                context, projectId, runId, sessionId, request, cancellationToken).ConfigureAwait(false)
        };
        var derived = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, resolvedRequest, cancellationToken).ConfigureAwait(false);
        RequireNextFenceReplacement(releasedRegistration, derived.Binding, expectedExecutionFence);
        var replacement = await registrations.ReplaceAsync(
            releasedRegistration, operationId, derived.Binding, derived.ExpiresAt, cancellationToken,
            async token =>
            {
                var current = await DeriveCurrentBindingAsync(
                    context, projectId, runId, sessionId, resolvedRequest, token).ConfigureAwait(false);
                RequireSame(derived, current);
            }).ConfigureAwait(false);
        var final = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, resolvedRequest, cancellationToken).ConfigureAwait(false);
        RequireSame(derived, final);
        return replacement;
    }

    /// <summary>
    /// Reads the current registration after rederiving and validating its owner binding.
    /// </summary>
    public async Task<RuntimeRegistration> ReadCurrentAsync(
        HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken) =>
        await ExecuteCurrentAsync(context, runtimeInstanceId,
            (current, _) => Task.FromResult(current), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Finds the single current registration for a session from bounded candidate pages.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when no unique current registration can be identified or owner state changes.</exception>
    public async Task<RuntimeRegistration> ReadCurrentForSessionAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var authorizedOwner = await ReadOwnerAsync(
            context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false);
        var candidateIds = new List<Guid>();
        Guid? afterRuntimeInstanceId = null;
        while (true)
        {
            var page = await registrations.ReadCandidateRuntimeIdsPageAsync(
                authorizedOwner.ProjectId,
                authorizedOwner.RunId,
                authorizedOwner.SessionId,
                afterRuntimeInstanceId,
                RuntimeRegistrationStore.MaximumCandidateIdPageSize,
                cancellationToken).ConfigureAwait(false);
            if (candidateIds.Count + page.Count > MaximumRegistrationCandidatesPerSession)
                throw new CoordinationException(
                    "runtime_registration_candidate_limit_exceeded", StatusCodes.Status409Conflict);
            candidateIds.AddRange(page);
            if (page.Count < RuntimeRegistrationStore.MaximumCandidateIdPageSize)
                break;
            afterRuntimeInstanceId = page[^1];
        }

        var currentMatches = new List<RuntimeRegistration>();
        foreach (var runtimeInstanceId in candidateIds)
        {
            try
            {
                currentMatches.Add(await ExecuteCurrentAsync(
                    context, runtimeInstanceId, (current, _) => Task.FromResult(current), cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (RuntimeAuthorizationException exception) when (IsStaleCandidate(exception))
            {
            }
        }

        if (await ReadOwnerAsync(context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false)
            != authorizedOwner)
            throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
        if (currentMatches.Count == 0)
            throw new CoordinationException(
                "runtime_registration_unavailable", StatusCodes.Status409Conflict);
        if (currentMatches.Count > 1)
            throw new CoordinationException(
                "runtime_registration_ambiguous", StatusCodes.Status409Conflict);

        try
        {
            var selected = await ExecuteCurrentAsync(
                context,
                currentMatches[0].RuntimeInstanceId,
                (current, _) => Task.FromResult(current),
                cancellationToken).ConfigureAwait(false);
            if (await ReadOwnerAsync(context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false)
                != authorizedOwner)
                throw new CoordinationException("runtime_owner_context_stale", StatusCodes.Status409Conflict);
            return selected;
        }
        catch (RuntimeAuthorizationException exception) when (IsStaleCandidate(exception))
        {
            throw new CoordinationException(
                "runtime_registration_unavailable", StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Reads a registration under its current database lock and executes an action only after binding revalidation.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the registration is unknown, inactive, expired, or stale.</exception>
    internal async Task<T> ExecuteCurrentAsync<T>(
        HttpContext context, Guid runtimeInstanceId,
        Func<RuntimeRegistration, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        T result = default!;
        var registration = await registrations.ReadAsync(
            runtimeInstanceId, cancellationToken, async (current, token) =>
            {
                RequireActive(current);
                var binding = current.Binding;
                var request = new RegisterRuntimeRequest(binding.EnvironmentId, binding.ProfileId);
                var derived = await DeriveCurrentBindingAsync(
                    context, binding.ProjectId, binding.RunId, binding.SessionId, request, token,
                    requireInitialOwnerSnapshot: false).ConfigureAwait(false);
                RequireRegistrationMatches(current, derived);
                result = await action(current, token).ConfigureAwait(false);
            }).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_registration_unknown");
        RequireActive(registration);
        return result;
    }

    /// <summary>
    /// Derives the runtime binding from authenticated caller, owner context, current placement, and accepted selection.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when caller credentials or owner/placement evidence is invalid.</exception>
    private async Task<DerivedBinding> DeriveCurrentBindingAsync(
        HttpContext context, string projectId, string runId, string sessionId,
        RegisterRuntimeRequest request, CancellationToken cancellationToken,
        bool requireInitialOwnerSnapshot = true)
    {
        var firstOwner = requireInitialOwnerSnapshot
            ? await ReadOwnerAsync(context, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false)
            : null;
        var authenticated = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiresAt = context.User.GetExpirationDate() ?? authenticated.Properties?.ExpiresUtc;
        if (!authenticated.Succeeded || expiresAt is null || expiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        var credential = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiresAt.Value, timeProvider);
        try
        {
            var actor = new RuntimeActorAuthorization(
                credential, CoordinationIdentity.ReadTenantSelector(context));
            var placement = await environment.ReadAsync(
                actor, projectId, runId, sessionId, request.EnvironmentId, request.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var owner = placement.RuntimeOwnerContext
                ?? throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
            var actorIdentity = CoordinationIdentity.RequireActor(context.User, options.Issuer);
            if (owner.ActorIssuer != actorIdentity.Issuer || owner.ActorId != actorIdentity.Subject ||
                owner.ProjectId != projectId || owner.RunId != runId || owner.SessionId != sessionId)
                throw new RuntimeAuthorizationException("runtime_owner_context_stale");
            if ((firstOwner is not null && owner != firstOwner) || !credential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_owner_context_stale");
            ValidatePlacement(owner, request, placement);
            var binding = new RuntimeBinding(
                owner.ActorIssuer, owner.ActorId, owner.TenantId, projectId, runId, sessionId,
                owner.AgentId, owner.TurnId, owner.ProjectRevision, owner.ProjectConfigurationRevision,
                owner.PlatformRuntimeRevision, owner.ContextRevision, owner.AcceptedSelectionHash,
                owner.ExecutionFence, placement.EnvironmentId, placement.Resource.ResourceId,
                placement.Resource.Generation, placement.ProfileId,
                placement.ConfigureEndpoint, placement.ObservationEndpoint)
            {
                WorkflowStepId = owner.WorkflowStepId,
                ModelSelectionReference = owner.ModelSelectionReference,
                ModelCredentialReference = owner.ModelCredentialReference,
                ModelSourceMode = owner.ModelSourceMode,
                ReasoningEffort = owner.ReasoningEffort,
                ModelBindingPin = owner.ModelBindingPin,
                ModelConnectionId = owner.ModelConnectionId,
                ModelConnectionScope = owner.ModelConnectionScope,
                MaxModelTurns = owner.MaxModelTurns,
                MaxToolCalls = owner.MaxToolCalls,
                MaxPromptTokens = owner.MaxPromptTokens,
                MaxRevisionAttempts = owner.MaxRevisionAttempts,
                CopilotSoftCreditLimit = owner.CopilotSoftCreditLimit,
                CopilotHardCreditLimit = owner.CopilotHardCreditLimit,
                PlacementProviderId = placement.Resource.ProviderId,
                EnvironmentLifecycleGeneration = placement.LifecycleGeneration,
                EnvironmentLeaseRevision = placement.LeaseRevision,
                EnvironmentCurrentFencingGeneration = placement.CurrentFencingGeneration,
                EnvironmentProviderFencingGeneration = placement.ProviderFencingGeneration,
                Image = placement.Image
            };
            var expiry = new DateTimeOffset(
                placement.LeaseExpiresAt.UtcTicks - placement.LeaseExpiresAt.UtcTicks % 10, TimeSpan.Zero);
            return new(binding, expiry);
        }

        finally
        {
            credential.Invalidate();
        }
    }

    /// <summary>
    /// Resolves the environment profile using a short-lived credential derived from the authenticated request.
    /// </summary>
    private async Task<string> ResolveProfileIdAsync(
        HttpContext context,
        string projectId,
        string runId,
        string sessionId,
        RegisterRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var authenticated = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiresAt = context.User.GetExpirationDate() ?? authenticated.Properties?.ExpiresUtc;
        if (!authenticated.Succeeded || expiresAt is null || expiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        var credential = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiresAt.Value, timeProvider);
        try
        {
            var actor = new RuntimeActorAuthorization(
                credential, CoordinationIdentity.ReadTenantSelector(context));
            return await environment.ResolveProfileIdAsync(
                actor, projectId, runId, sessionId, request.EnvironmentId, request.ProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    /// <summary>
    /// Reads the current orchestrator owner context for a session.
    /// </summary>
    private Task<RuntimeOwnerContext> ReadOwnerAsync(
        HttpContext context, string projectId, string runId, string sessionId, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
            projectId, runId, sessionId, context, options, projects, sessions, decisions,
            selectionContexts, checkpoints, cancellationToken, allowCoordinatorRoot: true);

    /// <summary>
    /// Validates the environment owner's bootstrap response and placement against the requested owner context.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when placement identity, lease, fencing, resource, or endpoint evidence is invalid.</exception>
    private void ValidatePlacement(
        RuntimeOwnerContext owner, RegisterRuntimeRequest request, EnvironmentRuntimeBootstrapContext placement)
    {
        if (placement.ContractVersion != 1 || placement.TenantId != owner.TenantId ||
            placement.ProjectId != owner.ProjectId || placement.RunId != owner.RunId ||
            placement.EnvironmentId != request.EnvironmentId || placement.ProfileId != request.ProfileId ||
            placement.LifecycleGeneration <= 0 || placement.LeaseRevision <= 0 ||
            placement.CurrentFencingGeneration <= 0 ||
            placement.CurrentFencingGeneration != placement.ProviderFencingGeneration ||
            placement.LeaseExpiresAt <= timeProvider.GetUtcNow() ||
            placement.Resource is not { Seam: ProviderSeam.Sandbox, Generation: > 0 } ||
            !RuntimeContractValidation.IsHttpsEndpoint(placement.ConfigureEndpoint) ||
            !RuntimeContractValidation.IsHttpsEndpoint(placement.ObservationEndpoint))
            throw new RuntimeAuthorizationException("runtime_placement_invalid");
        RuntimeContractValidation.ValidateIdentifier(placement.Resource.ProviderId);
        RuntimeContractValidation.ValidateIdentifier(placement.Resource.ResourceId);
        if (placement.Endpoint is null || placement.Placement is null)
            throw new RuntimeAuthorizationException("runtime_placement_invalid");
        placement.Endpoint.Validate();
        placement.Placement.Validate();
    }

    /// <summary>
    /// Rejects authority drift between the initially derived binding and its later revalidation.
    /// </summary>
    private static void RequireSame(DerivedBinding expected, DerivedBinding current)
    {
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>
    /// Requires a replacement registration to preserve identity and selection while advancing the execution fence once.
    /// </summary>
    private static void RequireNextFenceReplacement(
        RuntimeRegistration released,
        RuntimeBinding replacement,
        long expectedExecutionFence)
    {
        var source = released.Binding;
        if (replacement.ExecutionFence != expectedExecutionFence ||
            replacement.ActorIssuer != source.ActorIssuer ||
            replacement.ActorId != source.ActorId ||
            replacement.TenantId != source.TenantId ||
            replacement.ProjectId != source.ProjectId ||
            replacement.RunId != source.RunId ||
            replacement.SessionId != source.SessionId ||
            replacement.AcceptedSelectionHash != source.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>
    /// Requires a stored registration to match its newly derived binding and expiry.
    /// </summary>
    private void RequireRegistrationMatches(RuntimeRegistration registration, DerivedBinding derived)
    {
        RequireActive(registration);
        if (registration.Binding != derived.Binding || registration.ExpiresAt != derived.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>
    /// Validates a registration and requires it to remain active and unexpired.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the registration is invalid, inactive, or expired.</exception>
    private void RequireActive(RuntimeRegistration registration)
    {
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
    }

    /// <summary>
    /// Identifies stale or expired registration candidates that can be skipped during bounded lookup.
    /// </summary>
    private static bool IsStaleCandidate(RuntimeAuthorizationException exception) =>
        exception.Code is "runtime_registration_stale" or "runtime_registration_unavailable";

    /// <summary>
    /// Holds a server-derived runtime binding and its placement-bound expiration time.
    /// </summary>
    private sealed record DerivedBinding(RuntimeBinding Binding, DateTimeOffset ExpiresAt);
}
