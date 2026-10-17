using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Owner service addresses used for runtime registration, configuration delivery, and event reads.</summary>
public sealed record EnvironmentRuntimeBootstrapDeliveryOptions(
    Uri OrchestratorOwnerAddress, Uri BrokerOwnerAddress)
{
    public Uri? EventsOwnerAddress { get; init; }
}

/// <summary>
/// Coordinates runtime host configuration and credential delivery with current registration and Sandbox placement.
/// </summary>
public sealed partial class EnvironmentRuntimeBootstrapDelivery(
    HttpClient client,
    EnvironmentRuntimeBootstrapDeliveryOptions options,
    EnvironmentRuntimePlacementReader placements,
    EnvironmentRuntimeBootstrapProfileRegistry profiles,
    TimeProvider timeProvider,
    IEnvironmentRuntimeResumeBootstrapStore? resumeBootstraps = null,
    IEnvironmentRuntimeBootstrapProfileReplacementStore? profileReplacements = null)
{
    private readonly RuntimeRegistrationHttpClient _owner = new(
        client, options.OrchestratorOwnerAddress);

    /// <summary>
    /// Activates the current runtime host and validates its readiness receipt against current registration and placement.
    /// Resume activation additionally requires the exact accepted resume bootstrap request.
    /// </summary>
    public async Task<RuntimeHostReadinessReceipt> ActivateAsync(
        RuntimeHostConfigureRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        if (request.ContractVersion != 1)
            throw new RuntimeAuthorizationException("runtime_configuration_invalid");
        if (request.ResumeBootstrap is { } resume)
        {
            resume.Validate();
            if (resume.Registration != request.Registration ||
                request.Configuration.ValueKind is not (System.Text.Json.JsonValueKind.Null or
                    System.Text.Json.JsonValueKind.Undefined))
                throw new RuntimeAuthorizationException("runtime_resume_configuration_caller_input_denied");
            await RequireCurrentResumeBootstrapAsync(resume, actor, cancellationToken).ConfigureAwait(false);
        }
        var registration = await ReadCurrentAsync(request.Registration.RuntimeInstanceId, actor, cancellationToken)
            .ConfigureAwait(false);
        if (registration != request.Registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var placement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        if (request.McpServer is { IsValid: false } ||
            request.McpServer is not null && request.McpServer != placement.McpServer)
            throw new RuntimeAuthorizationException("runtime_mcp_server_binding_invalid");
        var endpoint = placement.ConfigureEndpoint;
        if (endpoint.AbsolutePath != "/runtime/v1/configure")
            throw new RuntimeAuthorizationException("runtime_configuration_version_unavailable");
        if (request.ResumeBootstrap is { } beforeDelivery)
            await RequireCurrentResumeBootstrapAsync(beforeDelivery, actor, cancellationToken).ConfigureAwait(false);
        var receipt = await SendToHostAsync<RuntimeHostReadinessReceipt>(
            placement,
            endpoint.AbsolutePath + "/activate", actor,
            request with { McpServer = placement.McpServer }, cancellationToken).ConfigureAwait(false);
        var currentPlacement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentAsync(registration.RuntimeInstanceId, actor, cancellationToken)
            .ConfigureAwait(false);
        if (current != registration || placement != currentPlacement ||
            receipt.ContractVersion != 1 || receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExecutionFence != registration.Binding.ExecutionFence || receipt.Image != registration.Binding.Image ||
            receipt.StartupPhases.IsDefault || receipt.StartupPhases.Length != 5 ||
            receipt.StartupPhases.Select(phase => phase.Phase).Distinct().Count() != 5 ||
            receipt.SourceGrant.Purpose != RuntimeCredentialPurpose.Observe ||
            receipt.SourceGrant.State != RuntimeCredentialState.Active ||
            receipt.SourceGrant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.SourceGrant.RegistrationRevision != registration.Revision ||
            receipt.SourceGrant.ExpiresAt > registration.ExpiresAt || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_activation_receipt_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ResumeBootstrap is { } verifiedResume)
        {
            if (receipt.SourceGrant.ExpiresAt <= timeProvider.GetUtcNow())
                throw new RuntimeAuthorizationException("runtime_resume_source_grant_expired");
            (receipt.ResumeCache ?? throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_missing"))
                .ValidateFor(verifiedResume);
            await RequireCurrentResumeBootstrapAsync(verifiedResume, actor, cancellationToken).ConfigureAwait(false);
        }
        return receipt;
    }

    /// <summary>Delivers a short-lived configuration credential only while its broker grant and placement remain current.</summary>
    public async Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
        RuntimeBootstrapDeliveryRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateHash(request.ConfigurationHash);
        RuntimeContractValidation.ValidateHash(request.CredentialValue);
        if (request.OperationId == Guid.Empty || request.GrantId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_delivery_invalid");
        var registration = await ReadCurrentAsync(request.RuntimeInstanceId, actor, cancellationToken)
            .ConfigureAwait(false);
        using var lifetime = new CredentialLifetime(
            new SecretCredential(request.CredentialValue, request.CredentialExpiresAt, timeProvider));
        var proof = new RuntimeCredentialProof(
            request.GrantId, request.RuntimeInstanceId, 1, RuntimeCredentialPurpose.Configure,
            registration.Binding.ConfigureEndpoint, request.ConfigurationHash, lifetime.Credential);
        var verifier = new RuntimePendingBootstrapHttpClient(client, options.BrokerOwnerAddress, actor);
        RequireGrant(await verifier.VerifyPendingBootstrapDeliveryAsync(
            proof, request.OperationId, cancellationToken).ConfigureAwait(false), registration);
        var placement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        var before = await ReadCurrentAsync(request.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        if (before != registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        var endpoint = placement.ConfigureEndpoint;
        var receipt = await SendToHostAsync<RuntimeBootstrapDeliveryReceipt>(
            placement,
            endpoint.AbsolutePath, actor, request, cancellationToken).ConfigureAwait(false);
        var currentPlacement = await ReadPlacementAsync(registration, actor, cancellationToken).ConfigureAwait(false);
        RequireGrant(await verifier.VerifyPendingBootstrapDeliveryAsync(
            proof, request.OperationId, cancellationToken).ConfigureAwait(false), registration);
        var after = await ReadCurrentAsync(request.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        if (after != registration || currentPlacement != placement || !lifetime.Credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_delivery_stale");
        var binding = registration.Binding;
        if (receipt.OperationId != request.OperationId || receipt.GrantId != request.GrantId ||
            receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.PlacementUid != binding.PlacementUid || receipt.PlacementGeneration != binding.PlacementGeneration ||
            receipt.ExecutionFence != binding.ExecutionFence ||
            receipt.EnvironmentCurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            receipt.EnvironmentProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            receipt.ConfigurationHash != request.ConfigurationHash ||
            receipt.DeliveredAt > timeProvider.GetUtcNow() || receipt.DeliveredAt >= request.CredentialExpiresAt)
            throw new RuntimeAuthorizationException("runtime_delivery_binding_invalid");
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    /// <summary>Reads an active, unexpired registration and requires its actor credential to remain usable.</summary>
    private async Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var registration = await _owner.ReadCurrentAsync(runtimeInstanceId, actor, token).ConfigureAwait(false);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
        return registration;
    }

    /// <summary>Resolves and compares all current placement, lease, profile, image, and endpoint bindings.</summary>
    private async Task<EnvironmentRuntimeBootstrapContext> ReadPlacementAsync(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var binding = registration.Binding;
        var placement = await placements.GetBootstrapContextAsync(
            actor, binding.ProjectId, binding.RunId, binding.SessionId, binding.EnvironmentId, binding.ProfileId,
            profiles, token, profileReplacements).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
        if (placement.TenantId != binding.TenantId || placement.ProjectId != binding.ProjectId ||
            placement.RunId != binding.RunId || placement.EnvironmentId != binding.EnvironmentId ||
            placement.Resource.Seam != ProviderSeam.Sandbox ||
            placement.Resource.ProviderId != binding.PlacementProviderId ||
            placement.Resource.ResourceId != binding.PlacementUid ||
            placement.Resource.Generation != binding.PlacementGeneration ||
            placement.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
            placement.LeaseRevision != binding.EnvironmentLeaseRevision ||
            placement.CurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            placement.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            placement.LeaseExpiresAt != registration.ExpiresAt ||
            placement.ProfileId != binding.ProfileId ||
            placement.Image != binding.Image ||
            placement.ConfigureEndpoint != binding.ConfigureEndpoint ||
            placement.ObservationEndpoint != binding.ObservationEndpoint)
            throw new RuntimeAuthorizationException("runtime_placement_stale");
        return placement;
    }

    /// <summary>Requires a broker grant to remain within the registration's revision and expiry bounds.</summary>
    private static void RequireGrant(RuntimeGrantReceipt grant, RuntimeRegistration registration)
    {
        if (grant.RegistrationRevision != registration.Revision || grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_delivery_binding_invalid");
    }

    /// <summary>Invalidates the temporary runtime credential when the delivery scope exits.</summary>
    private sealed class CredentialLifetime(SecretCredential credential) : IDisposable
    {
        public SecretCredential Credential { get; } = credential;
        /// <summary>Invalidates the short-lived credential when the owning delivery scope is disposed.</summary>
        public void Dispose() => Credential.Invalidate();
    }
}
