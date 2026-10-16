using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>Network, image, and queue limits that bind one runtime host to its deployment.</summary>
public sealed record RuntimeAgentHostOptions(
    Uri ConfigureEndpoint,
    Uri BrokerAddress,
    Uri OrchestratorAddress,
    Uri EnvironmentAddress,
    Uri EventsAddress,
    SandboxImageIdentity Image,
    int MaximumPendingTurns)
{
    /// <summary>Validates endpoint audiences, image identity, and bounded turn capacity.</summary>
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

/// <summary>Reports a startup failure with a stable code and optional failed budget evidence.</summary>
public sealed class RuntimeStartupException(string code, SandboxStartupBudgetFailure? failure = null)
    : Exception(code)
{
    /// <summary>The stable failure code returned by the host endpoint.</summary>
    public string Code { get; } = code;

    /// <summary>The startup budget that failed, when one was measured.</summary>
    public SandboxStartupBudgetFailure? Failure { get; } = failure;
}

/// <summary>
/// Owns one runtime registration, receives bootstrap configuration, serializes and dispatches
/// A2A turns, and serves readiness, refresh, suspend, and resume operations.
/// </summary>
public sealed class RuntimeAgentHost : IAsyncDisposable
{
    private readonly RuntimeAgentHostOptions _options;
    private readonly HttpClient _http;
    private readonly IRuntimeRegistrationOwner _owner;
    private readonly RuntimeCopilotSessionFactory _sessions;
    private readonly Func<string, WorkspaceVolumeFlushRequest, CancellationToken,
        Task<WorkspaceVolumeFlushReceipt>> _flushWorkspace;
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
    private CapturedMcpServer? _capturedMcpServer;
    private RuntimeSessionMaterialHttpClient? _material;
    private RuntimeUsageSourceHttpClient? _usageSource;
    private Task? _usage;
    private RuntimeHostSuspendRequest? _suspendRequest;
    private SessionMaterialAcknowledgment? _configurationAcknowledgment;
    private RuntimeHostResumeCacheReceipt? _resumeCacheReceipt;
    private RuntimeHostRecoveryReceipt? _recoveryReceipt;
    private PendingTurn? _lastTurn;
    private DateTimeOffset? _configuredAt;
    private DateTimeOffset? _readyAt;
    private int _disposed;
    private int _usageFailed;

    /// <summary>Creates a host with its owner, session factory, network client, and clock.</summary>
    public RuntimeAgentHost(
        RuntimeAgentHostOptions options,
        HttpClient http,
        IRuntimeRegistrationOwner owner,
        RuntimeCopilotSessionFactory sessions,
        TimeProvider timeProvider)
        : this(options, http, owner, sessions, timeProvider, MountedWorkspaceFlush.FlushAndCreateReceiptAsync)
    {
    }

    /// <summary>Creates a host with an injectable workspace flusher for controlled execution.</summary>
    internal RuntimeAgentHost(
        RuntimeAgentHostOptions options,
        HttpClient http,
        IRuntimeRegistrationOwner owner,
        RuntimeCopilotSessionFactory sessions,
        TimeProvider timeProvider,
        Func<string, WorkspaceVolumeFlushRequest, CancellationToken, Task<WorkspaceVolumeFlushReceipt>> flushWorkspace)
    {
        _options = options.Validate();
        _http = http;
        _owner = owner;
        _sessions = sessions;
        _flushWorkspace = flushWorkspace ?? throw new ArgumentNullException(nameof(flushWorkspace));
        _time = timeProvider;
        _dispatcher = DispatchAsync();
    }

    /// <summary>The configured HTTPS audience for bootstrap and activation requests.</summary>
    public Uri ConfigureEndpoint => _options.ConfigureEndpoint;

    /// <summary>Accepts a matching bootstrap delivery and stores its configuration for activation.</summary>
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

    /// <summary>
    /// Validates bootstrap or resume authority, creates the authorized SDK session, persists
    /// its configuration acknowledgment, and returns placement readiness.
    /// </summary>
    public async Task<RuntimeHostReadinessReceipt> ConfigureAsync(
        RuntimeHostConfigureRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var resumeBootstrap = request.ResumeBootstrap;
        if (request.ContractVersion != 1)
            throw new RuntimeAuthorizationException("runtime_configuration_invalid");
        byte[] configurationBytes;
        if (resumeBootstrap is null)
        {
            if (request.Configuration.ValueKind != JsonValueKind.Object ||
                request.Configuration.GetRawText().Length > AddressedMessageValidation.MaximumPayloadLength)
                throw new RuntimeAuthorizationException("runtime_configuration_invalid");
            configurationBytes = System.Text.Encoding.UTF8.GetBytes(request.Configuration.GetRawText());
        }
        else
        {
            resumeBootstrap.Validate();
            if (request.Configuration.ValueKind != JsonValueKind.Null ||
                request.Registration != resumeBootstrap.Registration ||
                request.ConsumeOperationId != resumeBootstrap.ConsumeOperationId ||
                request.ExchangeOperationId != resumeBootstrap.ExchangeOperationId)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_invalid");
            configurationBytes = [];
        }
        await _configure.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireAvailable();
            await RequireCallerAsync(request.Registration, actor, token).ConfigureAwait(false);
            RuntimeContractValidation.RequireSupportedNumericRunLimits(request.Registration.Binding);
            var receiver = _receiver ?? throw new RuntimeAuthorizationException("runtime_delivery_unavailable");
            if (_actor is null)
            {
                var lifetime = new[] { actor.Bearer.ExpiresAt, request.Registration.ExpiresAt }.Min();
                _actor = new(new SecretCredential(actor.Bearer.GetValue(), lifetime, _time), actor.TenantSelector);
            }
            var retained = _actor;
            _material ??= new(_http, _options.EventsAddress, retained);
            if (resumeBootstrap is not null)
            {
                await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
                var savedConfiguration = await new RuntimeResumeConfigurationHttpClient(
                    _http, _options.EventsAddress).ReadAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
                await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
                configurationBytes = savedConfiguration.Bytes;
            }
            var skillContent = new RuntimeSkillContentHttpClient(
                _http, _options.OrchestratorAddress, retained);
            await ReadPlacementAsync(request.Registration, retained, token).ConfigureAwait(false);
            var capturedMcpServer = Volatile.Read(ref _capturedMcpServer)
                ?? throw new RuntimeAuthorizationException("runtime_mcp_server_binding_invalid");
            if (request.McpServer is { IsValid: false } ||
                request.McpServer != capturedMcpServer.Endpoint)
                throw new RuntimeAuthorizationException("runtime_mcp_server_binding_invalid");
            var bootstrap = new RuntimeSessionBootstrap(
                _owner, new(_http, _options.BrokerAddress, request.Registration.Binding.ActorIssuer, retained, _time),
                _sessions, skillContent, retained, _time,
                _options.Image, _material, new(_http, _options.OrchestratorAddress, retained),
                async (registration, cancellation) =>
                {
                    if (resumeBootstrap is not null)
                        await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, cancellation)
                            .ConfigureAwait(false);
                    await ReadPlacementAsync(registration, retained, cancellation).ConfigureAwait(false);
                    if (resumeBootstrap is not null)
                        await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, cancellation)
                            .ConfigureAwait(false);
                }, capturedMcpServer: capturedMcpServer.Endpoint);
            _session = await receiver.ConfigureAsync(
                bootstrap, configurationBytes, request.ConsumeOperationId, request.ExchangeOperationId,
                token, resumeBootstrap).ConfigureAwait(false);
            var configuredSession = _session
                ?? throw new RuntimeAuthorizationException("runtime_session_unavailable");
            _resumeCacheReceipt = null;
            _recoveryReceipt = null;
            if (resumeBootstrap is not null)
            {
                await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
                _resumeCacheReceipt = new RuntimeHostResumeCacheReceipt(
                    1,
                    resumeBootstrap.Resume.OperationId,
                    resumeBootstrap.Resume.SourceManifest.ManifestId,
                    configuredSession.Registration,
                    resumeBootstrap.Resume.SourceManifest.CacheAcknowledgment
                        ?? throw new RuntimeAuthorizationException("runtime_resume_cache_unavailable"),
                    configuredSession.Facts,
                    configuredSession.RecoveryMode switch
                    {
                        RuntimeSessionRecoveryMode.NativeCache => "native-cache",
                        RuntimeSessionRecoveryMode.JournalRebuild => "journal-rebuild",
                        _ => throw new RuntimeAuthorizationException("runtime_resume_cache_unavailable")
                    })
                {
                    JournalRebuild = configuredSession.JournalRebuildReceipt
                }.ValidateFor(resumeBootstrap);
            }
            else
            {
                _recoveryReceipt = CreateRecoveryReceipt(configuredSession);
            }
            _configuredAt ??= _time.GetUtcNow();
            var source = _usageSource ??= new(_http, _options.OrchestratorAddress, retained);
            if (_usage is null)
            {
                try
                {
                    await configuredSession.RegisterUsageAsync(source, token).ConfigureAwait(false);
                    if (configuredSession.Registration.Binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
                        configuredSession.Registration.Binding.CopilotHardCreditLimit is > 0)
                        await new RuntimeUsageCostPreflightHttpClient(
                            _http, _options.EventsAddress, retained)
                            .EnsurePricedAsync(configuredSession, token).ConfigureAwait(false);
                    await configuredSession.RequireCurrentAsync(token).ConfigureAwait(false);
                    configuredSession.RequireSupportedCostTurnAffinity();
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                {
                    var prepared = _receiver;
                    _receiver = null;
                    _session = null;
                    _readyAt = null;
                    if (prepared is not null)
                    {
                        try
                        {
                            await prepared.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException)
                        {
                            throw new AggregateException(
                                "Runtime preparation failed and its prepared session could not be cleaned up.",
                                failure, cleanupFailure);
                        }
                    }
                    throw;
                }
                _usage = PersistUsageAsync(configuredSession, source, retained);
            }
            await configuredSession.RequireCurrentAsync(token).ConfigureAwait(false);
            if (resumeBootstrap is not null)
                await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
            var configurationAcknowledgment = await _material.WriteRuntimeConfigurationAsync(
                configuredSession, request.ConsumeOperationId, configurationBytes, token).ConfigureAwait(false);
            if (configurationAcknowledgment.EventId != request.ConsumeOperationId ||
                _configurationAcknowledgment is { } previousConfiguration &&
                    !RuntimeWorkspaceResumeHttpClient.SameJson(previousConfiguration, configurationAcknowledgment))
                throw new RuntimeAuthorizationException("runtime_configuration_acknowledgment_conflict");
            _configurationAcknowledgment = configurationAcknowledgment;
            await configuredSession.RequireCurrentAsync(token).ConfigureAwait(false);
            if (resumeBootstrap is not null)
                await RequireCurrentResumeBootstrapAsync(resumeBootstrap, retained, token).ConfigureAwait(false);
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

    /// <summary>Rotates the current observation credential for the supplied session proof.</summary>
    public async Task<RuntimeSessionRefreshReceipt> RefreshAsync(
        RuntimeHostRefreshRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var session = await RequireSessionAsync(request.Proof, actor, token, allowRefreshReplay: true)
            .ConfigureAwait(false);
        return await session.RefreshAsync(request.OperationId, request.Proof.SourceGrantRevision, token)
            .ConfigureAwait(false);
    }

    /// <summary>Reads host pre-queue credit evidence after validating the session proof.</summary>
    internal async Task<RuntimeFixedCreditGuardReadResult> ReadFixedCreditGuardAsync(
        RuntimeHostFixedCreditGuardReadRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var proof = request.Proof
            ?? throw new RuntimeAuthorizationException("runtime_session_proof_invalid");
        var session = await RequireSessionAsync(proof, actor, token).ConfigureAwait(false);
        var source = _usageSource
            ?? throw new RuntimeAuthorizationException("runtime_usage_source_unavailable");
        RuntimeFixedCreditGuardReadResult result;
        try
        {
            result = await source.ReadFixedCreditGuardAsync(
                session, request.RequestId, request.RequestHash, token).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException failure) when (failure.Code == "runtime_owner_unavailable")
        {
            throw new RuntimeAuthorizationException("runtime_usage_source_unavailable");
        }
        await RequireSessionAsync(proof, actor, token).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reads native denial evidence for a dispatch and denial stage.</summary>
    internal async Task<RuntimeNativeCreditDenialReadResult> ReadNativeCreditDenialAsync(
        RuntimeHostNativeCreditDenialReadRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var proof = request.Proof
            ?? throw new RuntimeAuthorizationException("runtime_session_proof_invalid");
        var session = await RequireSessionAsync(proof, actor, token).ConfigureAwait(false);
        var source = _usageSource
            ?? throw new RuntimeAuthorizationException("runtime_usage_source_unavailable");
        RuntimeNativeCreditDenialReadResult result;
        try
        {
            result = await source.ReadNativeCreditDenialAsync(
                session, request.DispatchId, request.Stage, token).ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException failure) when (failure.Code == "runtime_owner_unavailable")
        {
            throw new RuntimeAuthorizationException("runtime_usage_source_unavailable");
        }
        await RequireSessionAsync(proof, actor, token).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Validates and admits one user A2A message, deduplicates exact retries by message ID,
    /// then waits for the serialized native turn response.
    /// </summary>
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
        var fingerprint = RuntimeFixedCreditGuardContract.HashHostRequest(request);
        if (session.Registration.Binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
            session.Registration.Binding.CopilotHardCreditLimit is 0)
        {
            RuntimeFixedCreditGuardReadResult? fixedCreditGuard = null;
            if (_usageSource is { } source)
                fixedCreditGuard = await session.RecordFixedCreditNoSendAsync(
                    request, message.Metadata.Runtime, source, token).ConfigureAwait(false);
            throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted", fixedCreditGuard, null);
        }
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

    /// <summary>
    /// Stops turn admission, drains admitted work, flushes usage and native state, and flushes
    /// the mounted workspace before returning suspend evidence.
    /// </summary>
    public async Task<RuntimeHostSuspendReceipt> SuspendAsync(
        RuntimeHostSuspendRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.ManifestId == Guid.Empty || request.PhaseVersion <= 0 ||
            request.OperationId == request.ManifestId)
            throw new RuntimeAuthorizationException("runtime_suspend_request_invalid");
        var workspaceFlush = request.WorkspaceFlush
            ?? throw new RuntimeAuthorizationException("runtime_workspace_flush_request_missing");
        workspaceFlush.Validate();
        var proof = request.Proof;
        if (proof is null)
            throw new RuntimeAuthorizationException("runtime_suspend_request_invalid");
        var session = await RequireSessionAsync(proof, actor, token).ConfigureAwait(false);
        await RequireCurrentSuspendAsync(request, actor, token).ConfigureAwait(false);
        var binding = session.Registration.Binding;
        var owner = workspaceFlush.EnvironmentFence.Owner;
        if (owner.TenantId != binding.TenantId ||
            owner.ProjectId != binding.ProjectId ||
            owner.RunId != binding.RunId ||
            owner.EnvironmentId != binding.EnvironmentId ||
            workspaceFlush.EnvironmentFence.LifecycleGeneration != binding.EnvironmentLifecycleGeneration ||
            workspaceFlush.Spec.ProjectId != binding.ProjectId ||
            workspaceFlush.Spec.EnvironmentId != binding.EnvironmentId &&
                (workspaceFlush.Spec.BindingMode != WorkspaceVolumeBindingMode.Shared ||
                 !workspaceFlush.Spec.AuthorizedEnvironmentIds.Contains(
                      binding.EnvironmentId, StringComparer.Ordinal)))
            throw new RuntimeAuthorizationException("runtime_workspace_flush_scope_invalid");
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
        var receipt = await session.SuspendAsync(
            _material!, request, cancellation => RequireCurrentSuspendAsync(request, actor, cancellation), token)
            .ConfigureAwait(false);
        var flushReceipt = await _flushWorkspace(
            _sessions.WorkingDirectory, workspaceFlush, token).ConfigureAwait(false);
        await RequireCurrentSuspendAsync(request, actor, token).ConfigureAwait(false);
        var completed = receipt with
        {
            WorkspaceFlush = flushReceipt,
            ConfigurationAcknowledgment = _configurationAcknowledgment
                ?? throw new RuntimeAuthorizationException("runtime_configuration_acknowledgment_unavailable")
        };
        RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(completed);
        return completed;
    }

    /// <summary>Hashes the restored workspace and returns evidence bound to the resume request.</summary>
    public async Task<RuntimeHostWorkspaceResumeReceipt> VerifyWorkspaceResumeAsync(
        RuntimeWorkspaceResumeRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        request.Validate();
        if (request.RestoredWorkspace is null)
            throw new RuntimeAuthorizationException("runtime_resume_verify_workspace_required");

        await RequireCurrentResumeAsync(request, actor, token).ConfigureAwait(false);
        var treeHash = await MountedWorkspaceFlush.ComputeTreeSha256Async(
            _sessions.WorkingDirectory, token).ConfigureAwait(false);
        await RequireCurrentResumeAsync(request, actor, token).ConfigureAwait(false);
        return new RuntimeHostWorkspaceResumeReceipt(
            1,
            request.OperationId,
            request.SourceManifest.ManifestId,
            request.RestoredWorkspace,
            treeHash).ValidateFor(request);
    }

    /// <summary>Reports whether the requested resume bootstrap is absent, present, or activated.</summary>
    public async Task<RuntimeResumeHostStateReceipt> ReadResumeBootstrapStateAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        request.Validate();
        await _configure.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireAvailable();
            await RequireCurrentResumeBootstrapAsync(request, actor, token).ConfigureAwait(false);
            await RequireCurrentResumeRegistrationAsync(request, actor, token).ConfigureAwait(false);
            var state = ReadResumeBootstrapState(request);
            await RequireCurrentResumeBootstrapAsync(request, actor, token).ConfigureAwait(false);
            await RequireCurrentResumeRegistrationAsync(request, actor, token).ConfigureAwait(false);
            return new RuntimeResumeHostStateReceipt(
                1,
                request.Resume.OperationId,
                request.Resume.SourceManifest.ManifestId,
                request.Registration,
                request.ResumeAttemptId,
                state).ValidateFor(request);
        }
        finally
        {
            _configure.Release();
        }
    }

    /// <summary>Requires the resume registration to still be the owner's exact current registration.</summary>
    private async Task RequireCurrentResumeRegistrationAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var current = await _owner.ReadCurrentAsync(
            request.Registration.RuntimeInstanceId, actor, token).ConfigureAwait(false);
        RequireRegistration(current, actor);
        if (current != request.Registration)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>Classifies local bootstrap state and rejects inconsistent partially initialized state.</summary>
    private RuntimeResumeHostState ReadResumeBootstrapState(RuntimeResumeBootstrapRequest request)
    {
        if (_registration is null || _registration != request.Registration)
            return RuntimeResumeHostState.Missing;
        if (_receiver is null)
        {
            if (_session is null && _resumeCacheReceipt is null && _configurationAcknowledgment is null)
                return RuntimeResumeHostState.Missing;
            throw new RuntimeAuthorizationException("runtime_resume_host_state_unavailable");
        }

        switch (_receiver.State)
        {
            case RuntimeBootstrapReceiverState.Empty:
                if (_session is null && _resumeCacheReceipt is null && _configurationAcknowledgment is null)
                    return RuntimeResumeHostState.Missing;
                throw new RuntimeAuthorizationException("runtime_resume_host_state_unavailable");
            case RuntimeBootstrapReceiverState.Pending:
            case RuntimeBootstrapReceiverState.Configuring:
                return RuntimeResumeHostState.BootstrapPresent;
            case RuntimeBootstrapReceiverState.Ready:
                if (_session is { } session &&
                    session.Registration == request.Registration &&
                    _resumeCacheReceipt is { } cache &&
                    cache.OperationId == request.Resume.OperationId &&
                    cache.SourceManifestId == request.Resume.SourceManifest.ManifestId &&
                    cache.Registration == request.Registration)
                {
                    cache.ValidateFor(request);
                    return RuntimeResumeHostState.Activated;
                }
                return RuntimeResumeHostState.BootstrapPresent;
            default:
                throw new RuntimeAuthorizationException("runtime_resume_host_state_unavailable");
        }
    }

    /// <summary>Rechecks suspend authority with the owner and rejects any changed request.</summary>
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

    /// <summary>Rechecks workspace-resume authority with the owner before accepting workspace evidence.</summary>
    private async Task RequireCurrentResumeAsync(
        RuntimeWorkspaceResumeRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var current = await RuntimeOwnerHttpTransport.SendAsync<RuntimeWorkspaceResumeRequest>(
            _http, _options.OrchestratorAddress, "/internal/runtime/resume/require-current",
            actor, request, token).ConfigureAwait(false);
        if (current != request)
            throw new RuntimeAuthorizationException("runtime_resume_authority_changed");
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Rechecks resume-bootstrap authority by exact serialized request identity.</summary>
    private async Task RequireCurrentResumeBootstrapAsync(
        RuntimeResumeBootstrapRequest request, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var current = await RuntimeOwnerHttpTransport.SendAsync<RuntimeResumeBootstrapRequest>(
            _http, _options.OrchestratorAddress, "/internal/runtime/resume/require-bootstrap-current",
            actor, request, token).ConfigureAwait(false);
        if (!RuntimeWorkspaceResumeHttpClient.SameJson(current, request))
            throw new RuntimeAuthorizationException("runtime_resume_authority_changed");
        token.ThrowIfCancellationRequested();
    }

    /// <summary>Returns readiness using the actor captured during configuration.</summary>
    public Task<RuntimeHostReadinessReceipt> ReadinessAsync(CancellationToken token) =>
        ReadinessAsyncCore(null, token);

    /// <summary>Returns readiness after verifying the caller against the current registration.</summary>
    public Task<RuntimeHostReadinessReceipt> ReadinessAsync(
        RuntimeActorAuthorization actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ReadinessAsyncCore(actor, token);
    }

    /// <summary>Builds readiness from the current registration and placement observations.</summary>
    private async Task<RuntimeHostReadinessReceipt> ReadinessAsyncCore(
        RuntimeActorAuthorization? requestActor, CancellationToken token)
    {
        RequireAvailable();
        if (_readyAt is null || _session is not { } session || _actor is not { } configuredActor)
            throw new RuntimeAuthorizationException("runtime_host_not_ready");
        ValidateConfigurationAcknowledgment(
            _configurationAcknowledgment
                ?? throw new RuntimeAuthorizationException("runtime_configuration_acknowledgment_unavailable"),
            session);
        var actor = requestActor ?? configuredActor;
        if (requestActor is not null)
            await RequireCallerAsync(session.Registration, requestActor, token).ConfigureAwait(false);
        await session.RequireCurrentAsync(token).ConfigureAwait(false);
        var phases = await ReadPlacementAsync(session.Registration, actor, token).ConfigureAwait(false);
        RequireAvailable();
        return new(1, session.Registration.RuntimeInstanceId, session.Registration.Revision,
            session.Registration.Binding.ExecutionFence, _options.Image, phases, session.SourceGrant)
        {
            ResumeCache = _resumeCacheReceipt,
            Recovery = _recoveryReceipt
        };
    }

    /// <summary>Maps session recovery mode and evidence into a registration-bound receipt.</summary>
    private static RuntimeHostRecoveryReceipt CreateRecoveryReceipt(AuthorizedRuntimeSession session)
    {
        var recoveryMode = session.RecoveryMode switch
        {
            RuntimeSessionRecoveryMode.Fresh => RuntimeHostRecoveryMode.Fresh,
            RuntimeSessionRecoveryMode.NativeCache => RuntimeHostRecoveryMode.NativeCache,
            RuntimeSessionRecoveryMode.JournalRebuild => RuntimeHostRecoveryMode.JournalRebuild,
            _ => throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid")
        };
        return new RuntimeHostRecoveryReceipt(
            1,
            session.Registration.RuntimeInstanceId,
            session.Registration.Revision,
            session.Registration.Binding.ExecutionFence,
            recoveryMode,
            session.Facts)
        {
            NativeCacheAcknowledgment = session.ConsumedNativeCacheAcknowledgment,
            Journal = session.JournalEvidence is { } journal
                ? new RuntimeHostJournalRecoveryEvidence(journal.ThroughPosition, journal.JournalSha256)
                : null
        }.ValidateFor(session.Registration);
    }

    /// <summary>Checks that persisted configuration matches the registration and SDK facts.</summary>
    private static void ValidateConfigurationAcknowledgment(
        SessionMaterialAcknowledgment acknowledgment, AuthorizedRuntimeSession session)
    {
        SessionMaterialValidation.Validate(acknowledgment.Reference);
        var registration = session.Registration;
        var binding = registration.Binding;
        var material = acknowledgment.Reference.Material!;
        if (acknowledgment.ContractVersion != 1 || acknowledgment.EventId == Guid.Empty ||
            acknowledgment.Position <= 0 ||
            acknowledgment.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            material.Kind != SessionMaterialKind.RuntimeConfiguration ||
            material.RuntimeInstanceId != registration.RuntimeInstanceId ||
            material.RegistrationRevision != registration.Revision ||
            material.ExecutionFence != binding.ExecutionFence ||
            material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            material.TenantId != binding.TenantId ||
            material.Sha256 != session.SourceGrant.ConfigurationHash ||
            material.SdkVersion != session.Facts.SdkVersion ||
            material.RuntimeVersion != session.Facts.RuntimeVersion ||
            material.ModelSelectionReference != session.Facts.ModelSelectionReference ||
            material.ModelId != session.Facts.ModelId ||
            material.MaxPromptTokens != session.Facts.MaxPromptTokens)
            throw new RuntimeAuthorizationException("runtime_configuration_acknowledgment_invalid");
    }

    /// <summary>Validates the observe proof, caller, and current source grant before returning the session.</summary>
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

    /// <summary>Requires the caller and owner registration to still match this host.</summary>
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

    /// <summary>Checks registration freshness, actor authority, image identity, and configure audience.</summary>
    private void RequireRegistration(RuntimeRegistration registration, RuntimeActorAuthorization actor)
    {
        RuntimeSessionBootstrap.RequireCurrent(registration, actor, _time);
        if (registration.Binding.ConfigureEndpoint != _options.ConfigureEndpoint ||
            registration.Binding.Image != _options.Image ||
            registration.Binding.PlacementProviderId is null)
            throw new RuntimeAuthorizationException("runtime_image_or_endpoint_mismatch");
    }

    /// <summary>
    /// Reads placement and startup evidence, compares every pinned binding, and enforces
    /// startup time budgets before returning phase observations.
    /// </summary>
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
            placement.McpServer is { IsValid: false } ||
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
        var candidate = new CapturedMcpServer(placement.McpServer);
        var capturedMcpServer = Interlocked.CompareExchange(ref _capturedMcpServer, candidate, null);
        if (capturedMcpServer is not null && capturedMcpServer.Endpoint != placement.McpServer)
            throw new RuntimeAuthorizationException("runtime_mcp_server_binding_changed");
        return phases.OrderBy(phase => phase.Phase).ToImmutableArray();
    }

    private sealed record CapturedMcpServer(RuntimeMcpServerEndpoint? Endpoint);

    /// <summary>Persists SDK usage receipts and stops the stream on any owner mismatch.</summary>
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

    /// <summary>Processes immediate turns before queued turns and completes each admitted request once.</summary>
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

    /// <summary>Rejects use after disposal or after usage persistence has failed.</summary>
    private void RequireAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _usageFailed) != 0 || _stop.IsCancellationRequested)
            throw new RuntimeAuthorizationException("runtime_usage_persistence_failed");
    }

    /// <summary>Stops dispatch and usage processing, then disposes the receiver and active session.</summary>
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
