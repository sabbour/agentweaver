using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeBrokerCredentialClient(
    HttpClient client,
    Uri brokerAddress,
    string expectedIssuer,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider)
{
    private readonly Uri _brokerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(brokerAddress);

    public async Task<RuntimeGrantReceipt> ConsumeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Configure);
        var receipt = await SendReceiptAsync("/internal/runtime/bootstrap/consume",
            proof, operationId, cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Consumed, checked(proof.Revision + 1));
        return receipt;
    }

    public Task<RuntimeCredentialExchange> ExchangeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Configure);
        return ExchangeAsync("/internal/runtime/bootstrap/exchange", proof, operationId, rotate: false,
            cancellationToken);
    }

    public Task<RuntimeCredentialExchange> RotateSourceAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Observe);
        return ExchangeAsync("/internal/runtime/source/rotate", proof, operationId, rotate: true,
            cancellationToken);
    }

    public async Task<RuntimeGrantReceipt> VerifySourceAsync(
        RuntimeCredentialProof proof, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Observe);
        var receipt = await SendReceiptAsync("/internal/runtime/source/verify", proof, Guid.Empty,
            cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Active, proof.Revision);
        return receipt;
    }

    public async Task<RuntimeGrantReceipt> RevokeAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        var receipt = await SendReceiptAsync("/internal/runtime/source/revoke", proof, operationId,
            cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Revoked, checked(proof.Revision + 1));
        return receipt;
    }

    private Task<RuntimeGrantReceipt> SendReceiptAsync(
        string path, RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeGrantReceipt>(
            client, _brokerAddress, path, actor, Input(proof, operationId), cancellationToken);

    private async Task<RuntimeCredentialExchange> ExchangeAsync(
        string path, RuntimeCredentialProof proof, Guid operationId, bool rotate,
        CancellationToken cancellationToken)
    {
        var response = await RuntimeOwnerHttpTransport.SendAsync<RuntimeCredentialExchangeResponse>(
            client, _brokerAddress, path, actor, Input(proof, operationId), cancellationToken);
        var receipt = response.Receipt;
        if (receipt.RuntimeInstanceId != proof.RuntimeInstanceId ||
            receipt.Purpose != RuntimeCredentialPurpose.Observe ||
            receipt.State != RuntimeCredentialState.Active ||
            receipt.ConfigurationHash != proof.ConfigurationHash ||
            receipt.RegistrationRevision <= 0 || receipt.GrantId == Guid.Empty ||
            receipt.Revision != (rotate ? checked(proof.Revision + 1) : 1) ||
            rotate && receipt.GrantId != proof.GrantId ||
            !rotate && receipt.GrantId == proof.GrantId ||
            !RuntimeContractValidation.IsHttpsEndpoint(receipt.Audience))
            throw new RuntimeAuthorizationException("runtime_exchange_receipt_invalid");
        RequireIssuerAndLifetime(receipt);
        if (response.IsReplay)
        {
            if (response.CredentialValue is not null)
                throw new RuntimeAuthorizationException("runtime_replay_credential_invalid");
            return new RuntimeCredentialExchange(receipt, null, isReplay: true);
        }
        RuntimeContractValidation.ValidateHash(response.CredentialValue!);
        return new RuntimeCredentialExchange(receipt,
            new SecretCredential(response.CredentialValue!, receipt.ExpiresAt, timeProvider), isReplay: false);
    }

    private void RequireReceipt(
        RuntimeGrantReceipt receipt, RuntimeCredentialProof proof, RuntimeCredentialState state, long revision)
    {
        if (receipt.GrantId != proof.GrantId || receipt.RuntimeInstanceId != proof.RuntimeInstanceId ||
            receipt.Purpose != proof.Purpose || receipt.Audience != proof.Audience ||
            receipt.ConfigurationHash != proof.ConfigurationHash || receipt.State != state ||
            receipt.Revision != revision || receipt.RegistrationRevision <= 0)
            throw new RuntimeAuthorizationException("runtime_grant_receipt_invalid");
        RequireIssuerAndLifetime(receipt);
    }

    private void RequireIssuerAndLifetime(RuntimeGrantReceipt receipt)
    {
        if (!Uri.TryCreate(expectedIssuer, UriKind.Absolute, out var issuer) ||
            !RuntimeContractValidation.IsHttpsEndpoint(issuer) || receipt.Issuer != issuer.AbsoluteUri ||
            receipt.ExpiresAt <= timeProvider.GetUtcNow() || receipt.ExpiresAt > actor.Bearer.ExpiresAt ||
            receipt.RecordedAt > timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_grant_receipt_invalid");
    }

    private static RuntimeCredentialHttpRequest Input(RuntimeCredentialProof proof, Guid operationId) =>
        new(proof.GrantId, proof.RuntimeInstanceId, proof.Revision, proof.Purpose, proof.Audience,
            proof.ConfigurationHash, proof.Credential.GetValue(), proof.Credential.ExpiresAt, operationId);

    private static void RequirePurpose(RuntimeCredentialProof proof, RuntimeCredentialPurpose purpose)
    {
        if (proof.Purpose != purpose)
            throw new RuntimeAuthorizationException("runtime_purpose_invalid");
    }
}
