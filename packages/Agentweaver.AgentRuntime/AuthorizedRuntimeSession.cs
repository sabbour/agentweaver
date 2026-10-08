using System.Runtime.CompilerServices;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed record RuntimeSessionRefreshReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    Guid OperationId,
    long PreviousRevision,
    RuntimeGrantReceipt SourceGrant,
    DateTimeOffset EffectiveSessionExpiresAt);

public sealed class AuthorizedRuntimeSession : IAsyncDisposable
{
    private readonly RuntimeCopilotSession _session;
    private RuntimeCredentialExchange _source;
    private readonly SecretCredential _modelCredential;
    private readonly RuntimeModelCredential _modelAuthorization;
    private readonly IRuntimeRegistrationOwner _owner;
    private readonly RuntimeBrokerCredentialClient _broker;
    private readonly RuntimeActorAuthorization _actor;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeActionHttpClient? _actions;
    private readonly Func<RuntimeRegistration, CancellationToken, Task>? _requireReadiness;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _authorityGate = new(1, 1);
    private RuntimeSessionRefreshReceipt? _refreshReplay;
    private int _disposed;

    internal AuthorizedRuntimeSession(
        RuntimeRegistration registration, RuntimeGrantReceipt sourceGrant, SecretCredential sourceCredential,
        RuntimeCopilotSession session, RuntimeModelCredential modelCredential, IRuntimeRegistrationOwner owner,
        RuntimeBrokerCredentialClient broker, RuntimeActorAuthorization actor, TimeProvider timeProvider,
        RuntimeActionHttpClient? actions = null,
        Func<RuntimeRegistration, CancellationToken, Task>? requireReadiness = null)
    {
        Registration = registration;
        _source = new(sourceGrant, sourceCredential, isReplay: false);
        _session = session;
        _modelAuthorization = modelCredential;
        _modelCredential = modelCredential.Credential;
        _owner = owner;
        _broker = broker;
        _actor = actor;
        _timeProvider = timeProvider;
        _actions = actions;
        _requireReadiness = requireReadiness;
    }

    public RuntimeRegistration Registration { get; }
    public RuntimeGrantReceipt SourceGrant => Volatile.Read(ref _source).Receipt;
    public SdkSessionFacts Facts => _session.Facts;
    public RuntimeSessionRecoveryMode RecoveryMode => _session.RecoveryMode;
    public string? RecoveryReason => _session.RecoveryReason;

    public async Task<string> SendTurnAsync(
        string prompt, RuntimeSessionMaterialHttpClient material, CancellationToken cancellationToken,
        Guid? userEventId = null)
    {
        if (_actions is null)
            throw new RuntimeAuthorizationException("runtime_action_authority_unavailable");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            RuntimeCopilotSession.HasUnsupportedPromptControls(prompt))
            throw new ArgumentException("A bounded text prompt is required.", nameof(prompt));
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (userEventId is { } recordedId)
            {
                if (recordedId == Guid.Empty)
                    throw new ArgumentException("A non-empty A2A message identity is required.", nameof(userEventId));
                await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                var binding = Registration.Binding;
                if (await material.ReadOptionalTurnAsync(
                    new(binding.ProjectId, binding.RunId, binding.SessionId), recordedId, cancellationToken)
                    .ConfigureAwait(false) is not null)
                    throw new RuntimeAuthorizationException("runtime_turn_already_recorded");
            }
            await CommitTurnContentAsync(material, userEventId ?? Guid.NewGuid(), "user", prompt, cancellationToken)
                .ConfigureAwait(false);
            await _actions.RequireAsync(Registration, "model.turn", System.Text.Encoding.UTF8.GetBytes(prompt),
                RequireCurrentAsync, cancellationToken).ConfigureAwait(false);
            var answer = await _session.SendTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
            await CommitTurnContentAsync(material, Guid.NewGuid(), "assistant", answer, cancellationToken).ConfigureAwait(false);
            await CommitNativeCacheAsync(material, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            return answer;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    internal async Task RequireCurrentAsync(CancellationToken cancellationToken)
    {
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    private async Task RequireCurrentCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Proof().Credential.IsUsable() || !_modelCredential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_credential_unavailable");
        if (_requireReadiness is not null)
            await _requireReadiness(Registration, cancellationToken).ConfigureAwait(false);
        var current = await _owner.ReadCurrentAsync(
            Registration.RuntimeInstanceId, _actor, cancellationToken).ConfigureAwait(false);
        RuntimeSessionBootstrap.RequireCurrent(current, _actor, _timeProvider);
        if (current != Registration || !Proof().Credential.IsUsable() || !_modelCredential.IsUsable() ||
            Volatile.Read(ref _disposed) != 0)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
        await _broker.VerifySourceAsync(Proof(), cancellationToken).ConfigureAwait(false);
        await _broker.VerifyModelCredentialAsync(_modelAuthorization, cancellationToken).ConfigureAwait(false);
        RuntimeSessionBootstrap.RequireCurrent(current, _actor, _timeProvider);
    }

    public async Task<RuntimeSessionRefreshReceipt> RefreshAsync(
        Guid operationId, long expectedSourceRevision, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || expectedSourceRevision <= 0)
            throw new RuntimeAuthorizationException("runtime_refresh_invalid");
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_refreshReplay is { } replay && replay.OperationId == operationId)
            {
                if (replay.PreviousRevision != expectedSourceRevision || replay.SourceGrant != SourceGrant)
                    throw new RuntimeAuthorizationException("runtime_refresh_conflict");
                await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
                return replay;
            }
            if (SourceGrant.Revision != expectedSourceRevision)
                throw new RuntimeAuthorizationException("runtime_refresh_conflict");
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            var previous = Proof();
            var rotated = await _broker.RotateSourceAsync(previous, operationId, cancellationToken)
                .ConfigureAwait(false);
            if (rotated.IsReplay || rotated.Credential is null)
                throw new RuntimeAuthorizationException("runtime_refresh_credential_unavailable");
            try
            {
                if (rotated.Receipt.RegistrationRevision != Registration.Revision ||
                    rotated.Receipt.Audience != previous.Audience)
                    throw new RuntimeAuthorizationException("runtime_grant_registration_mismatch");
                Volatile.Write(ref _source, rotated);
                await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
                var effectiveExpiry = new[]
                {
                    rotated.Credential.ExpiresAt, _modelCredential.ExpiresAt,
                    _actor.Bearer.ExpiresAt, Registration.ExpiresAt
                }.Min();
                _refreshReplay = new(1, Registration.RuntimeInstanceId, operationId,
                    expectedSourceRevision, rotated.Receipt, effectiveExpiry);
                return _refreshReplay;
            }
            catch
            {
                rotated.Credential.Invalidate();
                throw;
            }
            finally
            {
                previous.Credential.Invalidate();
            }
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    public async IAsyncEnumerable<SdkUsageObservation> ReadUsageAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await foreach (var observation in _session.ReadUsageAsync(cancellationToken))
        {
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            yield return observation;
        }
    }

    public async Task<SessionMaterialAcknowledgment> CommitTurnContentAsync(
        RuntimeSessionMaterialHttpClient material, Guid eventId, string role, string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            var receipt = await material.WriteTurnContentAsync(
                this, eventId, role, content, cancellationToken).ConfigureAwait(false);
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    internal RuntimeCredentialProof Proof()
    {
        var source = Volatile.Read(ref _source);
        return new(source.Receipt.GrantId, source.Receipt.RuntimeInstanceId, source.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, source.Receipt.Audience, source.Receipt.ConfigurationHash,
            source.Credential ?? throw new RuntimeAuthorizationException("runtime_source_credential_unavailable"));
    }

    public async Task<SessionMaterialAcknowledgment> CommitNativeCacheAsync(
        RuntimeSessionMaterialHttpClient material, Guid eventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await _session.CaptureNativeCacheAsync(cancellationToken).ConfigureAwait(false);
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            var receipt = await material.WriteCacheAsync(this, eventId, bytes, cancellationToken).ConfigureAwait(false);
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    public async IAsyncEnumerable<RuntimeUsageSourceReceipt> CommitUsageAsync(
        RuntimeUsageSourceHttpClient source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await source.RegisterAsync(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authorityGate.Release();
        }
        await foreach (var observation in _session.ReadUsageAsync(cancellationToken).ConfigureAwait(false))
        {
            RuntimeUsageSourceReceipt receipt;
            await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                RuntimeSessionBootstrap.RequireCurrent(Registration, _actor, _timeProvider);
                if (!Proof().Credential.IsUsable() || !_modelCredential.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_credential_unavailable");
                receipt = await source.AppendAsync(this, observation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _authorityGate.Release();
            }
            yield return receipt;
        }
    }

    internal async Task RegisterUsageAsync(RuntimeUsageSourceHttpClient source, CancellationToken cancellationToken)
    {
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            await source.RegisterAsync(this, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    internal Task FlushUsageAsync(CancellationToken cancellationToken) =>
        _session.FlushUsageAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _authorityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var proof = Proof();
            try
            {
                if (proof.Credential.IsUsable())
                    await _broker.RevokeAsync(proof, Guid.NewGuid(), CancellationToken.None);
            }
            finally
            {
                proof.Credential.Invalidate();
                _modelCredential.Invalidate();
                await _session.DisposeAsync();
            }
        }
        finally
        {
            _authorityGate.Release();
        }
    }
}
