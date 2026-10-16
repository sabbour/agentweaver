using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Lifecycle states for receiving a bootstrap credential and configuring a session.</summary>
public enum RuntimeBootstrapReceiverState { Empty, Pending, Configuring, Ready, Failed, Disposed }

/// <summary>
/// Accepts an owner-verified, run-bound bootstrap delivery and uses it once to configure an
/// authorized runtime session. A semaphore serializes delivery, replay, configuration, and disposal.
/// </summary>
public sealed class RuntimeBootstrapReceiver(
    RuntimeRegistration registration,
    IRuntimeRegistrationOwner owner,
    HttpClient broker,
    Uri brokerAddress,
    TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingTicket? _pending;
    private DeliveryReplay? _deliveryReplay;
    private ConfiguredTicket? _configured;
    private AuthorizedRuntimeSession? _session;
    private int _state;

    /// <summary>Gets the receiver state using a volatile read.</summary>
    public RuntimeBootstrapReceiverState State => (RuntimeBootstrapReceiverState)Volatile.Read(ref _state);

    /// <summary>
    /// Verifies a delivered credential against the owner and registration, then retains it only
    /// for an exact replay or the pending configure operation. Conflicting replays are rejected.
    /// </summary>
    /// <param name="request">The delivery receipt fields and credential value.</param>
    /// <param name="actor">The authenticated runtime actor making the delivery.</param>
    /// <param name="cancellationToken">Cancels owner verification or state transitions.</param>
    /// <returns>A receipt bound to the current registration and placement generation.</returns>
    /// <exception cref="RuntimeAuthorizationException">The owner, registration, or replay does not match.</exception>
    public async Task<RuntimeBootstrapDeliveryReceipt> ReceiveAsync(
        RuntimeBootstrapDeliveryRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        RuntimeContractValidation.Validate(registration);
        RuntimeContractValidation.ValidateHash(request.ConfigurationHash);
        RuntimeContractValidation.ValidateHash(request.CredentialValue);
        if (request.RuntimeInstanceId != registration.RuntimeInstanceId ||
            request.OperationId == Guid.Empty || request.GrantId == Guid.Empty ||
            request.CredentialExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_delivery_binding_invalid");

        var credential = new SecretCredential(
            request.CredentialValue, request.CredentialExpiresAt, timeProvider);
        var retained = false;
        try
        {
            var credentialHash = RuntimeContractValidation.Hash(
                System.Text.Encoding.UTF8.GetBytes(credential.GetValue()));
            var proof = new RuntimeCredentialProof(
                request.GrantId, request.RuntimeInstanceId, 1, RuntimeCredentialPurpose.Configure,
                registration.Binding.ConfigureEndpoint, request.ConfigurationHash, credential);
            var verifier = new RuntimePendingBootstrapHttpClient(broker, brokerAddress, actor);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_deliveryReplay is { } replay)
                {
                    if (!MatchesDelivery(replay, request, credentialHash))
                        throw new RuntimeAuthorizationException("runtime_delivery_conflict");
                    await RequireCurrentAsync(actor, credential, cancellationToken).ConfigureAwait(false);
                    if (State == RuntimeBootstrapReceiverState.Pending && _pending is { } pendingReplay)
                    {
                        if (!pendingReplay.Proof.Credential.IsUsable())
                            throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
                        RequireGrant(await verifier.VerifyPendingBootstrapDeliveryAsync(
                            proof, request.OperationId, cancellationToken).ConfigureAwait(false));
                        return replay.Receipt;
                    }
                    if (State == RuntimeBootstrapReceiverState.Ready && _session is { } configuredSession)
                    {
                        await configuredSession.RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                        return replay.Receipt;
                    }
                    throw new RuntimeAuthorizationException("runtime_receiver_unavailable");
                }
                if (State != RuntimeBootstrapReceiverState.Empty)
                    throw new RuntimeAuthorizationException("runtime_receiver_unavailable");
                RequireGrant(await verifier.VerifyPendingBootstrapDeliveryAsync(
                    proof, request.OperationId, cancellationToken).ConfigureAwait(false));
                await RequireCurrentAsync(actor, credential, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (_pending is { } pending)
                {
                    if (pending.Receipt.OperationId != request.OperationId ||
                        pending.Receipt.GrantId != request.GrantId ||
                        pending.Proof.ConfigurationHash != request.ConfigurationHash ||
                        pending.Proof.Credential.ExpiresAt != credential.ExpiresAt ||
                        pending.CredentialHash != RuntimeContractValidation.Hash(
                            System.Text.Encoding.UTF8.GetBytes(credential.GetValue())) ||
                        !pending.Proof.Credential.IsUsable())
                        throw new RuntimeAuthorizationException("runtime_delivery_conflict");
                    return pending.Receipt;
                }

                var binding = registration.Binding;
                var receipt = new RuntimeBootstrapDeliveryReceipt(
                    request.OperationId, request.GrantId, registration.RuntimeInstanceId,
                    registration.Revision, binding.PlacementUid, binding.PlacementGeneration,
                    binding.ExecutionFence, request.ConfigurationHash, timeProvider.GetUtcNow())
                {
                    EnvironmentCurrentFencingGeneration = binding.EnvironmentCurrentFencingGeneration,
                    EnvironmentProviderFencingGeneration = binding.EnvironmentProviderFencingGeneration
                };
                _pending = new(proof, receipt, RuntimeContractValidation.Hash(
                    System.Text.Encoding.UTF8.GetBytes(credential.GetValue())));
                _deliveryReplay = new(request.OperationId, request.GrantId, request.ConfigurationHash,
                    request.CredentialExpiresAt, credentialHash, receipt);
                Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Pending);
                retained = true;
                return receipt;
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            if (!retained)
                credential.Invalidate();
        }
    }

    /// <summary>
    /// Configures once from the pending delivery. Exact repeats return the existing session;
    /// a different configuration or operation ID is a conflict. The bootstrap credential is
    /// invalidated whether configuration succeeds or fails.
    /// </summary>
    /// <param name="bootstrap">The runtime configuration operation.</param>
    /// <param name="configuration">The exact bytes whose hash was authorized for delivery.</param>
    /// <param name="consumeOperationId">Stable operation ID for grant consumption.</param>
    /// <param name="exchangeOperationId">Stable operation ID for the credential exchange.</param>
    /// <param name="cancellationToken">Cancels configuration work.</param>
    /// <param name="resumeBootstrap">Optional validated resume bootstrap evidence.</param>
    /// <returns>The configured authorized session.</returns>
    /// <exception cref="RuntimeAuthorizationException">No valid delivery exists or an operation conflicts.</exception>
    public async Task<AuthorizedRuntimeSession> ConfigureAsync(
        RuntimeSessionBootstrap bootstrap,
        ReadOnlyMemory<byte> configuration,
        Guid consumeOperationId,
        Guid exchangeOperationId,
        CancellationToken cancellationToken,
        RuntimeResumeBootstrapRequest? resumeBootstrap = null)
    {
        resumeBootstrap?.Validate();
        if (resumeBootstrap is not null &&
            (resumeBootstrap.Registration != registration ||
             resumeBootstrap.ConsumeOperationId != consumeOperationId ||
             resumeBootstrap.ExchangeOperationId != exchangeOperationId))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_invalid");
        var resumeBootstrapHash = resumeBootstrap is null
            ? null
            : RuntimeContractValidation.Hash(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(resumeBootstrap));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RuntimeBootstrapReceiverState.Ready && _configured is { } configured &&
                _session is { } currentSession)
            {
                if (configured.ConfigurationHash != RuntimeContractValidation.Hash(configuration.Span) ||
                    configured.ConsumeOperationId != consumeOperationId ||
                    configured.ExchangeOperationId != exchangeOperationId ||
                    configured.ResumeBootstrapHash != resumeBootstrapHash)
                    throw new RuntimeAuthorizationException("runtime_configuration_conflict");
                await currentSession.RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                return currentSession;
            }
            if (State != RuntimeBootstrapReceiverState.Pending || _pending is not { } pending)
                throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
            if (RuntimeContractValidation.Hash(configuration.Span) != pending.Proof.ConfigurationHash)
                throw new RuntimeAuthorizationException("runtime_configuration_invalid");
            if (resumeBootstrap is not null && resumeBootstrap.BootstrapOperationId != pending.Receipt.OperationId)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_invalid");
            Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Configuring);
            try
            {
                _session = await bootstrap.ConfigureAsync(
                    configuration, pending.Proof, consumeOperationId, exchangeOperationId,
                    cancellationToken, resumeBootstrap).ConfigureAwait(false);
                _configured = new(pending.Proof.ConfigurationHash, consumeOperationId,
                    exchangeOperationId, resumeBootstrapHash);
                Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Ready);
                return _session;
            }
            catch
            {
                Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Failed);
                throw;
            }
            finally
            {
                pending.Proof.Credential.Invalidate();
                _pending = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Rereads owner registration state and confirms both registration and credential remain current.</summary>
    private async Task RequireCurrentAsync(
        RuntimeActorAuthorization actor, SecretCredential credential, CancellationToken cancellationToken)
    {
        var current = await owner.ReadCurrentAsync(
            registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        RuntimeSessionBootstrap.RequireCurrent(current, actor, timeProvider);
        if (current != registration || !credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>Compares all replay-sensitive values, using a hash rather than retaining another secret copy.</summary>
    private static bool MatchesDelivery(
        DeliveryReplay replay,
        RuntimeBootstrapDeliveryRequest request,
        string credentialHash) =>
        replay.OperationId == request.OperationId &&
        replay.GrantId == request.GrantId &&
        replay.ConfigurationHash == request.ConfigurationHash &&
        replay.CredentialExpiresAt == request.CredentialExpiresAt &&
        replay.CredentialHash == credentialHash;

    /// <summary>Requires the grant receipt to match the registration revision and expiry boundary.</summary>
    private void RequireGrant(RuntimeGrantReceipt grant)
    {
        if (grant.RegistrationRevision != registration.Revision ||
            grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_delivery_binding_invalid");
    }

    /// <summary>Invalidates pending credentials, clears replay state, and disposes a configured session.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == RuntimeBootstrapReceiverState.Disposed)
                return;
            Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Disposed);
            _pending?.Proof.Credential.Invalidate();
            _pending = null;
            _deliveryReplay = null;
            _configured = null;
            if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record PendingTicket(
        RuntimeCredentialProof Proof, RuntimeBootstrapDeliveryReceipt Receipt, string CredentialHash);

    private sealed record DeliveryReplay(
        Guid OperationId,
        Guid GrantId,
        string ConfigurationHash,
        DateTimeOffset CredentialExpiresAt,
        string CredentialHash,
        RuntimeBootstrapDeliveryReceipt Receipt);

    private sealed record ConfiguredTicket(
        string ConfigurationHash,
        Guid ConsumeOperationId,
        Guid ExchangeOperationId,
        string? ResumeBootstrapHash);
}
