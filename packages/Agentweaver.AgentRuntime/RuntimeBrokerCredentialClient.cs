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

    public async Task<SecretCredential> RedeemModelCredentialAsync(
        RuntimeRegistration registration, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var binding = registration.Binding;
        var credentialReference = binding.ModelCredentialReference;
        if (operationId == Guid.Empty || credentialReference is null ||
            string.IsNullOrWhiteSpace(binding.ModelSelectionReference) ||
            registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_model_credential_unavailable");

        var grant = await RuntimeOwnerHttpTransport.SendAsync<RuntimeModelCredentialGrantReceipt>(
            client, _brokerAddress, "/internal/runtime/model-session/grant", actor,
            new RuntimeModelCredentialGrantRequest(registration.RuntimeInstanceId, operationId),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(grant.GrantId) || grant.Revision <= 0 ||
            grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision ||
            grant.ModelSelectionReference != binding.ModelSelectionReference ||
            grant.CredentialReference != credentialReference ||
            grant.Purpose != RuntimeSecretPurposes.ModelSession ||
            grant.ExpiresAt <= timeProvider.GetUtcNow() || grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_model_grant_receipt_invalid");

        var redeemed = await RuntimeOwnerHttpTransport.SendAsync<ModelCredentialRedemptionResponse>(
            client, _brokerAddress, "/secrets/redeem", actor,
            new ModelCredentialRedemptionRequest(
                credentialReference.Id, credentialReference.Version,
                RuntimeSecretPurposes.ModelSession, binding.RunId),
            cancellationToken);
        if (redeemed.SecretId != credentialReference.Id ||
            redeemed.SecretVersion != credentialReference.Version ||
            string.IsNullOrEmpty(redeemed.Value) ||
            redeemed.ExpiresAt <= timeProvider.GetUtcNow() ||
            redeemed.ExpiresAt > grant.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_model_credential_response_invalid");

        SecretCredential? credential = null;
        try
        {
            credential = new SecretCredential(redeemed.Value, redeemed.ExpiresAt, timeProvider);
            var lifetimeLimit = grant.ExpiresAt < actor.Bearer.ExpiresAt
                ? grant.ExpiresAt
                : actor.Bearer.ExpiresAt;
            credential.LimitLifetime(lifetimeLimit);
            return credential;
        }
        catch
        {
            credential?.Invalidate();
            throw;
        }
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

    private sealed record ModelCredentialRedemptionRequest(
        string SecretId, string SecretVersion, string Purpose, string RunId);

    private sealed record ModelCredentialRedemptionResponse(
        string SecretId, string SecretVersion, DateTimeOffset ExpiresAt, string Value)
    {
        public override string ToString() => nameof(ModelCredentialRedemptionResponse) + " [REDACTED]";
    }
}
