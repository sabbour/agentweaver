using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Implements resume-specific bootstrap authorization, delivery, and host-state reconciliation.</summary>
public sealed partial class EnvironmentRuntimeBootstrapDelivery
{
    /// <summary>Requires the orchestrator to echo the same still-current resume bootstrap request.</summary>
    public async Task RequireCurrentResumeBootstrapAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_resume_actor_expired");
        var current = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeResumeBootstrapRequest>(
            client, options.OrchestratorOwnerAddress, "/internal/runtime/resume/require-bootstrap-current",
            actor, request, SessionMaterialValidation.MaximumBytes, false, cancellationToken).ConfigureAwait(false);
        if (current is null || !actor.Bearer.IsUsable() ||
            !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request),
                JsonSerializer.SerializeToElement(current)))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_authority_changed");
    }

    /// <summary>
    /// Executes or reconciles bootstrap for a restored runtime, including delivery verification and host activation.
    /// </summary>
    public async Task<RuntimeResumeBootstrapReceipt> BootstrapResumeAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        request.Validate();
        if (options.EventsOwnerAddress is null)
            throw new RuntimeAuthorizationException("runtime_resume_configuration_owner_unavailable");
        var store = resumeBootstraps
            ?? throw new RuntimeAuthorizationException("runtime_resume_bootstrap_store_unavailable");
        return await store.ExecuteAsync(request, RequireCurrentAsync,
            async (saved, token) =>
            {
                if (saved is null)
                    return await ProduceResumeBootstrapAsync(request, actor, token).ConfigureAwait(false);
                saved.ValidateFor(request);
                await RequireHostStateAsync(request, saved.Delivery, actor, token).ConfigureAwait(false);
                var live = await ActivateAsync(ResumeConfigurationRequest(request), actor, token).ConfigureAwait(false);
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(saved.HostReceipt),
                    JsonSerializer.SerializeToElement(live)))
                    throw new RuntimeAuthorizationException("runtime_resume_bootstrap_host_changed");
                return saved;
            }, cancellationToken).ConfigureAwait(false);

        async Task RequireCurrentAsync(CancellationToken token)
        {
            await RequireCurrentResumeBootstrapAsync(request, actor, token).ConfigureAwait(false);
            if (await ReadCurrentAsync(request.Registration.RuntimeInstanceId, actor, token).ConfigureAwait(false)
                != request.Registration)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_registration_changed");
            await ReadPlacementAsync(request.Registration, actor, token).ConfigureAwait(false);
        }
    }

    /// <summary>Reads protected resume configuration, delivers it to the broker, activates the host, and clears bytes.</summary>
    private async Task<RuntimeResumeBootstrapReceipt> ProduceResumeBootstrapAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        var events = options.EventsOwnerAddress
            ?? throw new RuntimeAuthorizationException("runtime_resume_configuration_owner_unavailable");
        await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
        if (await ReadCurrentAsync(request.Registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false)
            != request.Registration)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_registration_changed");
        await ReadPlacementAsync(request.Registration, actor, cancellationToken).ConfigureAwait(false);
        var configuration = await new RuntimeResumeConfigurationHttpClient(client, events)
            .ReadAsync(request, actor, cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
            var hash = configuration.Material.Reference.Material!.Sha256;
            var delivery = await RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapDeliveryReceipt>(
                client, options.BrokerOwnerAddress, "/internal/runtime/bootstrap/request", actor,
                new RuntimeBootstrapRequest(request.Registration.RuntimeInstanceId, request.BootstrapOperationId, hash),
                cancellationToken).ConfigureAwait(false);
            await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
            RequireResumeDelivery(delivery, request, hash);
            await RequireHostStateAsync(request, delivery, actor, cancellationToken).ConfigureAwait(false);
            var host = await ActivateAsync(ResumeConfigurationRequest(request), actor, cancellationToken)
                .ConfigureAwait(false);
            await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
            return new RuntimeResumeBootstrapReceipt(
                1, request.Resume.OperationId, request.Resume.SourceManifest.ManifestId,
                request.Registration, delivery, host).ValidateFor(request);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(configuration.Bytes);
        }
    }

    /// <summary>Builds the host configuration request bound to the resume registration and operation identifiers.</summary>
    private static RuntimeHostConfigureRequest ResumeConfigurationRequest(RuntimeResumeBootstrapRequest request) =>
        new(1, request.Registration, request.ConsumeOperationId, request.ExchangeOperationId,
            JsonSerializer.SerializeToElement<object?>(null))
        {
            ResumeBootstrap = request
        };

    /// <summary>Checks that the host retained the expected resume state and that registration and placement remain current.</summary>
    private async Task RequireHostStateAsync(
        RuntimeResumeBootstrapRequest request, RuntimeBootstrapDeliveryReceipt delivery,
        RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
        var placement = await ReadPlacementAsync(request.Registration, actor, cancellationToken).ConfigureAwait(false);
        var state = (await SendToHostAsync<RuntimeResumeHostStateReceipt>(
            placement, "/runtime/v1/configure/resume-state", actor, request, cancellationToken).ConfigureAwait(false))
            .ValidateFor(request);
        await RequireCurrentResumeBootstrapAsync(request, actor, cancellationToken).ConfigureAwait(false);
        if (await ReadCurrentAsync(request.Registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false)
            != request.Registration ||
            placement != await ReadPlacementAsync(request.Registration, actor, cancellationToken).ConfigureAwait(false))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_registration_changed");
        if (state.State == RuntimeResumeHostState.Missing)
            throw new RuntimeResumeBootstrapStateLostException(
                new RuntimeResumeBootstrapStateLossReceipt(delivery, state).ValidateFor(request));
    }

    /// <summary>Rejects a broker receipt that does not bind to the requested operation, hash, registration, and placement.</summary>
    private static void RequireResumeDelivery(
        RuntimeBootstrapDeliveryReceipt delivery, RuntimeResumeBootstrapRequest request, string hash)
    {
        var registration = request.Registration;
        var binding = registration.Binding;
        if (delivery.OperationId != request.BootstrapOperationId || delivery.GrantId == Guid.Empty ||
            delivery.RuntimeInstanceId != registration.RuntimeInstanceId ||
            delivery.RegistrationRevision != registration.Revision || delivery.ConfigurationHash != hash ||
            delivery.PlacementUid != binding.PlacementUid ||
            delivery.PlacementGeneration != binding.PlacementGeneration ||
            delivery.ExecutionFence != binding.ExecutionFence ||
            delivery.EnvironmentCurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            delivery.EnvironmentProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_delivery_invalid");
    }
}
