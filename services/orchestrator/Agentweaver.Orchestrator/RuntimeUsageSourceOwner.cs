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

    internal Task<RuntimeUsageCostSnapshotReceipt> ReadCostSnapshotAsync(
        RuntimeActorAuthorization actor, string eventsOwnerAddress, RuntimeUsageCostSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(eventsOwnerAddress, UriKind.Absolute, out var address))
            throw new RuntimeAuthorizationException("runtime_usage_cost_owner_unavailable");
        var scope = request.Registration.Binding;
        return RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageCostSnapshotReceipt>(
            client, RuntimeOwnerHttpTransport.RequireOwnerAddress(address),
            $"/internal/projects/{Uri.EscapeDataString(scope.ProjectId)}/runs/{Uri.EscapeDataString(scope.RunId)}" +
            "/usage/copilot-cost-snapshot", actor, request, cancellationToken);
    }
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
            (connection, transaction, registration, grant, actor, token) =>
                store.RegisterWithinTransactionAsync(
                    connection, transaction, registration, grant, request.Source, token), cancellationToken);
    }

    internal Task<RuntimeUsageSourceReceipt> AppendAsync(
        HttpContext context, RuntimeUsageObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Observation);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.AppendWithinTransactionAsync(
                    connection, transaction, registration, grant, request.Observation, token), cancellationToken);
    }

    internal async Task<RuntimeNativeTurnAdmissionReceipt> BeginNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Message);
        ArgumentNullException.ThrowIfNull(request.Source);
        var receipt = await ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.BeginNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, request,
                    (source, currentToken) => broker.ReadCostSnapshotAsync(actor, options.EventsOwnerBaseAddress,
                        new(1, registration, source), currentToken), token), cancellationToken).ConfigureAwait(false);
        return receipt ?? throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
    }

    internal async Task PrepareNativeTurnAsync(
        HttpContext context, RuntimeRegistration expected, SessionIdentity root,
        MafExecutionCheckpointSnapshot checkpoint, MafExecutionDispatchIntent intent,
        RuntimeA2ASendRequest message,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireCurrentOwner,
        CancellationToken cancellationToken)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        var bearer = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider);
        bool prepared;
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, CoordinationIdentity.ReadTenantSelector(context));
            prepared = await store.ExecuteLockedAsync(expected.RuntimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, expected.RuntimeInstanceId, async (registration, currentToken) =>
                {
                    if (registration != expected)
                        throw new RuntimeAuthorizationException("runtime_registration_stale");
                    await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                    var admitted = await store.PrepareNativeTurnWithinTransactionAsync(
                        connection, transaction, root, checkpoint, intent, registration, message,
                        (source, quoteToken) => broker.ReadCostSnapshotAsync(
                            actor, options.EventsOwnerBaseAddress, new(1, registration, source), quoteToken), currentToken)
                        .ConfigureAwait(false);
                    await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                    if (!bearer.IsUsable() ||
                        registration.ExpiresAt <= timeProvider.GetUtcNow())
                        throw new RuntimeAuthorizationException("runtime_actor_expired");
                    await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                    return admitted;
                }, token), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            bearer.Invalidate();
        }
        if (!prepared)
            throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
    }

    internal Task RequireNativeTurnAccountedAsync(
        HttpContext context, RuntimeRegistration expected, RuntimeA2ASendRequest message, string answer,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireCurrentOwner,
        CancellationToken cancellationToken) =>
        store.ExecuteLockedAsync(expected.RuntimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, expected.RuntimeInstanceId, async (registration, currentToken) =>
                {
                    if (registration != expected)
                        throw new RuntimeAuthorizationException("runtime_registration_stale");
                    await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                    await store.RequireNativeTurnAccountedWithinTransactionAsync(
                        connection, transaction, registration, message, answer, currentToken).ConfigureAwait(false);
                    return true;
                }, token), cancellationToken);

    internal Task<RuntimeNativeTurnRecordedReceipt> RecordNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Admission);
        ArgumentNullException.ThrowIfNull(request.Observation);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.RecordNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, request, token), cancellationToken);
    }

    internal Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnAccountingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        var snapshotRequest = RuntimeNativeTurnContract.AccountingSnapshotRequest(
            request.Recorded, request.RequiredReceipts);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.CompleteNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, request,
                    currentToken => broker.ReadCostSnapshotAsync(
                        actor, options.EventsOwnerBaseAddress, snapshotRequest, currentToken), token),
            cancellationToken);
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
        Func<NpgsqlConnection, NpgsqlTransaction, RuntimeRegistration, RuntimeGrantReceipt, RuntimeActorAuthorization,
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
                            connection, transaction, registration, grant, actor, currentToken).ConfigureAwait(false);
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

    internal async Task<UsageDispatchSourceCompletionManifest> ReadSourceCompletionAsync(
        HttpContext context, string projectId, string runId, string sessionId, Guid dispatchId,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.ValidateIdentifier(projectId);
        RuntimeContractValidation.ValidateIdentifier(runId);
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        if (dispatchId == Guid.Empty)
            throw new ArgumentException("A non-empty native dispatch identity is required.", nameof(dispatchId));
        var first = await projects.ReadSelectionForReadWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        var stored = await store.ReadSourceCompletionAsync(
            projectId, runId, sessionId, dispatchId, cancellationToken).ConfigureAwait(false);
        var current = await projects.ReadSelectionForReadWithAuthorityAsync(
            context, projectId, runId, cancellationToken).ConfigureAwait(false);
        if (first.Authorization.TenantId != current.Authorization.TenantId ||
            first.Selection.Snapshot.GetRawText() != current.Selection.Snapshot.GetRawText())
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");
        // Native output receipts never populate the source-completion fields.
        if (stored is not { } proof)
            throw new CoordinationException(
                "runtime_usage_source_completion_unavailable", StatusCodes.Status404NotFound);
        if (proof.Manifest.TenantId != current.Authorization.TenantId ||
            proof.SelectionHash != RuntimeContractValidation.Hash(
                Encoding.UTF8.GetBytes(current.Selection.Snapshot.GetRawText())))
            throw new RuntimeAuthorizationException("runtime_usage_receipt_scope_invalid");
        return proof.Manifest;
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
