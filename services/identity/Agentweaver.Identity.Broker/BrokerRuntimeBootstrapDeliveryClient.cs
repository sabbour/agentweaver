using Agentweaver.Identity;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity.Broker;

public sealed class BrokerRuntimeBootstrapDeliveryClient(
    HttpClient client, Uri environmentAddress) : IRuntimeBootstrapDelivery
{
    private readonly Uri _environmentAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentAddress);

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
