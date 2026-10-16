using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Exchanges bootstrap and model credentials with the Identity broker, validating every
/// returned receipt against the source proof, actor lifetime, and registration.
/// </summary>
public sealed class RuntimeBrokerCredentialClient(
    HttpClient client,
    Uri brokerAddress,
    string expectedIssuer,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider)
{
    private readonly Uri _brokerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(brokerAddress);

    /// <summary>Consumes a configure grant once and requires the next consumed revision.</summary>
    public async Task<RuntimeGrantReceipt> ConsumeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Configure);
        var receipt = await SendReceiptAsync("/internal/runtime/bootstrap/consume",
            proof, operationId, cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Consumed, checked(proof.Revision + 1));
        return receipt;
    }

    /// <summary>Exchanges a consumed configure grant for a fresh observe credential.</summary>
    public Task<RuntimeCredentialExchange> ExchangeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Configure);
        return ExchangeAsync("/internal/runtime/bootstrap/exchange", proof, operationId, rotate: false,
            cancellationToken);
    }

    /// <summary>Rotates a source grant; a replay returns only the receipt, never the old credential.</summary>
    public Task<RuntimeCredentialExchange> RotateSourceAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Observe);
        return ExchangeAsync("/internal/runtime/source/rotate", proof, operationId, rotate: true,
            cancellationToken);
    }

    /// <summary>Requires the current observe grant to remain active at its exact revision.</summary>
    public async Task<RuntimeGrantReceipt> VerifySourceAsync(
        RuntimeCredentialProof proof, CancellationToken cancellationToken)
    {
        RequirePurpose(proof, RuntimeCredentialPurpose.Observe);
        var receipt = await SendReceiptAsync("/internal/runtime/source/verify", proof, Guid.Empty,
            cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Active, proof.Revision);
        return receipt;
    }

    /// <summary>Revokes the source grant and requires the next revision in the broker receipt.</summary>
    public async Task<RuntimeGrantReceipt> RevokeAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken)
    {
        var receipt = await SendReceiptAsync("/internal/runtime/source/revoke", proof, operationId,
            cancellationToken);
        RequireReceipt(receipt, proof, RuntimeCredentialState.Revoked, checked(proof.Revision + 1));
        return receipt;
    }

    /// <summary>
    /// Obtains a grant for the accepted model credential, redeems it, caps its lifetime by
    /// grant and actor expiry, and verifies the grant before returning the in-memory secret.
    /// </summary>
    public async Task<RuntimeModelCredential> RedeemModelCredentialAsync(
        RuntimeRegistration registration, Guid operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var binding = registration.Binding;
        var credentialReference = binding.ModelCredentialReference;
        if (operationId == Guid.Empty ||
            binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
                (binding.ModelConnectionId is null || credentialReference is not null) ||
            binding.ModelSourceMode == ModelSourceMode.Byok &&
                (credentialReference is null || binding.ModelConnectionId is not null) ||
            string.IsNullOrWhiteSpace(binding.ModelSelectionReference) || binding.ModelSourceMode is null ||
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
            grant.SourceMode != binding.ModelSourceMode ||
            grant.CredentialReference is null ||
            binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
                (grant.ConnectionId != binding.ModelConnectionId || grant.ConnectionRevision is not > 0 ||
                    grant.CredentialKind != RuntimeModelCredentialKind.GitHubUserAccess) ||
            binding.ModelSourceMode == ModelSourceMode.Byok &&
                (grant.CredentialReference != credentialReference ||
                    grant.CredentialKind != RuntimeModelCredentialKind.ByokKey ||
                    grant.ConnectionId is not null || grant.ConnectionRevision is not null) ||
            grant.Purpose != RuntimeSecretPurposes.ModelSession ||
            grant.ExpiresAt <= timeProvider.GetUtcNow() || grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_model_grant_receipt_invalid");

        var redeemed = await RuntimeOwnerHttpTransport.SendAsync<RuntimeModelCredentialResponse>(
            client, _brokerAddress, "/internal/runtime/model-session/redeem", actor,
            new RuntimeModelCredentialProof(grant),
            cancellationToken);
        if (redeemed.Receipt != grant ||
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
            var result = new RuntimeModelCredential(grant, credential);
            await VerifyModelCredentialAsync(result, cancellationToken);
            return result;
        }
        catch
        {
            credential?.Invalidate();
            throw;
        }
    }

    /// <summary>Rechecks the broker grant and credential lifetime before a model call.</summary>
    public async Task VerifyModelCredentialAsync(
        RuntimeModelCredential model, CancellationToken cancellationToken)
    {
        if (!model.Credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_model_credential_unavailable");
        var verified = await RuntimeOwnerHttpTransport.SendAsync<RuntimeModelCredentialGrantReceipt>(
            client, _brokerAddress, "/internal/runtime/model-session/verify", actor,
            new RuntimeModelCredentialProof(model.Receipt), cancellationToken);
        if (verified != model.Receipt || !model.Credential.IsUsable() || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_model_grant_stale");
    }

    private Task<RuntimeGrantReceipt> SendReceiptAsync(
        string path, RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeGrantReceipt>(
            client, _brokerAddress, path, actor, Input(proof, operationId), cancellationToken);

    /// <summary>Performs an exchange or rotation and validates the replay/no-credential contract.</summary>
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

    /// <summary>Requires the configured HTTPS issuer and caps grant lifetime at the actor bearer expiry.</summary>
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
