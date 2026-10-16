using Agentweaver.Identity;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Adapts the broker's runtime bootstrap-delivery contract to an authenticated HTTP call
/// to Environment. The raw credential is sent only for that delivery and is not persisted
/// by this client.
/// </summary>
public sealed class BrokerRuntimeBootstrapDeliveryClient(
    HttpClient client, Uri environmentAddress) : IRuntimeBootstrapDelivery
{
    private readonly Uri _environmentAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentAddress);

    /// <summary>Delivers a bootstrap verifier and all registration/fencing bindings to Environment.</summary>
    public Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, Guid operationId,
        Guid grantId, string configurationHash, SecretCredential credential,
        CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeBootstrapDeliveryReceipt>(
            client, _environmentAddress, "/internal/runtime/bootstrap/deliver", actor,
            new RuntimeBootstrapDeliveryRequest(
                registration.RuntimeInstanceId, operationId, grantId, configurationHash,
                credential.GetValue(), credential.ExpiresAt), cancellationToken);
}
