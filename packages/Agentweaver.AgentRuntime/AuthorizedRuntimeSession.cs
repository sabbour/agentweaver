using System.Runtime.CompilerServices;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Receipt confirming a source-grant rotation and the resulting effective session expiry.</summary>
public sealed record RuntimeSessionRefreshReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    Guid OperationId,
    long PreviousRevision,
    RuntimeGrantReceipt SourceGrant,
    DateTimeOffset EffectiveSessionExpiresAt);

/// <summary>
/// Runs the Copilot session only while its registration, source grant, model credential,
/// placement readiness, action authority, and accepted skill projection remain current.
/// </summary>
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
    private readonly Func<CancellationToken, Task>? _requireSkillContent;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _authorityGate = new(1, 1);
    private RuntimeSessionRefreshReceipt? _refreshReplay;
    private RuntimeNativeTurnRecordedReceipt? _lastNativeTurn;
    private RuntimeHostSuspendReceipt? _suspendReceipt;
    private RuntimeHostSuspendRequest? _suspendRequest;
    private int _suspended;
    private int _disposed;

    /// <summary>Creates a configured session with its owner, broker, actor, and model credentials.</summary>
    internal AuthorizedRuntimeSession(
        RuntimeRegistration registration, RuntimeGrantReceipt sourceGrant, SecretCredential sourceCredential,
        RuntimeCopilotSession session, RuntimeModelCredential modelCredential, IRuntimeRegistrationOwner owner,
        RuntimeBrokerCredentialClient broker, RuntimeActorAuthorization actor, TimeProvider timeProvider,
        RuntimeActionHttpClient? actions = null,
        Func<RuntimeRegistration, CancellationToken, Task>? requireReadiness = null,
        Func<CancellationToken, Task>? requireSkillContent = null)
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
        _requireSkillContent = requireSkillContent;
    }

    /// <summary>The immutable runtime registration pinned when the session was configured.</summary>
    public RuntimeRegistration Registration { get; }

    /// <summary>The latest owner-issued source grant after any successful refresh.</summary>
    public RuntimeGrantReceipt SourceGrant => Volatile.Read(ref _source).Receipt;

    /// <summary>The selected native SDK source and model facts.</summary>
    public SdkSessionFacts Facts => _session.Facts;

    /// <summary>The source used to recover the SDK conversation.</summary>
    public RuntimeSessionRecoveryMode RecoveryMode => _session.RecoveryMode;

    /// <summary>Safe reason explaining why recovery did not reuse a compatible native cache.</summary>
    public string? RecoveryReason => _session.RecoveryReason;
    internal RuntimeResumeJournalRebuildReceipt? JournalRebuildReceipt => _session.JournalRebuildReceipt;
    internal SessionMaterialAcknowledgment? ConsumedNativeCacheAcknowledgment =>
        _session.ConsumedNativeCacheAcknowledgment;
    internal RuntimeSessionJournalEvidence? JournalEvidence => _session.JournalEvidence;

    /// <summary>
    /// Sends one bounded user turn after recording user and assistant content, rechecking owner
    /// authority around the model call, and persisting the resulting native cache.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">The turn lacks action authority or the session is stale.</exception>
    public async Task<string> SendTurnAsync(
        string prompt, RuntimeSessionMaterialHttpClient material, CancellationToken cancellationToken,
        Guid? userEventId = null)
    {
        if (Registration.Binding.WorkflowStepId is not null)
            throw new RuntimeAuthorizationException("runtime_native_turn_admission_required");
        if (_actions is null)
            throw new RuntimeAuthorizationException("runtime_action_authority_unavailable");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            RuntimeCopilotSession.HasUnsupportedPromptControls(prompt))
            throw new ArgumentException("A bounded text prompt is required.", nameof(prompt));
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireTurnAdmission();
            _lastNativeTurn = null;
            var turnId = userEventId ?? Guid.NewGuid();
            if (userEventId is { } recordedId)
            {
                if (recordedId == Guid.Empty)
                    throw new ArgumentException("A non-empty A2A message identity is required.", nameof(userEventId));
                await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (await material.ReadOptionalTurnAsync(
                    Registration, Facts, recordedId, cancellationToken)
                    .ConfigureAwait(false) is not null)
                    throw new RuntimeAuthorizationException("runtime_turn_already_recorded");
            }
            RequireSupportedCostTurnAffinity();
            await CommitTurnContentAsync(material, turnId, "user", prompt, cancellationToken)
                .ConfigureAwait(false);
            var coordinatorRoot = Registration.Binding.AgentId == RuntimeActionContract.CoordinatorRootStepId &&
                Registration.Binding.WorkflowStepId is null;
            // Bind a coordinator turn to its persisted generation message for owner admission.
            await _actions.RequireAsync(
                Registration, "model.turn", turnId, System.Text.Encoding.UTF8.GetBytes(prompt),
                RequireCurrentAsync, cancellationToken,
                dispatchId: coordinatorRoot ? turnId : null).ConfigureAwait(false);
            var answer = await _session.SendTurnAsync(prompt, cancellationToken, userEventId).ConfigureAwait(false);
            await CommitTurnContentAsync(material, Guid.NewGuid(), "assistant", answer, cancellationToken).ConfigureAwait(false);
            await CommitNativeCacheAsync(material, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            return answer;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>Rejects hosted Copilot hard-credit limits that cannot be tied to a workflow-step turn.</summary>
    internal void RequireSupportedCostTurnAffinity()
    {
        if (Registration.Binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
            Registration.Binding.CopilotHardCreditLimit is > 0 &&
            Registration.Binding.WorkflowStepId is null)
            throw new RuntimeAuthorizationException("runtime_usage_affinity_unavailable");
    }

    /// <summary>
    /// Persists a zero-credit denial only after confirming no native event appeared since the
    /// captured SDK cursor; returns both the guard receipt and no-send evidence.
    /// </summary>
    internal async Task<RuntimeFixedCreditGuardReadResult> RecordFixedCreditNoSendAsync(
        RuntimeA2ASendRequest request, RuntimeHostSessionProof expectedProof,
        RuntimeUsageSourceHttpClient source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expectedProof);
        ArgumentNullException.ThrowIfNull(source);
        var message = request.Message
            ?? throw new RuntimeAuthorizationException("runtime_a2a_message_invalid");
        var requestHash = RuntimeFixedCreditGuardContract.HashHostRequest(request);
        if (message.MessageId == Guid.Empty ||
            expectedProof.ContractVersion != 1 || expectedProof.Registration != Registration ||
            expectedProof.Purpose != RuntimeCredentialPurpose.Observe ||
            Registration.Binding.ModelSourceMode != ModelSourceMode.HostedCopilot ||
            Registration.Binding.CopilotHardCreditLimit is not 0m)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_invalid");

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RequireTurnAdmission();
                RequireExpectedSourceGrant(expectedProof);
                await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
                var beforeCursor = await _session.CaptureNativeEventCursorAsync(cancellationToken)
                    .ConfigureAwait(false);
                var range = await _session.RequireNoNativeEventsSinceAsync(beforeCursor, cancellationToken)
                    .ConfigureAwait(false);
                if (!range.IsComplete || range.ObservedEventCount != 0 ||
                    range.BeforeCursorHash != range.AfterCursorHash)
                    throw new RuntimeAuthorizationException("runtime_fixed_credit_no_send_invalid");
                await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
                RequireExpectedSourceGrant(expectedProof);

                var grant = SourceGrant;
                var sourceReceipt = new RuntimeSdkSourceReceipt(
                    Registration.RuntimeInstanceId, Registration.Revision, grant.GrantId, Facts,
                    RuntimeUsageSourceReceiptContract.HashSource(Registration, Facts),
                    _timeProvider.GetUtcNow());
                var receipt = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditGuardReceipt(
                    1, RuntimeFixedCreditGuardStage.HostPreQueue, RuntimeFixedCreditGuardCode.HardLimitZero,
                    Registration, RuntimeContractValidation.RegistrationHash(Registration),
                    message.MessageId, requestHash, sourceReceipt.CanonicalPayloadHash,
                    grant.GrantId, grant.Revision, _timeProvider.GetUtcNow(), string.Empty));
                RuntimeFixedCreditGuardContract.ValidateSourceTuple(
                    receipt, Registration, grant, sourceReceipt);

                RuntimeFixedCreditGuardReceipt stored;
                try
                {
                    stored = await source.RecordFixedCreditGuardAsync(
                        this, expectedProof, receipt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (IsOwnerPersistenceUnavailable(failure, cancellationToken))
                {
                    throw new RuntimeAuthorizationException(
                        "runtime_fixed_credit_guard_unavailable", null, failure);
                }
                var evidence = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditNoSendReceipt(
                    1, Registration.RuntimeInstanceId, Registration.Revision,
                    Registration.Binding.ExecutionFence, message.MessageId,
                    RuntimeFixedCreditGuardStage.HostPreQueue, stored.CanonicalPayloadHash,
                    receipt.RegistrationHash, sourceReceipt.CanonicalPayloadHash,
                    grant.GrantId, grant.Revision,
                    RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(Facts.SdkSessionId)),
                    range.BeforeCursorHash, range.AfterCursorHash, range.ObservedEventCount,
                    range.IsComplete, _timeProvider.GetUtcNow(), string.Empty));
                RuntimeFixedCreditGuardContract.ValidateEvidence(
                    evidence, stored, Registration, grant, sourceReceipt);
                RuntimeFixedCreditNoSendReceipt storedEvidence;
                try
                {
                    storedEvidence = await source.RecordFixedCreditNoSendAsync(
                        this, expectedProof, stored, evidence, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception failure) when (IsOwnerPersistenceUnavailable(failure, cancellationToken))
                {
                    throw new RuntimeAuthorizationException(
                        "runtime_fixed_credit_no_send_unavailable",
                        new(stored, null), failure);
                }
                catch (RuntimeAuthorizationException failure)
                {
                    throw new RuntimeAuthorizationException(failure.Code, new(stored, null), failure);
                }
                return new(stored, storedEvidence);
            }
            finally
            {
                _authorityGate.Release();
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static bool IsOwnerPersistenceUnavailable(Exception failure, CancellationToken cancellationToken) =>
        failure is HttpRequestException ||
        failure is OperationCanceledException && !cancellationToken.IsCancellationRequested ||
        failure is RuntimeAuthorizationException { Code: "runtime_owner_unavailable" };

    /// <summary>
    /// Begins a step-bound turn with the owner, records its native SDK events and output,
    /// and commits the returned cache under the same current registration.
    /// </summary>
    internal async Task<(string Response, RuntimeNativeTurnRecordedReceipt Receipt)> SendNativeTurnAsync(
        RuntimeA2ASendRequest request, RuntimeSessionMaterialHttpClient material,
        RuntimeUsageSourceHttpClient source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(source);
        if (_actions is null)
            throw new RuntimeAuthorizationException("runtime_action_authority_unavailable");
        var message = request.Message;
        if (message is null || message.MessageId == Guid.Empty ||
            message.Parts is not [{ Kind: "text", Text: { Length: > 0 } prompt }] ||
            string.IsNullOrWhiteSpace(prompt) || prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            RuntimeCopilotSession.HasUnsupportedPromptControls(prompt))
            throw new RuntimeAuthorizationException("runtime_a2a_message_invalid");
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireTurnAdmission();
            _lastNativeTurn = null;
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (await material.ReadOptionalTurnAsync(
                Registration, Facts, message.MessageId, cancellationToken)
                .ConfigureAwait(false) is not null)
                throw new RuntimeAuthorizationException("runtime_turn_already_recorded");
            await _actions.RequireAsync(Registration, "model.turn", System.Text.Encoding.UTF8.GetBytes(prompt),
                RequireCurrentAsync, cancellationToken, dispatchId: message.MessageId).ConfigureAwait(false);
            var nativeCursor = Registration.Binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
                Registration.Binding.CopilotHardCreditLimit is not null
                    ? await _session.CaptureNativeEventCursorAsync(cancellationToken).ConfigureAwait(false)
                    : null;
            var begin = await source.BeginNativeTurnAsync(this, request, cancellationToken).ConfigureAwait(false);
            if (begin.Denial is { } denial)
            {
                if (nativeCursor is null)
                    throw new RuntimeAuthorizationException("runtime_native_turn_no_send_invalid");
                await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                var range = await _session.RequireNoNativeEventsSinceAsync(nativeCursor, cancellationToken)
                    .ConfigureAwait(false);
                var noSend = RuntimeNativeDenialContract.SealNoSend(new(
                    1, Registration.RuntimeInstanceId, Registration.Revision, message.MessageId,
                    denial.CanonicalPayloadHash, denial.RegistrationHash, denial.SourceHash,
                    RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(Facts.SdkSessionId)),
                    range.BeforeCursorHash, range.AfterCursorHash, range.ObservedEventCount,
                    range.IsComplete, DateTimeOffset.UtcNow, string.Empty));
                await source.RecordNativeTurnNoSendAsync(this, denial, noSend, cancellationToken)
                    .ConfigureAwait(false);
                throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
            }
            var admission = begin.Admission
                ?? throw new RuntimeAuthorizationException("runtime_native_turn_begin_result_invalid");
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            await CommitTurnContentAsync(material, message.MessageId, "user", prompt, cancellationToken)
                .ConfigureAwait(false);
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            var turn = await _session.SendTurnWithNativeReceiptAsync(
                prompt, cancellationToken, message.MessageId).ConfigureAwait(false);
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            var recorded = await source.RecordNativeTurnAsync(this, admission, turn.Receipt, cancellationToken)
                .ConfigureAwait(false);
            await CommitTurnContentAsync(material, Guid.NewGuid(), "assistant", turn.Response, cancellationToken)
                .ConfigureAwait(false);
            await CommitNativeCacheAsync(material, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            _lastNativeTurn = recorded;
            return (turn.Response, recorded);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>Serializes and performs a fresh registration, credential, source, and skill authority check.</summary>
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
        if (_requireSkillContent is not null)
            await _requireSkillContent(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rotates the source credential once for the expected grant revision. An exact operation
    /// replay returns the same receipt; the replaced credential is invalidated after rotation.
    /// </summary>
    public async Task<RuntimeSessionRefreshReceipt> RefreshAsync(
        Guid operationId, long expectedSourceRevision, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || expectedSourceRevision <= 0)
            throw new RuntimeAuthorizationException("runtime_refresh_invalid");
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireTurnAdmission();
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

    /// <summary>Streams SDK usage only after rechecking current authority for each observation.</summary>
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

    /// <summary>Writes one turn message to the Events owner between fresh authority checks.</summary>
    public async Task<SessionMaterialAcknowledgment> CommitTurnContentAsync(
        RuntimeSessionMaterialHttpClient material, Guid eventId, string role, string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireTurnAdmission();
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

    /// <summary>Builds a transient observe proof around the current source credential.</summary>
    internal RuntimeCredentialProof Proof()
    {
        var source = Volatile.Read(ref _source);
        return new(source.Receipt.GrantId, source.Receipt.RuntimeInstanceId, source.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, source.Receipt.Audience, source.Receipt.ConfigurationHash,
            source.Credential ?? throw new RuntimeAuthorizationException("runtime_source_credential_unavailable"));
    }

    /// <summary>Captures the SDK filesystem and persists its cache material with authority checks.</summary>
    public Task<SessionMaterialAcknowledgment> CommitNativeCacheAsync(
        RuntimeSessionMaterialHttpClient material, Guid eventId, CancellationToken cancellationToken) =>
        CommitNativeCacheCoreAsync(material, eventId, cancellationToken, requireCurrentSuspend: null);

    /// <summary>Shared cache writer that can also require current suspend authority at each boundary.</summary>
    private async Task<SessionMaterialAcknowledgment> CommitNativeCacheCoreAsync(
        RuntimeSessionMaterialHttpClient material, Guid eventId, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? requireCurrentSuspend)
    {
        ArgumentNullException.ThrowIfNull(material);
        await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requireCurrentSuspend is null)
                RequireTurnAdmission();
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            if (requireCurrentSuspend is not null)
                await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
            var bytes = await _session.CaptureNativeCacheAsync(cancellationToken).ConfigureAwait(false);
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            if (requireCurrentSuspend is not null)
                await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
            var receipt = await material.WriteCacheAsync(this, eventId, bytes, cancellationToken).ConfigureAwait(false);
            await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
            if (requireCurrentSuspend is not null)
                await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        finally
        {
            _authorityGate.Release();
        }
    }

    /// <summary>
    /// Prevents new turns, waits for the in-flight execution, and creates a durable native-turn
    /// and cache receipt. Exact retries reread the original cache acknowledgment.
    /// </summary>
    internal async Task<RuntimeHostSuspendReceipt> SuspendAsync(
        RuntimeSessionMaterialHttpClient material, RuntimeHostSuspendRequest request,
        Func<CancellationToken, Task> requireCurrentSuspend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requireCurrentSuspend);
        if (request.OperationId == Guid.Empty || request.ManifestId == Guid.Empty || request.PhaseVersion <= 0 ||
            request.OperationId == request.ManifestId)
            throw new RuntimeAuthorizationException("runtime_suspend_request_invalid");
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _authorityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RequireCurrentCoreAsync(cancellationToken).ConfigureAwait(false);
                await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _suspended) != 0 && _suspendRequest != request)
                    throw new RuntimeAuthorizationException("runtime_suspend_operation_conflict");
                _suspendRequest = request;
                Volatile.Write(ref _suspended, 1);
            }
            finally
            {
                _authorityGate.Release();
            }
            if (_suspendReceipt is { } replay)
            {
                if (replay.SourceGrant != SourceGrant)
                    throw new RuntimeAuthorizationException("runtime_suspend_receipt_stale");
                var stored = await material.ReadRecordedAsync(
                    replay.CacheAcknowledgment.Identity, replay.CacheAcknowledgment.EventId,
                    SessionMaterialKind.SdkCache, cancellationToken).ConfigureAwait(false);
                if (stored.Material != replay.CacheAcknowledgment)
                    throw new RuntimeAuthorizationException("runtime_suspend_receipt_stale");
                await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
                await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
                return replay;
            }
            var native = _lastNativeTurn
                ?? throw new RuntimeAuthorizationException("runtime_native_suspend_receipt_unavailable");
            RuntimeNativeTurnContract.ValidateRecorded(native);
            if (native.Admission.Registration != Registration || native.Admission.Source != Facts)
                throw new RuntimeAuthorizationException("runtime_native_suspend_receipt_invalid");
            var cache = await CommitNativeCacheCoreAsync(
                material, request.ManifestId, cancellationToken, requireCurrentSuspend).ConfigureAwait(false);
            await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
            await requireCurrentSuspend(cancellationToken).ConfigureAwait(false);
            _suspendReceipt = new(1, request.OperationId, request.ManifestId, Registration, SourceGrant, native, cache);
            return _suspendReceipt;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    /// <summary>Rejects new work after suspension has begun.</summary>
    private void RequireTurnAdmission()
    {
        if (Volatile.Read(ref _suspended) != 0)
            throw new RuntimeAuthorizationException("runtime_session_suspended");
    }

    /// <summary>Requires a caller proof to identify the current observe grant and registration.</summary>
    private void RequireExpectedSourceGrant(RuntimeHostSessionProof proof)
    {
        var grant = SourceGrant;
        if (proof.Registration != Registration || proof.Purpose != RuntimeCredentialPurpose.Observe ||
            proof.SourceGrantId != grant.GrantId || proof.SourceGrantRevision != grant.Revision)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_source_invalid");
    }

    /// <summary>Registers the SDK source and streams each usage event to the owner under the authority gate.</summary>
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

    /// <summary>Registers the exact SDK source with fresh authority checks before and after the request.</summary>
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

    /// <summary>Waits until all previously emitted usage has passed through the single-reader stream.</summary>
    internal Task FlushUsageAsync(CancellationToken cancellationToken) =>
        _session.FlushUsageAsync(cancellationToken);

    /// <summary>Revokes the current source grant when possible, invalidates credentials, and disposes the SDK session.</summary>
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
