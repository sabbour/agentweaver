using System.Runtime.CompilerServices;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class AuthorizedRuntimeSession : IAsyncDisposable
{
    private readonly RuntimeCopilotSession _session;
    private readonly SecretCredential _sourceCredential;
    private readonly SecretCredential _modelCredential;
    private readonly IRuntimeRegistrationOwner _owner;
    private readonly RuntimeBrokerCredentialClient _broker;
    private readonly RuntimeActorAuthorization _actor;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    internal AuthorizedRuntimeSession(
        RuntimeRegistration registration, RuntimeGrantReceipt sourceGrant, SecretCredential sourceCredential,
        RuntimeCopilotSession session, SecretCredential modelCredential, IRuntimeRegistrationOwner owner,
        RuntimeBrokerCredentialClient broker, RuntimeActorAuthorization actor, TimeProvider timeProvider)
    {
        Registration = registration;
        SourceGrant = sourceGrant;
        _sourceCredential = sourceCredential;
        _session = session;
        _modelCredential = modelCredential;
        _owner = owner;
        _broker = broker;
        _actor = actor;
        _timeProvider = timeProvider;
    }

    public RuntimeRegistration Registration { get; }
    public RuntimeGrantReceipt SourceGrant { get; }
    public SdkSessionFacts Facts => _session.Facts;

    public async IAsyncEnumerable<SdkUsageObservation> ReadUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await foreach (var observation in _session.ReadUsageAsync(cancellationToken))
        {
            await _broker.VerifySourceAsync(Proof(), cancellationToken);
            var current = await _owner.ReadCurrentAsync(
                Registration.RuntimeInstanceId, _actor, cancellationToken);
            RuntimeSessionBootstrap.RequireCurrent(current, _actor, _timeProvider);
            if (current != Registration || !_sourceCredential.IsUsable() || !_modelCredential.IsUsable() ||
                Volatile.Read(ref _disposed) != 0)
                throw new RuntimeAuthorizationException("runtime_registration_stale");
            yield return observation;
        }
    }

    internal RuntimeCredentialProof Proof() =>
        new(SourceGrant.GrantId, SourceGrant.RuntimeInstanceId, SourceGrant.Revision,
            RuntimeCredentialPurpose.Observe, SourceGrant.Audience, SourceGrant.ConfigurationHash,
            _sourceCredential);

    public async IAsyncEnumerable<RuntimeUsageSourceReceipt> CommitUsageAsync(
        RuntimeUsageSourceHttpClient source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await source.RegisterAsync(this, cancellationToken).ConfigureAwait(false);
        await foreach (var observation in _session.ReadUsageAsync(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            RuntimeSessionBootstrap.RequireCurrent(Registration, _actor, _timeProvider);
            if (!_sourceCredential.IsUsable() || !_modelCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_credential_unavailable");
            yield return await source.AppendAsync(this, observation, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            if (_sourceCredential.IsUsable())
                await _broker.RevokeAsync(Proof(), Guid.NewGuid(), CancellationToken.None);
        }
        finally
        {
            _sourceCredential.Invalidate();
            _modelCredential.Invalidate();
            await _session.DisposeAsync();
        }
    }
}
