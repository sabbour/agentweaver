using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed record RuntimeAgentHostOptions(
    Uri ConfigureEndpoint,
    Uri BrokerAddress,
    Uri OrchestratorAddress,
    Uri EnvironmentAddress,
    Uri EventsAddress,
    SandboxImageIdentity Image,
    int MaximumPendingTurns)
{
    public RuntimeAgentHostOptions Validate()
    {
        if (!RuntimeContractValidation.IsHttpsEndpoint(ConfigureEndpoint) ||
            ConfigureEndpoint.AbsolutePath != "/runtime/v1/configure" || MaximumPendingTurns <= 0)
            throw new ArgumentException("A versioned HTTPS configure endpoint and positive pending-turn limit are required.");
        RuntimeOwnerHttpTransport.RequireOwnerAddress(BrokerAddress);
        RuntimeOwnerHttpTransport.RequireOwnerAddress(OrchestratorAddress);
        RuntimeOwnerHttpTransport.RequireOwnerAddress(EnvironmentAddress);
        RuntimeOwnerHttpTransport.RequireOwnerAddress(EventsAddress);
        Image.Validate();
        return this;
    }
}

public sealed class RuntimeStartupException(string code, SandboxStartupBudgetFailure? failure = null)
    : Exception(code)
{
    public string Code { get; } = code;
    public SandboxStartupBudgetFailure? Failure { get; } = failure;
}

public sealed class RuntimeAgentHost : IAsyncDisposable
{
    private readonly RuntimeAgentHostOptions _options;
    private readonly HttpClient _http;
    private readonly IRuntimeRegistrationOwner _owner;
    private readonly RuntimeCopilotSessionFactory _sessions;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _configure = new(1, 1);
    private readonly object _queueLock = new();
    private readonly Queue<PendingTurn> _immediate = new();
    private readonly Queue<PendingTurn> _enqueue = new();
    private readonly Dictionary<Guid, PendingTurn> _turns = new();
    private readonly ConcurrentDictionary<Guid, RuntimeUsageCostReceiptReference> _accountedUsage = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _dispatcher;
    private RuntimeBootstrapReceiver? _receiver;
    private RuntimeRegistration? _registration;
    private RuntimeActorAuthorization? _actor;
    private AuthorizedRuntimeSession? _session;
    private RuntimeSessionMaterialHttpClient? _material;
    private RuntimeUsageSourceHttpClient? _usageSource;
    private Task? _usage;
    private RuntimeHostSuspendRequest? _suspendRequest;
    private PendingTurn? _lastTurn;
    private DateTimeOffset? _configuredAt;
    private DateTimeOffset? _readyAt;
    private int _disposed;
    private int _usageFailed;

    public RuntimeAgentHost(
        RuntimeAgentHostOptions options,
        HttpClient http,
        IRuntimeRegistrationOwner owner,
        RuntimeCopilotSessionFactory sessions,
        TimeProvider timeProvider)
    {
        _options = options.Validate();
        _http = http;
        _owner = owner;
        _sessions = sessions;
        _time = timeProvider;
        _dispatcher = DispatchAsync();
    }

    public Uri ConfigureEndpoint => _options.ConfigureEndpoint;

    public async Task<RuntimeBootstrapDeliveryReceipt> ReceiveAsync(
        RuntimeBootstrapDeliveryRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        await _configure.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireAvailable();
            var current = await _owner.ReadCurrentAsync(request.RuntimeInstanceId, actor, token).ConfigureAwait(false);
            RequireRegistration(current, actor);
            if (_registration is not null && current != _registration)
                throw new RuntimeAuthorizationException("runtime_configuration_conflict");
            _registration = current;
            _receiver ??= new(current, _owner, _http, _options.BrokerAddress, _time);
            return await _receiver.ReceiveAsync(request, actor, token).ConfigureAwait(false);
        }
        finally
        {
            _configure.Release();
        }
    }

    public async Task<RuntimeHostReadinessReceipt> ConfigureAsync(
        RuntimeHostConfigureRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        if (request.ContractVersion != 1 || request.Configuration.ValueKind != JsonValueKind.Object ||
            request.Configuration.GetRawText().Length > AddressedMessageValidation.MaximumPayloadLength)
            throw new RuntimeAuthorizationException("runtime_configuration_invalid");
        await _configure.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireAvailable();
            await RequireCallerAsync(request.Registration, actor, token).ConfigureAwait(false);
            var receiver = _receiver ?? throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
            if (_actor is null)
            {
                var lifetime = new[] { actor.Bearer.ExpiresAt, request.Registration.ExpiresAt }.Min();
                _actor = new(new SecretCredential(actor.Bearer.GetValue(), lifetime, _time), actor.TenantSelector);
            }
            var retained = _actor;
            _material ??= new(_http, _options.EventsAddress, retained);
            var skillContent = new RuntimeSkillContentHttpClient(
                _http, _options.OrchestratorAddress, retained);
            var bootstrap = new RuntimeSessionBootstrap(
                _owner, new(_http, _options.BrokerAddress, request.Registration.Binding.ActorIssuer, retained, _time),
                _sessions, skillContent, retained, _time,
                _options.Image, _material, new(_http, _options.OrchestratorAddress, retained),
                async (registration, cancellation) =>
                {
                    await ReadPlacementAsync(registration, retained, cancellation).ConfigureAwait(false);
                });
            var bytes = System.Text.Encoding.UTF8.GetBytes(request.Configuration.GetRawText());
            _session = await receiver.ConfigureAsync(
                bootstrap, bytes, request.ConsumeOperationId, request.ExchangeOperationId, token).ConfigureAwait(false);
            _configuredAt ??= _time.GetUtcNow();
            var source = _usageSource ??= new(_http, _options.OrchestratorAddress, retained);
            if (_usage is null)
            {
                await _session.RegisterUsageAsync(source, token).ConfigureAwait(false);
                _usage = PersistUsageAsync(_session, source, retained);
            }
            await _session.RequireCurrentAsync(token).ConfigureAwait(false);
            _readyAt ??= _time.GetUtcNow();
            return await ReadinessAsync(token).ConfigureAwait(false);
        }
        catch
        {
            if (_receiver?.State != RuntimeBootstrapReceiverState.Ready)
                _readyAt = null;
            throw;
        }
        finally
        {
            _configure.Release();
        }
    }

    public async Task<RuntimeSessionRefreshReceipt> RefreshAsync(
        RuntimeHostRefreshRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var session = await RequireSessionAsync(request.Proof, actor, token, allowRefreshReplay: true)
            .ConfigureAwait(false);
        return await session.RefreshAsync(request.OperationId, request.Proof.SourceGrantRevision, token)
            .ConfigureAwait(false);
    }

    public async Task<RuntimeA2AResponse> SendAsync(
        RuntimeA2ASendRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var message = request.Message;
        if (message is null || message.Kind != "message" || message.Role != "user" ||
            message.MessageId == Guid.Empty || message.Metadata is null ||
            !Enum.IsDefined(message.Metadata.DeliveryMode) || message.Parts.IsDefault ||
            message.Parts.Length != 1 || message.Parts[0] is not { Kind: "text", Text: { Length: > 0 } } part ||
            part.Text.Length > AddressedMessageValidation.MaximumTextLength ||
            RuntimeCopilotSession.HasUnsupportedPromptControls(part.Text))
            throw new RuntimeAuthorizationException("runtime_a2a_message_invalid");
        var session = await RequireSessionAsync(message.Metadata.Runtime, actor, token).ConfigureAwait(false);
        if (message.ContextId != session.Facts.SdkSessionId)
            throw new RuntimeAuthorizationException("runtime_a2a_context_invalid");
        var fingerprint = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(request));
        PendingTurn pending;
        lock (_queueLock)
        {
            RequireAvailable();
            if (_turns.TryGetValue(message.MessageId, out var replay))
            {
                if (replay.Fingerprint != fingerprint)
                    throw new RuntimeAuthorizationException("runtime_a2a_message_conflict");
                pending = replay;
            }
            else
            {
                if (_suspendRequest is not null)
                    throw new RuntimeAuthorizationException("runtime_session_suspended");
                if (_immediate.Count + _enqueue.Count >= _options.MaximumPendingTurns)
                    throw new RuntimeAuthorizationException("runtime_a2a_pending_capacity_exceeded");
                pending = new(message.MessageId, fingerprint, request, message.Metadata.Runtime, actor,
                    token, new(TaskCreationOptions.RunContinuationsAsynchronously));
                _turns.Add(message.MessageId, pending);
                (message.Metadata.DeliveryMode == AddressedMessageDeliveryMode.Immediate
                    ? _immediate : _enqueue).Enqueue(pending);
                _available.Release();
            }
        }
        return await pending.Completion.Task.WaitAsync(token).ConfigureAwait(false);
    }

    public async Task<RuntimeHostSuspendReceipt> SuspendAsync(
        RuntimeHostSuspendRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.ManifestId == Guid.Empty || request.PhaseVersion <= 0 ||
            request.OperationId == request.ManifestId)
            throw new RuntimeAuthorizationException("runtime_suspend_request_invalid");
        var session = await RequireSessionAsync(request.Proof, actor, token).ConfigureAwait(false);
        await RequireCurrentSuspendAsync(request, actor, token).ConfigureAwait(false);
        Task[] admitted;
        lock (_queueLock)
        {
            RequireAvailable();
            if (_suspendRequest is not null && _suspendRequest != request)
                throw new RuntimeAuthorizationException("runtime_suspend_operation_conflict");
            _suspendRequest = request;
            admitted = _turns.Values.Where(turn => !turn.Completion.Task.IsCompleted)
                .Select(turn => (Task)turn.Completion.Task).ToArray();
        }
        await Task.WhenAll(admitted).WaitAsync(token).ConfigureAwait(false);
        lock (_queueLock)
        {
            RequireAvailable();
            if (_lastTurn?.Completion.Task.Status != TaskStatus.RanToCompletion)
                throw new RuntimeAuthorizationException("runtime_native_suspend_receipt_unavailable");
        }
        await RequireCurrentSuspendAsync(request, actor, token).ConfigureAwait(false);
        await session.FlushUsageAsync(token).ConfigureAwait(false);
        RequireAvailable();
        return await session.SuspendAsync(
            _material!, request, cancellation => RequireCurrentSuspendAsync(request, actor, cancellation), token)
            .ConfigureAwait(false);
    }

    private async Task RequireCurrentSuspendAsync(
        RuntimeHostSuspendRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var current = await RuntimeOwnerHttpTransport.SendAsync<RuntimeHostSuspendRequest>(
            _http, _options.OrchestratorAddress, "/internal/runtime/suspend/require-current",
            actor, request, token).ConfigureAwait(false);
        if (current != request)
            throw new RuntimeAuthorizationException("runtime_suspend_authority_changed");
        token.ThrowIfCancellationRequested();
    }

    public async Task<RuntimeHostReadinessReceipt> ReadinessAsync(CancellationToken token)
    {
        RequireAvailable();
        if (_readyAt is null || _session is not { } session || _actor is not { } actor)
            throw new RuntimeAuthorizationException("runtime_host_not_ready");
        await session.RequireCurrentAsync(token).ConfigureAwait(false);
        var phases = await ReadPlacementAsync(session.Registration, actor, token).ConfigureAwait(false);
        RequireAvailable();
        return new(1, session.Registration.RuntimeInstanceId, session.Registration.Revision,
            session.Registration.Binding.ExecutionFence, _options.Image, phases, session.SourceGrant);
    }

    private async Task<AuthorizedRuntimeSession> RequireSessionAsync(
        RuntimeHostSessionProof proof, RuntimeActorAuthorization actor, CancellationToken token,
        bool allowRefreshReplay = false)
    {
        RequireAvailable();
        if (proof is null || proof.ContractVersion != 1 || proof.Purpose != RuntimeCredentialPurpose.Observe ||
            _readyAt is null || _session is not { } session ||
            proof.Registration != session.Registration ||
            proof.SourceGrantId != session.SourceGrant.GrantId ||
            proof.SourceGrantRevision != session.SourceGrant.Revision &&
                !(allowRefreshReplay && proof.SourceGrantRevision == session.SourceGrant.Revision - 1))
            throw new RuntimeAuthorizationException("runtime_session_proof_invalid");
        await RequireCallerAsync(proof.Registration, actor, token).ConfigureAwait(false);
        await session.RequireCurrentAsync(token).ConfigureAwait(false);
        return session;
    }

    private async Task RequireCallerAsync(
        RuntimeRegistration expected, RuntimeActorAuthorization actor, CancellationToken token)
    {
        RequireRegistration(expected, actor);
        if (expected != _registration)
            throw new RuntimeAuthorizationException("runtime_configuration_conflict");
        var current = await _owner.ReadCurrentAsync(expected.RuntimeInstanceId, actor, token).ConfigureAwait(false);
        RequireRegistration(current, actor);
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    private void RequireRegistration(RuntimeRegistration registration, RuntimeActorAuthorization actor)
    {
        RuntimeSessionBootstrap.RequireCurrent(registration, actor, _time);
        if (registration.Binding.ConfigureEndpoint != _options.ConfigureEndpoint ||
            registration.Binding.Image != _options.Image ||
            registration.Binding.PlacementProviderId is null)
            throw new RuntimeAuthorizationException("runtime_image_or_endpoint_mismatch");
    }

    private async Task<ImmutableArray<SandboxStartupPhaseObservation>> ReadPlacementAsync(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var binding = registration.Binding;
        var context = await RuntimeOwnerHttpTransport.SendAsync<EnvironmentRuntimeReadinessContext>(
            _http, _options.EnvironmentAddress,
            $"/internal/projects/{Uri.EscapeDataString(binding.ProjectId)}/runs/{Uri.EscapeDataString(binding.RunId)}" +
            $"/environments/{Uri.EscapeDataString(binding.EnvironmentId)}/coordination/sessions/{Uri.EscapeDataString(binding.SessionId)}" +
            $"/runtime-bootstrap/profiles/{Uri.EscapeDataString(binding.ProfileId)}/readiness",
            actor, null, token).ConfigureAwait(false);
        var placement = context.Placement;
        var observation = context.Observation;
        if (context.ContractVersion != 1 || placement is null || placement.ContractVersion != 1 ||
            placement.TenantId != binding.TenantId || placement.ProjectId != binding.ProjectId ||
            placement.RunId != binding.RunId || placement.EnvironmentId != binding.EnvironmentId ||
            placement.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
            placement.LeaseRevision != binding.EnvironmentLeaseRevision ||
            placement.CurrentFencingGeneration != binding.EnvironmentCurrentFencingGeneration ||
            placement.ProviderFencingGeneration != binding.EnvironmentProviderFencingGeneration ||
            placement.LeaseExpiresAt != registration.ExpiresAt ||
            placement.Resource != new ProviderResourceRef(ProviderSeam.Sandbox, binding.PlacementProviderId!,
                binding.PlacementUid, binding.PlacementGeneration) ||
            placement.Image != _options.Image || placement.ConfigureEndpoint != _options.ConfigureEndpoint ||
            placement.ObservationEndpoint != binding.ObservationEndpoint ||
            placement.ProfileId != binding.ProfileId || placement.LeaseExpiresAt <= _time.GetUtcNow() ||
            observation is null || !Enum.IsDefined(observation.State) || observation.Resource != placement.Resource ||
            observation.FencingGeneration != placement.ProviderFencingGeneration ||
            observation.State is SandboxObservedState.Absent or SandboxObservedState.Finished ||
            observation.State == SandboxObservedState.Failed &&
                observation.StartupFailure?.Phase is not (SandboxStartupPhase.Configured or SandboxStartupPhase.Ready) ||
            !observation.VmIsolationVerified || !observation.WorkspaceAttachmentVerified ||
            observation.VerifiedNetworkGeneration is not > 0 ||
            context.WorkspaceMountPath != _sessions.WorkingDirectory)
            throw new RuntimeAuthorizationException("runtime_placement_not_ready");
        var owner = placement.RuntimeOwnerContext;
        if (owner is null || owner.ContractVersion != 1 || owner.ActorIssuer != binding.ActorIssuer ||
            owner.ActorId != binding.ActorId || owner.TenantId != binding.TenantId ||
            owner.ProjectId != binding.ProjectId || owner.RunId != binding.RunId ||
            owner.SessionId != binding.SessionId || owner.AgentId != binding.AgentId ||
            owner.TurnId != binding.TurnId || owner.ExecutionFence != binding.ExecutionFence ||
            owner.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            owner.ProjectRevision != binding.ProjectRevision ||
            owner.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            owner.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            owner.ContextRevision != binding.ContextRevision ||
            owner.WorkflowStepId != binding.WorkflowStepId ||
            owner.ModelSelectionReference != binding.ModelSelectionReference ||
            owner.ModelSourceMode != binding.ModelSourceMode ||
            owner.ModelBindingPin != binding.ModelBindingPin ||
            owner.ModelCredentialReference != binding.ModelCredentialReference ||
            owner.ModelConnectionId != binding.ModelConnectionId ||
            owner.ModelConnectionScope != binding.ModelConnectionScope ||
            owner.MaxModelTurns != binding.MaxModelTurns ||
            owner.MaxToolCalls != binding.MaxToolCalls ||
            owner.MaxPromptTokens != binding.MaxPromptTokens ||
            owner.MaxRevisionAttempts != binding.MaxRevisionAttempts ||
            owner.CopilotSoftCreditLimit != binding.CopilotSoftCreditLimit ||
            owner.CopilotHardCreditLimit != binding.CopilotHardCreditLimit)
            throw new RuntimeAuthorizationException("runtime_owner_context_stale");
        var phases = observation.StartupPhases;
        if (phases.IsDefault || phases.Length != 3 ||
            phases.Any(phase => phase is null) ||
            phases.Select(phase => phase.Phase).Distinct().Count() != phases.Length ||
            phases.Any(phase => phase.Phase is SandboxStartupPhase.Configured or SandboxStartupPhase.Ready) ||
            !phases.Any(phase => phase.Phase == SandboxStartupPhase.Scheduled) ||
            !phases.Any(phase => phase.Phase == SandboxStartupPhase.Started) ||
            phases.SingleOrDefault(phase => phase.Phase == SandboxStartupPhase.ImageReady) is not { } imageReady ||
            imageReady.ImageDigest != _options.Image.Digest ||
            imageReady.CompressedPullBytes != _options.Image.CompressedPullBytes)
            throw new RuntimeStartupException("runtime_startup_phase_missing_or_image_mismatch");
        if (_configuredAt is { } configured)
            phases = phases.Add(new(SandboxStartupPhase.Configured, 1, configured));
        if (_readyAt is { } ready)
            phases = phases.Add(new(SandboxStartupPhase.Ready, 1, ready));
        SandboxStartupBudgetFailure? failure;
        try
        {
            if (context.StartupBudgets is null)
                throw new ArgumentException("Configured startup time budgets are required.");
            failure = context.StartupBudgets.Evaluate(context.LeaseCreatedAt, phases, _time.GetUtcNow());
        }
        catch (ArgumentException)
        {
            throw new RuntimeStartupException("runtime_startup_evidence_invalid");
        }
        if (failure is not null)
            throw new RuntimeStartupException("runtime_startup_time_budget_exceeded", failure);
        token.ThrowIfCancellationRequested();
        if (!actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_actor_expired");
        return phases.OrderBy(phase => phase.Phase).ToImmutableArray();
    }

    private async Task PersistUsageAsync(
        AuthorizedRuntimeSession session, RuntimeUsageSourceHttpClient source, RuntimeActorAuthorization actor)
    {
        try
        {
            await foreach (var receipt in session.CommitUsageAsync(source, _stop.Token).ConfigureAwait(false))
            {
                var acknowledgment = await RuntimeOwnerHttpTransport.SendAsync<RuntimeUsageAccountingAcknowledgment>(
                    _http, _options.EventsAddress,
                    $"/internal/sessions/{Uri.EscapeDataString(session.Registration.Binding.SessionId)}/usage-receipts",
                    actor, new RuntimeUsageReceiptReferenceRequest(receipt.ReceiptId), _stop.Token).ConfigureAwait(false);
                if (acknowledgment.SourceReceiptId != receipt.ReceiptId ||
                    acknowledgment.Accounting.EventId != receipt.Usage.EventId ||
                    acknowledgment.Accounting.Attribution != receipt.Usage.Attribution)
                    throw new RuntimeAuthorizationException("runtime_usage_accounting_mismatch");
                var reference = new RuntimeUsageCostReceiptReference(receipt.ReceiptId, acknowledgment.Accounting);
                if (!_accountedUsage.TryAdd(receipt.Usage.EventId, reference) &&
                    _accountedUsage[receipt.Usage.EventId] != reference)
                    throw new RuntimeAuthorizationException("runtime_usage_accounting_mismatch");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            Volatile.Write(ref _usageFailed, 1);
            await _stop.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_stop.Token).ConfigureAwait(false);
                PendingTurn pending;
                lock (_queueLock)
                {
                    pending = _immediate.Count > 0 ? _immediate.Dequeue() : _enqueue.Dequeue();
                    _lastTurn = pending;
                }
                using var turn = CancellationTokenSource.CreateLinkedTokenSource(pending.Cancellation, _stop.Token);
                try
                {
                    turn.Token.ThrowIfCancellationRequested();
                    var session = await RequireSessionAsync(pending.Proof, pending.Actor, turn.Token).ConfigureAwait(false);
                    string answer;
                    RuntimeNativeTurnRecordedReceipt? recorded = null;
                    if (session.Registration.Binding.WorkflowStepId is not null)
                        (answer, recorded) = await session.SendNativeTurnAsync(
                            pending.Request, _material!, _usageSource!, turn.Token).ConfigureAwait(false);
                    else
                        answer = await session.SendTurnAsync(
                            pending.Request.Message.Parts[0].Text, _material!, turn.Token, pending.MessageId)
                            .ConfigureAwait(false);
                    await session.FlushUsageAsync(turn.Token).ConfigureAwait(false);
                    RequireAvailable();
                    if (recorded is not null)
                    {
                        var references = recorded.Observation.UsageEventIds.Select(nativeId =>
                        {
                            var eventId = SdkUsageIdentity.Create(
                                session.Facts.RuntimeInstanceId, session.Facts.SdkSessionId, nativeId.ToString("D"));
                            return _accountedUsage.TryGetValue(eventId, out var reference)
                                ? reference : throw new RuntimeAuthorizationException("runtime_usage_accounting_pending");
                        }).ToImmutableArray();
                        await _usageSource!.CompleteNativeTurnAsync(session, recorded, references, turn.Token)
                            .ConfigureAwait(false);
                    }
                    pending.Completion.TrySetResult(new(
                        "message", Guid.NewGuid(), session.Facts.SdkSessionId, "agent", [new("text", answer)]));
                }
                catch (OperationCanceledException) when (turn.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _usageFailed) != 0)
                        pending.Completion.TrySetException(new RuntimeAuthorizationException("runtime_usage_persistence_failed"));
                    else
                        pending.Completion.TrySetCanceled(turn.Token);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    pending.Completion.TrySetException(failure);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            lock (_queueLock)
                foreach (var pending in _immediate.Concat(_enqueue))
                    pending.Completion.TrySetException(new RuntimeAuthorizationException("runtime_host_unavailable"));
        }
    }

    private void RequireAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _usageFailed) != 0 || _stop.IsCancellationRequested)
            throw new RuntimeAuthorizationException("runtime_usage_persistence_failed");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _dispatcher.ConfigureAwait(false);
        await _configure.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                if (_receiver is not null)
                    await _receiver.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _actor?.Bearer.Invalidate();
                if (_usage is not null)
                    await _usage.ConfigureAwait(false);
            }
        }
        finally
        {
            _configure.Release();
            _stop.Dispose();
        }
    }

    private sealed record PendingTurn(
        Guid MessageId, string Fingerprint, RuntimeA2ASendRequest Request, RuntimeHostSessionProof Proof,
        RuntimeActorAuthorization Actor, CancellationToken Cancellation, TaskCompletionSource<RuntimeA2AResponse> Completion);
}
