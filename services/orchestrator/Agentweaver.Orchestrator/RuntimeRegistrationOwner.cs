using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

public sealed record RuntimeRegistrationOwnerOptions(Uri EnvironmentOwnerAddress);

public sealed record RegisterRuntimeRequest(string EnvironmentId, string ProfileId);

internal sealed class RuntimeEnvironmentContextClient(
    HttpClient client, RuntimeRegistrationOwnerOptions options)
{
    public Task<EnvironmentRuntimeBootstrapContext> ReadAsync(
        RuntimeActorAuthorization actor, string projectId, string runId,
        string environmentId, string profileId, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<EnvironmentRuntimeBootstrapContext>(
            client, options.EnvironmentOwnerAddress,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/environments/{Uri.EscapeDataString(environmentId)}/runtime-bootstrap/profiles/{Uri.EscapeDataString(profileId)}",
            actor, null, cancellationToken);
}

internal sealed class RuntimeRegistrationOwner(
    OrchestratorOptions options,
    ProjectsRunSelectionClient projects,
    CoordinationOwnerStore sessions,
    CoordinatorDecisionOwnerStore decisions,
    CoordinatorRunSelectionContextStore selectionContexts,
    RuntimeRegistrationStore registrations,
    RuntimeEnvironmentContextClient environment,
    TimeProvider timeProvider)
{
    public async Task<RuntimeRegistration> RegisterAsync(
        HttpContext context, string projectId, string runId, string sessionId,
        RegisterRuntimeRequest request, CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(request.EnvironmentId);
        RuntimeContractValidation.ValidateIdentifier(request.ProfileId);
        var derived = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, request, cancellationToken).ConfigureAwait(false);
        var registration = await registrations.RegisterAsync(
            derived.Binding, derived.ExpiresAt, cancellationToken,
            async token =>
            {
                var current = await DeriveCurrentBindingAsync(
                    context, projectId, runId, sessionId, request, token).ConfigureAwait(false);
                RequireSame(derived, current);
            }).ConfigureAwait(false);
        var final = await DeriveCurrentBindingAsync(
            context, projectId, runId, sessionId, request, cancellationToken).ConfigureAwait(false);
        RequireSame(derived, final);
        return registration;
    }

    public async Task<RuntimeRegistration> ReadCurrentAsync(
        HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken)
    {
        var registration = await registrations.ReadAsync(runtimeInstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_registration_unknown");
        RequireActive(registration);
        var binding = registration.Binding;
        var request = new RegisterRuntimeRequest(binding.EnvironmentId, binding.ProfileId);
        var derived = await DeriveCurrentBindingAsync(
            context, binding.ProjectId, binding.RunId, binding.SessionId, request, cancellationToken)
            .ConfigureAwait(false);
        RequireRegistrationMatches(registration, derived);
        var currentRegistration = await registrations.ReadAsync(runtimeInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (currentRegistration != registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var final = await DeriveCurrentBindingAsync(
            context, binding.ProjectId, binding.RunId, binding.SessionId, request, cancellationToken)
            .ConfigureAwait(false);
        RequireRegistrationMatches(registration, final);
        return registration;
    }

    private async Task<DerivedBinding> DeriveCurrentBindingAsync(
        HttpContext context, string projectId, string runId, string sessionId,
        RegisterRuntimeRequest request, CancellationToken cancellationToken)
    {
        var firstOwner = await ReadOwnerAsync(context, projectId, runId, sessionId, cancellationToken)
            .ConfigureAwait(false);
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
            var firstPlacement = await environment.ReadAsync(
                actor, projectId, runId, request.EnvironmentId, request.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            var placement = await environment.ReadAsync(
                actor, projectId, runId, request.EnvironmentId, request.ProfileId, cancellationToken)
                .ConfigureAwait(false);
            if (placement != firstPlacement)
                throw new RuntimeAuthorizationException("runtime_placement_stale");
            var owner = await ReadOwnerAsync(context, projectId, runId, sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (owner != firstOwner || !credential.IsUsable())
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
                ModelSelectionReference = owner.ModelSelectionReference,
                PlacementProviderId = placement.Resource.ProviderId,
                EnvironmentLifecycleGeneration = placement.LifecycleGeneration,
                EnvironmentLeaseRevision = placement.LeaseRevision,
                EnvironmentCurrentFencingGeneration = placement.CurrentFencingGeneration,
                EnvironmentProviderFencingGeneration = placement.ProviderFencingGeneration
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

    private Task<RuntimeOwnerContext> ReadOwnerAsync(
        HttpContext context, string projectId, string runId, string sessionId, CancellationToken cancellationToken) =>
        CoordinationEndpoints.ReadRuntimeOwnerContextCoreAsync(
            projectId, runId, sessionId, context, options, projects, sessions, decisions,
            selectionContexts, cancellationToken);

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

    private static void RequireSame(DerivedBinding expected, DerivedBinding current)
    {
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    private void RequireRegistrationMatches(RuntimeRegistration registration, DerivedBinding derived)
    {
        RequireActive(registration);
        if (registration.Binding != derived.Binding || registration.ExpiresAt != derived.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    private void RequireActive(RuntimeRegistration registration)
    {
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
    }

    private sealed record DerivedBinding(RuntimeBinding Binding, DateTimeOffset ExpiresAt);
}
