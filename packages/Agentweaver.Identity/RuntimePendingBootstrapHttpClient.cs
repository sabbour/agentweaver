namespace Agentweaver.Identity;

public sealed class RuntimePendingBootstrapHttpClient(
    HttpClient client,
    Uri brokerAddress,
    RuntimeActorAuthorization actor) : IRuntimePendingBootstrapVerifier
{
    private readonly Uri _brokerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(brokerAddress);

    public async Task<RuntimeGrantReceipt> VerifyPendingBootstrapDeliveryAsync(
        RuntimeCredentialProof proof, Guid deliveryOperationId, CancellationToken cancellationToken)
    {
        if (proof.Purpose != RuntimeCredentialPurpose.Configure || deliveryOperationId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_pending_delivery_invalid");
        var input = new PendingBootstrapVerificationRequest(
            proof.GrantId, proof.RuntimeInstanceId, proof.Revision, proof.Audience,
            proof.ConfigurationHash, proof.Credential.GetValue(), proof.Credential.ExpiresAt,
            deliveryOperationId);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeGrantReceipt>(
            client, _brokerAddress, "/internal/runtime/bootstrap/verify-pending", actor, input,
            cancellationToken).ConfigureAwait(false);
        if (receipt.GrantId != proof.GrantId || receipt.RuntimeInstanceId != proof.RuntimeInstanceId ||
            receipt.Revision != proof.Revision || receipt.Purpose != RuntimeCredentialPurpose.Configure ||
            receipt.State != RuntimeCredentialState.Active || receipt.Audience != proof.Audience ||
            receipt.ConfigurationHash != proof.ConfigurationHash ||
            receipt.ExpiresAt != proof.Credential.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_pending_delivery_invalid");
        return receipt;
    }
}
