using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

internal sealed record RuntimeUsageSourceOptions(Uri BrokerOwnerAddress);

internal sealed class RuntimeUsageBrokerClient(HttpClient client, RuntimeUsageSourceOptions options)
{
    internal Task<RuntimeGrantReceipt> VerifyAsync(
        RuntimeActorAuthorization actor, RuntimeCredentialHttpRequest proof, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeGrantReceipt>(
            client, options.BrokerOwnerAddress, "/internal/runtime/source/verify", actor, proof, cancellationToken);
}

internal sealed class RuntimeUsageSourceOwner(
    RuntimeUsageSourceStore store, RuntimeRegistrationOwner registrations,
    RuntimeUsageBrokerClient broker, ProjectsRunSelectionClient projects,
    OrchestratorOptions options, TimeProvider timeProvider)
{
    internal Task<RuntimeSdkSourceReceipt> RegisterAsync(
        HttpContext context, Guid runtimeInstanceId, RuntimeSdkSourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Source);
        if (request.Authorization.RuntimeInstanceId != runtimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_usage_binding_invalid");
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, token) =>
                store.RegisterWithinTransactionAsync(
                    connection, transaction, registration, grant, request.Source, token), cancellationToken);
    }

    internal Task<RuntimeUsageSourceReceipt> AppendAsync(
        HttpContext context, RuntimeUsageObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Observation);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, token) =>
                store.AppendWithinTransactionAsync(
                    connection, transaction, registration, grant, request.Observation, token), cancellationToken);
    }

    internal async Task<RuntimeSdkSourceReceipt> ReadCurrentSourceAsync(
        HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken)
    {
        var receipt = await store.ExecuteLockedAsync(runtimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, runtimeInstanceId, (registration, currentToken) =>
                    store.ReadCurrentSourceAsync(connection, transaction, registration, currentToken),
                token), cancellationToken).ConfigureAwait(false);
        var current = await registrations.ReadCurrentAsync(context, runtimeInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(current, receipt.Source))
            throw new RuntimeAuthorizationException("runtime_sdk_source_conflict");
        return receipt;
    }

    private async Task<T> ExecuteWriteAsync<T>(
        HttpContext context, RuntimeCredentialHttpRequest proof,
        Func<NpgsqlConnection, NpgsqlTransaction, RuntimeRegistration, RuntimeGrantReceipt,
            CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (proof.GrantId == Guid.Empty || proof.Revision < 1 ||
            proof.Purpose != RuntimeCredentialPurpose.Observe || proof.OperationId != Guid.Empty ||
            !RuntimeContractValidation.IsHttpsEndpoint(proof.Audience) ||
            proof.Audience.AbsolutePath != "/internal/runtime/observations")
            throw new RuntimeAuthorizationException("runtime_source_proof_invalid");
        RuntimeContractValidation.ValidateHash(proof.CredentialValue);
        RuntimeContractValidation.ValidateHash(proof.ConfigurationHash);
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiresAt = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiresAt is null || expiresAt <= timeProvider.GetUtcNow() ||
            proof.CredentialExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        var bearer = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiresAt.Value, timeProvider);
        var credential = new SecretCredential(proof.CredentialValue, proof.CredentialExpiresAt, timeProvider);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, CoordinationIdentity.ReadTenantSelector(context));
            return await store.ExecuteLockedAsync(proof.RuntimeInstanceId,
                (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                    context, proof.RuntimeInstanceId, async (registration, currentToken) =>
                    {
                        if (proof.Audience != registration.Binding.ObservationEndpoint)
                            throw new RuntimeAuthorizationException("runtime_source_audience_invalid");
                        RequireLive(registration, bearer, credential);
                        var grant = await broker.VerifyAsync(actor, proof, currentToken).ConfigureAwait(false);
                        RequireGrant(grant, registration, proof);
                        credential.LimitLifetime(grant.ExpiresAt);
                        RequireLive(registration, bearer, credential);
                        var result = await action(
                            connection, transaction, registration, grant, currentToken).ConfigureAwait(false);
                        var finalGrant = await broker.VerifyAsync(actor, proof, currentToken).ConfigureAwait(false);
                        RequireGrant(finalGrant, registration, proof);
                        RequireLive(registration, bearer, credential);
                        await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                        RequireLive(registration, bearer, credential);
                        return result;
                    }, token), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            credential.Invalidate();
            bearer.Invalidate();
        }
    }

    internal async Task<RuntimeUsageSourceReceipt> ReadReceiptAsync(
        HttpContext context, string projectId, string runId, string sessionId,
        Guid receiptId, CancellationToken cancellationToken)
    {
        var first = await projects.ReadSelectionForReadWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var receipt = await store.ReadReceiptAsync(
            receiptId, projectId, runId, sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException("runtime_usage_receipt_unknown", StatusCodes.Status404NotFound);
        var current = await projects.ReadSelectionForReadWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var binding = receipt.Registration.Binding;
        if (first.Authorization.TenantId != binding.TenantId ||
            current.Authorization.TenantId != binding.TenantId ||
            current.Selection.ProjectRevision != binding.ProjectRevision ||
            current.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            current.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            current.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(current.Selection.Snapshot.GetRawText())) !=
                binding.AcceptedSelectionHash ||
            first.Selection.Snapshot.GetRawText() != current.Selection.Snapshot.GetRawText())
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");
        return receipt;
    }

    private void RequireGrant(
        RuntimeGrantReceipt grant, RuntimeRegistration registration, RuntimeCredentialHttpRequest proof)
    {
        if (grant.GrantId != proof.GrantId || grant.RuntimeInstanceId != registration.RuntimeInstanceId ||
            grant.RegistrationRevision != registration.Revision || grant.Revision != proof.Revision ||
            grant.Issuer != options.Issuer || grant.Purpose != RuntimeCredentialPurpose.Observe ||
            grant.State != RuntimeCredentialState.Active || grant.Audience != proof.Audience ||
            grant.ConfigurationHash != proof.ConfigurationHash || grant.ExpiresAt > registration.ExpiresAt ||
            grant.ExpiresAt <= timeProvider.GetUtcNow() || grant.RecordedAt > timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_source_proof_invalid");
    }

    private void RequireLive(
        RuntimeRegistration registration, SecretCredential bearer, SecretCredential credential)
    {
        if (registration.ExpiresAt <= timeProvider.GetUtcNow() || !bearer.IsUsable() || !credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_source_expired");
    }
}
