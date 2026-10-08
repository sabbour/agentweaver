using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public enum RuntimeBootstrapReceiverState { Empty, Pending, Configuring, Ready, Failed, Disposed }

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

    public RuntimeBootstrapReceiverState State => (RuntimeBootstrapReceiverState)Volatile.Read(ref _state);

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

    public async Task<AuthorizedRuntimeSession> ConfigureAsync(
        RuntimeSessionBootstrap bootstrap,
        ReadOnlyMemory<byte> configuration,
        Guid consumeOperationId,
        Guid exchangeOperationId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == RuntimeBootstrapReceiverState.Ready && _configured is { } configured &&
                _session is { } currentSession)
            {
                if (configured.ConfigurationHash != RuntimeContractValidation.Hash(configuration.Span) ||
                    configured.ConsumeOperationId != consumeOperationId ||
                    configured.ExchangeOperationId != exchangeOperationId)
                    throw new RuntimeAuthorizationException("runtime_configuration_conflict");
                await currentSession.RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                return currentSession;
            }
            if (State != RuntimeBootstrapReceiverState.Pending || _pending is not { } pending)
                throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
            if (RuntimeContractValidation.Hash(configuration.Span) != pending.Proof.ConfigurationHash)
                throw new RuntimeAuthorizationException("runtime_configuration_invalid");
            Volatile.Write(ref _state, (int)RuntimeBootstrapReceiverState.Configuring);
            try
            {
                _session = await bootstrap.ConfigureAsync(
                    configuration, pending.Proof, consumeOperationId, exchangeOperationId,
                    cancellationToken).ConfigureAwait(false);
                _configured = new(pending.Proof.ConfigurationHash, consumeOperationId,
                    exchangeOperationId);
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

    private async Task RequireCurrentAsync(
        RuntimeActorAuthorization actor, SecretCredential credential, CancellationToken cancellationToken)
    {
        var current = await owner.ReadCurrentAsync(
            registration.RuntimeInstanceId, actor, cancellationToken).ConfigureAwait(false);
        RuntimeSessionBootstrap.RequireCurrent(current, actor, timeProvider);
        if (current != registration || !credential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    private static bool MatchesDelivery(
        DeliveryReplay replay,
        RuntimeBootstrapDeliveryRequest request,
        string credentialHash) =>
        replay.OperationId == request.OperationId &&
        replay.GrantId == request.GrantId &&
        replay.ConfigurationHash == request.ConfigurationHash &&
        replay.CredentialExpiresAt == request.CredentialExpiresAt &&
        replay.CredentialHash == credentialHash;

    private void RequireGrant(RuntimeGrantReceipt grant)
    {
        if (grant.RegistrationRevision != registration.Revision ||
            grant.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_delivery_binding_invalid");
    }

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
        Guid ExchangeOperationId);
}
