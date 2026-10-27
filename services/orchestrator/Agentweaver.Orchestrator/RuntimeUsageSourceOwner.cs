using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Npgsql;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Configures the runtime-usage broker owner used for source grants and cost snapshots.
/// </summary>
internal sealed record RuntimeUsageSourceOptions(Uri BrokerOwnerAddress);

/// <summary>
/// Verifies source grants and reads usage-cost snapshots from their owning services.
/// </summary>
internal sealed class RuntimeUsageBrokerClient(HttpClient client, RuntimeUsageSourceOptions options)
{
    /// <summary>
    /// Verifies the credential proof with the broker owner.
    /// </summary>
    internal Task<RuntimeGrantReceipt> VerifyAsync(
        RuntimeActorAuthorization actor, RuntimeCredentialHttpRequest proof, CancellationToken cancellationToken) =>
        RuntimeOwnerHttpTransport.SendAsync<RuntimeGrantReceipt>(
            client, options.BrokerOwnerAddress, "/internal/runtime/source/verify", actor, proof, cancellationToken);

    /// <summary>
    /// Reads the current Copilot cost snapshot for a registered runtime source.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the configured Events owner address is invalid.</exception>
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

    /// <summary>
    /// Reads Events spend evidence for one budget bucket and the dispatches that still hold exposure in it.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the configured Events owner address is invalid.</exception>
    internal Task<UsageSpendEvidence> ReadSpendEvidenceAsync(
        RuntimeActorAuthorization actor, string eventsOwnerAddress, string projectId, string runId,
        UsageSpendEvidenceRequest request, CancellationToken cancellationToken)
    {
        // Events is the owner of spend evidence. An invalid configured address fails closed, so
        // spend budget admission never runs without evidence.
        if (!Uri.TryCreate(eventsOwnerAddress, UriKind.Absolute, out var address))
            throw new RuntimeAuthorizationException("runtime_usage_cost_owner_unavailable");
        // The call forwards the runtime actor's credentials and is scoped to one project run.
        return RuntimeOwnerHttpTransport.SendAsync<UsageSpendEvidence>(
            client, RuntimeOwnerHttpTransport.RequireOwnerAddress(address),
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            "/usage/spend-evidence", actor, request, cancellationToken);
    }
}

/// <summary>
/// Authorizes runtime usage-source operations and writes their grant-backed evidence transactionally.
/// It revalidates registration and grant state around writes and limits observations to the current accepted selection.
/// </summary>
internal sealed class RuntimeUsageSourceOwner(
    RuntimeUsageSourceStore store, RuntimeRegistrationOwner registrations,
    RuntimeUsageBrokerClient broker, ProjectsRunSelectionClient projects,
    OrchestratorOptions options, TimeProvider timeProvider)
{
    /// <summary>
    /// Registers a source for the specified runtime after validating its observation credential.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source request is malformed or its authorization proof is invalid.</exception>
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

    /// <summary>
    /// Appends one usage observation for the current registered source and grant.
    /// </summary>
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

    /// <summary>
    /// Admits a native model turn and stores its cost snapshot and dispatch evidence before the turn is sent.
    /// </summary>
    internal Task<RuntimeNativeTurnBeginResult> BeginNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnBeginRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Message);
        ArgumentNullException.ThrowIfNull(request.Source);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.BeginNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, request,
                    (source, currentToken) => broker.ReadCostSnapshotAsync(actor, options.EventsOwnerBaseAddress,
                        new(1, registration, source), currentToken), token), cancellationToken);
    }

    /// <summary>
    /// Reads the current native credit-denial receipt and any associated no-send evidence.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when no matching denial evidence exists.</exception>
    internal Task<RuntimeNativeCreditDenialReadResult> ReadNativeCreditDenialAsync(
        HttpContext context, RuntimeNativeCreditDenialReadRequest request, CancellationToken cancellationToken)
    {
        RuntimeNativeDenialContract.ValidateReadRequest(request);
        return ExecuteWriteAsync(context, request.Authorization,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                var source = await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                var receipt = await store.ReadNativeCreditDenialWithinTransactionAsync(
                    connection, transaction, registration, source, request.DispatchId, request.Stage, token)
                    .ConfigureAwait(false);
                var noSend = receipt?.Stage == RuntimeCreditLimitDenialStage.NativeTurnBegin
                    ? await store.ReadNativeTurnNoSendWithinTransactionAsync(
                        connection, transaction, registration, receipt.CanonicalPayloadHash, token)
                        .ConfigureAwait(false)
                    : null;
                var result = new RuntimeNativeCreditDenialReadResult(receipt, noSend);
                RuntimeNativeDenialContract.ValidateReadResult(
                    result, registration, source, request.DispatchId, request.Stage);
                return result;
            }, cancellationToken);
    }

    /// <summary>
    /// Records host pre-queue fixed-credit guard evidence after verifying its registration, source, and grant tuple.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the receipt is not a valid host pre-queue guard for this registration.</exception>
    internal Task<RuntimeFixedCreditGuardReceipt> RecordFixedCreditGuardAsync(
        HttpContext context, RuntimeFixedCreditGuardRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Receipt);
        RuntimeFixedCreditGuardContract.ValidateReceipt(request.Receipt);
        if (request.Receipt.Stage != RuntimeFixedCreditGuardStage.HostPreQueue ||
            request.Authorization.RuntimeInstanceId != request.Receipt.Registration.RuntimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
        return ExecuteWriteAsync(context, request.Authorization,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                if (request.Receipt.Registration != registration)
                    throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
                var source = await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                RuntimeFixedCreditGuardContract.ValidateSourceTuple(
                    request.Receipt, registration, grant, source);
                return await store.RecordFixedCreditGuardWithinTransactionAsync(
                    connection, transaction, registration, request.Receipt, grant, source, token)
                    .ConfigureAwait(false);
            }, cancellationToken);
    }

    /// <summary>
    /// Records fixed-credit no-send evidence for a denied host pre-queue dispatch.
    /// </summary>
    internal Task<RuntimeFixedCreditNoSendReceipt> RecordFixedCreditNoSendAsync(
        HttpContext context, RuntimeFixedCreditNoSendEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Receipt);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        if (request.Receipt.Stage != RuntimeFixedCreditGuardStage.HostPreQueue ||
            request.Authorization.RuntimeInstanceId != request.Receipt.Registration.RuntimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
        return ExecuteWriteAsync(context, request.Authorization,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                var source = await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                return await store.RecordFixedCreditNoSendWithinTransactionAsync(
                    connection, transaction, registration, grant, source, request, token)
                    .ConfigureAwait(false);
            }, cancellationToken);
    }

    /// <summary>
    /// Reads and validates the fixed-credit guard and its supporting evidence for a runtime dispatch.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the requested guard is not recorded.</exception>
    internal Task<RuntimeFixedCreditGuardReadResult> ReadFixedCreditGuardAsync(
        HttpContext context, RuntimeFixedCreditGuardReadRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeFixedCreditGuardContract.ValidateReadRequest(request);
        var proof = request.Authorization
            ?? throw new RuntimeAuthorizationException("runtime_source_proof_invalid");
        if (request.Stage != RuntimeFixedCreditGuardStage.HostPreQueue ||
            proof.RuntimeInstanceId != request.RuntimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_request_invalid");
        return ExecuteWriteAsync(context, proof,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                var source = await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                var result = await store.ReadFixedCreditGuardResultWithinTransactionAsync(
                    connection, transaction, registration, request, includeEvidence: true,
                    grant, source, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "runtime_fixed_credit_guard_unknown", StatusCodes.Status404NotFound);
                RuntimeFixedCreditGuardContract.ValidateReadResult(
                    result, registration, request, grant, source);
                return result;
            }, cancellationToken);
    }

    /// <summary>
    /// Reads the current persisted credit-guard snapshot for the specified dispatch intent.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the requested guard snapshot is unknown.</exception>
    internal Task<RuntimeCreditGuardSnapshot> ReadCreditGuardAsync(
        HttpContext context, RuntimeCreditGuardReadRequest request, CancellationToken cancellationToken)
    {
        RuntimeCreditGuardObservationContract.ValidateReadRequest(request);
        return ExecuteWriteAsync(context, request.Authorization,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                RuntimeCreditGuardObservationContract.RequireIntent(request.Intent, registration);
                _ = await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                var snapshot = await store.ReadCreditGuardWithinTransactionAsync(
                    connection, transaction, registration, request.Intent, token).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "runtime_credit_guard_unknown", StatusCodes.Status404NotFound);
                RuntimeCreditGuardObservationContract.ValidateSnapshot(
                    snapshot, registration, request.Intent);
                return snapshot;
            }, cancellationToken);
    }

    /// <summary>
    /// Records evidence that a denied native model turn was not sent.
    /// </summary>
    internal Task<RuntimeNativeTurnNoSendReceipt> RecordNativeTurnNoSendAsync(
        HttpContext context, RuntimeNativeTurnNoSendEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Authorization);
        ArgumentNullException.ThrowIfNull(request.Denial);
        ArgumentNullException.ThrowIfNull(request.Evidence);
        return ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.RecordNativeTurnNoSendWithinTransactionAsync(
                    connection, transaction, registration, grant, request, token), cancellationToken);
    }

    /// <summary>
    /// Persists native-turn admission and cost evidence before the host submits the native model request.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when actor credentials, registration, grant, owner state, or cost admission is invalid.</exception>
    /// <remarks>
    /// The caller's owner recheck runs before and after the transaction work; any denial is surfaced after the lock is released.
    /// Spend budget admission reads Events spend evidence inside the same transaction, so its reservation commits with the dispatch.
    /// </remarks>
    internal async Task PrepareNativeTurnAsync(
        HttpContext context, RuntimeRegistration expected, SessionIdentity root,
        MafExecutionCheckpointSnapshot checkpoint, MafExecutionDispatchIntent intent,
        RuntimeA2ASendRequest message,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> requireCurrentOwner,
        CancellationToken cancellationToken,
        SpendBudgetAdmissionContext? spendBudgets = null)
    {
        // Check the caller's bearer token before any database work. An expired or missing expiry
        // is rejected because the token is forwarded to Events during admission.
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        // Wrap the token so it stops being usable at its expiry and can be invalidated after this call.
        var bearer = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider);
        RuntimeNativePreparationOutcome outcome;
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, CoordinationIdentity.ReadTenantSelector(context));
            // Run the whole preparation in one PostgreSQL transaction under the runtime advisory lock,
            // against the registration that is current inside that lock.
            outcome = await store.ExecuteLockedAsync(expected.RuntimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, expected.RuntimeInstanceId, async (registration, currentToken) =>
                {
                    // The registration must not have changed since the caller read it.
                    if (registration != expected)
                        throw new RuntimeAuthorizationException("runtime_registration_stale");
                    // First owner authority check: the caller must still be the current coordination owner.
                    await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                    // Run the admission checks and write the dispatch. Credit snapshots and spend
                    // evidence are read from Events with the caller's credentials and the project and
                    // run from the current registration.
                    var admitted = await store.PrepareNativeTurnWithinTransactionAsync(
                        connection, transaction, root, checkpoint, intent, registration, message,
                        (source, quoteToken) => broker.ReadCostSnapshotAsync(
                            actor, options.EventsOwnerBaseAddress, new(1, registration, source), quoteToken), currentToken,
                        spendBudgets,
                        (spendRequest, spendToken) => broker.ReadSpendEvidenceAsync(
                            actor, options.EventsOwnerBaseAddress, registration.Binding.ProjectId,
                            registration.Binding.RunId, spendRequest, spendToken))
                        .ConfigureAwait(false);
                    // Second owner authority check: ownership can change while Events is being called,
                    // so check it again before commit.
                    await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                    // The bearer token and the registration must still be valid at commit time.
                    // Any throw here rolls back the transaction, so no dispatch row or reservation remains.
                    if (!bearer.IsUsable() ||
                        registration.ExpiresAt <= timeProvider.GetUtcNow())
                        throw new RuntimeAuthorizationException("runtime_actor_expired");
                    // Commit admitted dispatches and denial evidence alike; a denial is reported below.
                    await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                    return admitted;
                }, token), cancellationToken).ConfigureAwait(false);
        }
        catch (RuntimeNumericLimitDeniedException denial)
        {
            // The failed admission transaction has unwound before this fresh transaction starts.
            // Recheck the same owner, checkpoint, count, source and send-state fingerprint before
            // appending denial evidence; a changed state leaves no receipt behind.
            if (denial.SendStateEmpty)
                _ = await store.ExecuteLockedAsync(expected.RuntimeInstanceId,
                    (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                        context, expected.RuntimeInstanceId, async (registration, currentToken) =>
                        {
                            if (registration != expected)
                                throw new RuntimeAuthorizationException("runtime_registration_stale");
                            await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                            if (!bearer.IsUsable() || registration.ExpiresAt <= timeProvider.GetUtcNow())
                                throw new RuntimeAuthorizationException("runtime_actor_expired");
                            var receipt = await store.RecordNativeNumericLimitDenialWithinTransactionAsync(
                                connection, transaction, root, checkpoint, intent, registration, message,
                                denial, currentToken).ConfigureAwait(false);
                            await requireCurrentOwner(connection, transaction, currentToken).ConfigureAwait(false);
                            await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                            return receipt;
                        }, token), cancellationToken).ConfigureAwait(false);
            throw new RuntimeAuthorizationException(denial.Code);
        }
        finally
        {
            // Never keep the forwarded token after the call, on success or failure.
            bearer.Invalidate();
        }
        // Report denials only after the lock is released. The caller then does not send the dispatch.
        if (outcome.CreditDenial is not null)
            throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
        if (outcome.SpendBudgetDenialCode is { } spendBudgetDenial)
            throw new RuntimeAuthorizationException(spendBudgetDenial);
    }

    /// <summary>
    /// Requires the native turn to have been accounted before the caller treats its result as complete.
    /// </summary>
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

    /// <summary>
    /// Records the usage observation for an admitted native turn.
    /// </summary>
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

    /// <summary>
    /// Completes native-turn accounting using a fresh cost snapshot and the required usage receipts.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the caller, source grant, or recorded-turn accounting is stale or invalid.</exception>
    internal async Task<RuntimeNativeTurnAccountingReceipt> CompleteNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnAccountingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization);
        var snapshotRequest = RuntimeNativeTurnContract.AccountingSnapshotRequest(
            request.Recorded, request.RequiredReceipts);
        var recorded = await ExecuteWriteAsync(context, request.Authorization,
            async (connection, transaction, registration, grant, actor, token) =>
            {
                await store.ReadCurrentSourceForGrantAsync(
                    connection, transaction, registration, grant, token).ConfigureAwait(false);
                return await store.ReadNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, request.Recorded, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        RuntimeNativeTurnContract.ValidateAccountingSnapshot(snapshotRequest, recorded);
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        var bearer = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider);
        RuntimeUsageCostSnapshotReceipt snapshot;
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, CoordinationIdentity.ReadTenantSelector(context));
            snapshot = await broker.ReadCostSnapshotAsync(
                actor, options.EventsOwnerBaseAddress, snapshotRequest, cancellationToken).ConfigureAwait(false);
            if (!bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_actor_expired");
        }
        finally
        {
            bearer.Invalidate();
        }
        return await ExecuteWriteAsync(context, request.Authorization,
            (connection, transaction, registration, grant, actor, token) =>
                store.CompleteNativeTurnWithinTransactionAsync(
                    connection, transaction, registration, grant, request, snapshot, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a recorded native turn and confirms its registration is still current.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the recorded turn's registration is no longer current.</exception>
    internal async Task<RuntimeNativeTurnRecordedReceipt> ReadNativeTurnAsync(
        HttpContext context, RuntimeNativeTurnRecordedReceipt expected, CancellationToken cancellationToken)
    {
        RuntimeNativeTurnContract.ValidateRecorded(expected);
        var runtimeInstanceId = expected.Admission.Registration.RuntimeInstanceId;
        var recorded = await store.ExecuteLockedAsync(runtimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, runtimeInstanceId, (registration, currentToken) =>
                    store.ReadNativeTurnWithinTransactionAsync(
                        connection, transaction, registration, expected, currentToken), token),
            cancellationToken).ConfigureAwait(false);
        var current = await registrations.ReadCurrentAsync(context, runtimeInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (recorded.Admission.Registration != current)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        return recorded;
    }

    /// <summary>
    /// Reads the current runtime source and verifies its canonical payload hash against the current registration.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the source is inconsistent with current registration state.</exception>
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

    /// <summary>
    /// Validates the observation proof and performs a locked write after checking live credentials, grant, and registration.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the proof, credential, grant, registration, or audience is invalid or expires.</exception>
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

    /// <summary>
    /// Reads a usage receipt only when the caller's current accepted selection still matches its registration scope.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the requested receipt does not exist.</exception>
    /// <exception cref="RuntimeAuthorizationException">Thrown when tenant, selection revisions, or accepted selection hash differs.</exception>
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

    /// <summary>
    /// Reads a native source-completion manifest after confirming the accepted selection stayed unchanged.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when source-completion evidence is unavailable.</exception>
    /// <exception cref="RuntimeAuthorizationException">Thrown when request identifiers or manifest scope are invalid.</exception>
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

    /// <summary>
    /// Requires the broker receipt to match the exact active grant and registration proof.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when grant identity, scope, state, audience, or validity interval differs.</exception>
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

    /// <summary>
    /// Requires the bearer and observation credential to remain valid within the active registration lifetime.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the registration or either credential has expired.</exception>
    private void RequireLive(
        RuntimeRegistration registration, SecretCredential bearer, SecretCredential credential)
    {
        if (registration.ExpiresAt <= timeProvider.GetUtcNow() || !bearer.IsUsable() || !credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_source_expired");
    }
}
