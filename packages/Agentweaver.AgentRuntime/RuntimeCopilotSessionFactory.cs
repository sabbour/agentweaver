using System.Reflection;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Resolves an accepted model binding and creates a native Copilot SDK session with
/// approved tools, skills, filesystem, and optional recovery state.
/// </summary>
public sealed class RuntimeCopilotSessionFactory
{
    private readonly RuntimeConnection _connection;
    private readonly string _baseDirectory;
    private readonly RuntimeModelBindingsResolver _modelBindings;
    private readonly TimeSpan _abortDrainTimeout;
    private readonly string _workingDirectory;
    private readonly string? _expectedRuntimeVersion;
    private readonly TimeProvider _timeProvider;

    /// <summary>The absolute workspace directory exposed to the SDK session.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>The model-binding revision that scopes hosted capability publications.</summary>
    public string ModelBindingsRevision => _modelBindings.ConfigurationRevision;

    /// <summary>Creates a factory using the supplied legacy model-binding map.</summary>
    public RuntimeCopilotSessionFactory(
        RuntimeConnection approvedConnection,
        string baseDirectory,
        IReadOnlyDictionary<string, RuntimeModelBinding> approvedModelBindings,
        TimeSpan? abortDrainTimeout = null,
        string? workingDirectory = null,
        string? expectedRuntimeVersion = null,
        TimeProvider? timeProvider = null)
        : this(approvedConnection, baseDirectory,
            new RuntimeModelBindingsResolver("legacy-unversioned", approvedModelBindings),
            abortDrainTimeout, workingDirectory, expectedRuntimeVersion, timeProvider)
    {
    }

    /// <summary>Creates a factory using versioned approved model bindings.</summary>
    public RuntimeCopilotSessionFactory(
        RuntimeConnection approvedConnection,
        string baseDirectory,
        RuntimeModelBindingsResolver approvedModelBindings,
        TimeSpan? abortDrainTimeout = null,
        string? workingDirectory = null,
        string? expectedRuntimeVersion = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(approvedConnection);
        ArgumentNullException.ThrowIfNull(approvedModelBindings);
        if (approvedConnection is not UriRuntimeConnection { ConnectionToken.Length: > 0 } &&
            approvedConnection is not StdioRuntimeConnection { Path: { Length: > 0 }, Environment: not null } ||
            !Path.IsPathFullyQualified(baseDirectory) ||
            workingDirectory is not null && !Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("A registered authenticated connection or private stdio runtime and absolute directories are required.");
        if (approvedConnection is StdioRuntimeConnection process &&
            (!Path.IsPathFullyQualified(process.Path!) ||
                process.Args is { Count: > 0 } && !process.Args.SequenceEqual(["--no-auto-update"]) ||
                !process.Environment!.TryGetValue("COPILOT_CLI_DIST_DIR", out var distribution) ||
                distribution != Path.GetDirectoryName(process.Path) ||
                process.Environment!.Keys.Any(key =>
                    key is not ("PATH" or "HOME" or "LANG" or "TMPDIR" or "SSL_CERT_FILE" or "SSL_CERT_DIR" or
                        "COPILOT_CLI_DIST_DIR"))))
            throw new ArgumentException("The image-owned stdio runtime requires an absolute executable and credential-free environment.");
        _connection = approvedConnection;
        _baseDirectory = baseDirectory;
        _workingDirectory = workingDirectory ?? baseDirectory;
        if (expectedRuntimeVersion is not null)
            RuntimeContractValidation.ValidateIdentifier(expectedRuntimeVersion);
        _expectedRuntimeVersion = expectedRuntimeVersion;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _modelBindings = approvedModelBindings;
        _abortDrainTimeout = abortDrainTimeout ?? TimeSpan.FromSeconds(30);
        if (_abortDrainTimeout <= TimeSpan.Zero || _abortDrainTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(abortDrainTimeout));
    }

    /// <summary>
    /// Starts the pinned SDK, verifies its effective model, applies accepted limits and tools,
    /// and restores a compatible native cache or journal context when supplied.
    /// </summary>
    internal async Task<RuntimeCopilotSession> CreateAsync(
        RuntimeRegistration registration,
        string acceptedModelSelectionReference,
        SecretCredential sdkCredential,
        Func<CancellationToken, Task> requireCreationAuthority,
        CancellationToken cancellationToken,
        SkillRuntimeContentProjectionV1 acceptedSkills,
        RuntimeSessionRecovery? recovery = null,
        Func<RuntimeActionRequest, CancellationToken, Task>? requireActionAuthority = null,
        RuntimeMcpServerEndpoint? mcpServer = null,
        Func<CancellationToken, Task<RuntimeSessionRecovery>>? journalFallback = null)
    {
        ArgumentNullException.ThrowIfNull(requireCreationAuthority);
        RuntimeContractValidation.Validate(registration);
        RuntimeSkillContentHttpClient.ValidateForRegistration(acceptedSkills, registration);
        if (journalFallback is not null && recovery is null)
            throw new RuntimeAuthorizationException("runtime_resume_cache_unavailable");
        if (mcpServer is { IsValid: false })
            throw new RuntimeAuthorizationException("runtime_mcp_server_binding_invalid");
        if (mcpServer is not null)
            throw new RuntimeAuthorizationException("runtime_mcp_tool_interception_unavailable");
        if (acceptedModelSelectionReference != registration.Binding.ModelSelectionReference)
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        var model = _modelBindings.Resolve(acceptedModelSelectionReference,
            registration.Binding.ModelSourceMode, registration.Binding.ModelBindingPin);
        if (model.SourceMode == ModelSourceMode.Byok && registration.Binding.ModelBindingPin is null)
            throw new RuntimeAuthorizationException("runtime_byok_binding_pin_required");
        var reasoningEffort = registration.Binding.ReasoningEffort;
        var resolvedModelBindingPin = _modelBindings.Pin(
            acceptedModelSelectionReference, model.SourceMode, reasoningEffort);
        if (!sdkCredential.IsUsable())
            throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
        var token = sdkCredential.GetValue();
        if (model.SourceMode == ModelSourceMode.HostedCopilot &&
            (!token.StartsWith("ghu_", StringComparison.Ordinal) || token.Any(char.IsWhiteSpace)))
            throw new RuntimeAuthorizationException("runtime_copilot_access_token_invalid");
        var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            Connection = _connection,
            BaseDirectory = _baseDirectory,
#pragma warning disable GHCP001 // Session filesystem RPC options are pinned to SDK 1.0.18.
            SessionFs = new SessionFsConfig
            {
                InitialWorkingDirectory = "/workspace",
                SessionStatePath = "state",
                Conventions = GitHub.Copilot.Rpc.SessionFsSetProviderConventions.Posix,
                Capabilities = new GitHub.Copilot.Rpc.SessionFsSetProviderCapabilities { Sqlite = false }
            },
#pragma warning restore GHCP001
            Logger = NullLogger.Instance,
            LogLevel = CopilotLogLevel.None
        });
        CopilotSession? session = null;
        var usage = Channel.CreateBounded<RuntimeCopilotUsageItem>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        var promptCapacityObserver = new RuntimeNativePromptCapacityObserver();
        var turns = new RuntimeCopilotTurnObserver();
        var nativeFiles = new RuntimeNativeSessionFiles(
            [token, (_connection as UriRuntimeConnection)?.ConnectionToken ?? ""], _workingDirectory);
        RuntimeCopilotToolCallAuthorization? toolAuthorization = null;
        var skillProvider = new RuntimeAcceptedSkillProvider(acceptedSkills);
        var recoveryMode = RuntimeSessionRecoveryMode.Fresh;
        string? recoveryReason = null;
        try
        {
            await client.StartAsync(cancellationToken);
            var status = await client.GetStatusAsync(cancellationToken);
            if (_expectedRuntimeVersion is not null && status.Version != _expectedRuntimeVersion)
                throw new RuntimeAuthorizationException("runtime_native_version_mismatch");
            string catalogHash;
            decimal? multiplier = null;
            long? supportedPromptTokens = model.PromptCapacityTokens;
            ImmutableArray<string>? verifiedHostedReasoningEfforts = null;
            DateTimeOffset? verifiedHostedReasoningCatalogAt = null;
#pragma warning disable GHCP001 // The pinned SDK marks its RPC policy and permission types as experimental.
            if (model.SourceMode == ModelSourceMode.HostedCopilot)
            {
                var catalog = await client.Rpc.Models.ListAsync(
                    selectionId: null, gitHubToken: token, cancellationToken: cancellationToken);
                var matches = catalog.Models.Where(item => item.Id == model.ModelId).Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Policy?.State != GitHub.Copilot.Rpc.ModelPolicyState.Enabled)
                    throw new RuntimeAuthorizationException("runtime_sdk_model_unavailable");
                RequireSupportedReasoningEffort(
                    matches[0].Capabilities?.Supports?.ReasoningEffort,
                    matches[0].SupportedReasoningEfforts,
                    reasoningEffort);
                // Publish only the contract's canonical levels and retain when the SDK catalog was observed.
                verifiedHostedReasoningEfforts = ResolveHostedReasoningEfforts(
                    matches[0].Capabilities?.Supports?.ReasoningEffort,
                    matches[0].SupportedReasoningEfforts);
                verifiedHostedReasoningCatalogAt = _timeProvider.GetUtcNow();
                catalogHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(catalog));
                multiplier = RuntimeCopilotSession.NullableDecimal(matches[0].Billing?.Multiplier);
                var limits = matches[0].Capabilities?.Limits;
                supportedPromptTokens = limits?.MaxPromptTokens ?? limits?.MaxContextWindowTokens;
                if (model.PromptCapacityTokens is { } configuredCapacity && supportedPromptTokens is { } nativeCapacity)
                    supportedPromptTokens = Math.Min(configuredCapacity, nativeCapacity);
            }
            else
            {
                if (!model.SupportedReasoningEfforts.Contains(reasoningEffort, StringComparer.Ordinal))
                    throw new RuntimeAuthorizationException("runtime_reasoning_effort_unsupported");
                catalogHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(model));
            }
            var sdkVersion = typeof(CopilotClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? throw new InvalidOperationException("The Copilot SDK assembly has no version metadata.");
            var binding = registration.Binding;
            var maxPromptTokens = ResolvePromptCapacity(binding.MaxPromptTokens, supportedPromptTokens);
            var sdkSessionId = RuntimeContractValidation.NativeSessionId(binding);
            toolAuthorization = requireActionAuthority is null ? null :
                new RuntimeCopilotToolCallAuthorization(
                    registration, sdkSessionId, requireActionAuthority, turns);
            if (recovery is not null)
            {
                var recordedLimit = recovery.Cache?.Material.Reference.Material?.MaxPromptTokens;
                var recordedMaterial = recovery.Cache?.Material.Reference.Material;
                var previousConfiguration = recovery.PreviousConfigurationBinding;
                var previousModelBinding = recovery.PreviousModelBinding;
                if (recovery.JournalEvidence is not null &&
                    previousConfiguration is null && previousModelBinding is null && recordedMaterial is null)
                    throw new RuntimeAuthorizationException("runtime_recovery_model_binding_unavailable");
                if (previousConfiguration is not null &&
                    (previousConfiguration.Kind != SessionMaterialKind.RuntimeConfiguration ||
                     previousConfiguration.TenantId != binding.TenantId ||
                     previousConfiguration.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                     previousConfiguration.ModelSelectionReference != acceptedModelSelectionReference ||
                     previousConfiguration.ModelId != model.ModelId ||
                     previousConfiguration.ReasoningEffort != reasoningEffort ||
                     previousConfiguration.ModelBindingPin != binding.ModelBindingPin ||
                     previousConfiguration.ResolvedModelBindingPin != resolvedModelBindingPin))
                    throw new RuntimeAuthorizationException("runtime_recovery_model_binding_mismatch");
                if (previousModelBinding is not null &&
                    (previousModelBinding.Kind is not
                         (SessionMaterialKind.TurnContent or SessionMaterialKind.SdkCache) ||
                     previousModelBinding.TenantId != binding.TenantId ||
                     previousModelBinding.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                     previousModelBinding.ModelSelectionReference != acceptedModelSelectionReference ||
                     previousModelBinding.ModelId != model.ModelId ||
                     previousModelBinding.ReasoningEffort != reasoningEffort ||
                     previousModelBinding.ModelBindingPin != binding.ModelBindingPin ||
                     previousModelBinding.ResolvedModelBindingPin != resolvedModelBindingPin))
                    throw new RuntimeAuthorizationException("runtime_recovery_model_binding_mismatch");
                if (recordedMaterial is not null &&
                    (recordedMaterial.ReasoningEffort != reasoningEffort ||
                     recordedMaterial.ModelBindingPin != registration.Binding.ModelBindingPin ||
                     recordedMaterial.ResolvedModelBindingPin != resolvedModelBindingPin))
                    throw new RuntimeAuthorizationException("runtime_recovery_model_binding_mismatch");
                var cacheMatches = recovery.CacheMatches(
                    registration, sdkVersion, status.Version, model.ModelId, resolvedModelBindingPin, recordedLimit);
                if (cacheMatches && recordedLimit is { } previousLimit)
                {
                    if (maxPromptTokens is null || maxPromptTokens < previousLimit)
                        throw new RuntimeAuthorizationException("runtime_prompt_capacity_changed");
                    maxPromptTokens = previousLimit;
                }
                cacheMatches = recovery.CacheMatches(
                    registration, sdkVersion, status.Version, model.ModelId, resolvedModelBindingPin, maxPromptTokens);
                if (cacheMatches)
                {
                    try
                    {
                        nativeFiles.Restore(recovery.Cache!.Bytes);
                        recoveryMode = RuntimeSessionRecoveryMode.NativeCache;
                    }
                    catch (RuntimeAuthorizationException failure) when (
                        failure.Code is "runtime_sdk_cache_invalid" or "runtime_sdk_cache_unavailable")
                    {
                        recoveryReason = failure.Code;
                    }
                }

                if (recoveryMode != RuntimeSessionRecoveryMode.NativeCache)
                {
                    if (journalFallback is not null)
                    {
                        var reason = recoveryReason ??
                            (recovery.Cache is null ? "runtime_sdk_cache_missing" : "runtime_sdk_cache_incompatible");
                        recovery = await journalFallback(cancellationToken).ConfigureAwait(false);
                        if (recovery.Cache is not null || recovery.JournalRebuild is null)
                            throw new RuntimeAuthorizationException("runtime_resume_journal_receipt_invalid");
                        recoveryMode = RuntimeSessionRecoveryMode.JournalRebuild;
                        recoveryReason = reason;
                    }
                    else if (recovery.Turns.Count > 0 || recovery.Cache is not null)
                    {
                        recoveryMode = RuntimeSessionRecoveryMode.JournalRebuild;
                        recoveryReason ??= recovery.Cache is null
                            ? "runtime_sdk_cache_missing"
                            : "runtime_sdk_cache_incompatible";
                    }
                }
            }
            nativeFiles.InstallSkillResources(acceptedSkills);
            await requireCreationAuthority(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
            void Configure(SessionConfigBase options)
            {
                options.Model = model.ModelId;
                options.ReasoningEffort = reasoningEffort;
                options.GitHubToken = model.SourceMode == ModelSourceMode.HostedCopilot ? token : null;
                options.Provider = model.Provider?.ToSdkProvider(model.ModelId, token);
                if (options.Provider is { } provider)
                    provider.MaxPromptTokens = maxPromptTokens;
                options.ModelCapabilities = maxPromptTokens is not null || model.SourceMode == ModelSourceMode.Byok
                    ? new GitHub.Copilot.Rpc.ModelCapabilitiesOverride
                    {
                        Supports = model.SourceMode == ModelSourceMode.Byok
                            ? new GitHub.Copilot.Rpc.ModelCapabilitiesOverrideSupports { ReasoningEffort = true }
                            : null,
                        Limits = maxPromptTokens is { } promptCapacity
                            ? new GitHub.Copilot.Rpc.ModelCapabilitiesOverrideLimits
                            {
                                MaxPromptTokens = promptCapacity
                            }
                            : null
                    }
                    : null;
                options.EnableConfigDiscovery = false;
                options.EnableSessionStore = false;
                options.EnableSkills = !acceptedSkills.Skills.IsEmpty;
                options.SkillProvider = acceptedSkills.Skills.IsEmpty ? null : skillProvider;
                options.AvailableTools = requireActionAuthority is null
                    ? [] : ["view", "create", "edit", "bash", "read_bash", "write_bash", "stop_bash"];
                options.WorkingDirectory = _workingDirectory;
                options.CreateSessionFsProvider = _ => nativeFiles;
                options.OnPermissionRequest = toolAuthorization is null
                    ? (_, _) => Task.FromResult(GitHub.Copilot.Rpc.PermissionDecision.Reject(
                        "runtime_tool_authority_unavailable"))
                    : toolAuthorization.OnPermissionRequestAsync;
                if (toolAuthorization is not null)
                    options.Hooks = new SessionHooks
                    {
                        OnPreToolUse = toolAuthorization.OnPreToolUseAsync
                    };
                options.OnEvent = sessionEvent =>
                {
                    // The pinned SDK does not prove which A2A send owns a usage event.
                    if (sessionEvent is AssistantUsageEvent nativeUsage &&
                        !usage.Writer.TryWrite(new(nativeUsage, null, null)))
                        usage.Writer.TryComplete(new RuntimeAuthorizationException(
                            "runtime_usage_pending_capacity_exceeded"));
                    promptCapacityObserver.Observe(sessionEvent);
                    turns.Observe(sessionEvent);
                    if (sessionEvent is SessionIdleEvent or SessionErrorEvent)
                        toolAuthorization?.Clear();
                };
                options.RemoteSession = GitHub.Copilot.Rpc.RemoteSessionMode.Off;
                if (recoveryMode == RuntimeSessionRecoveryMode.JournalRebuild)
                    if (recovery!.RebuiltContext() is { } context)
                        options.SystemMessage = new SystemMessageConfig
                    {
                        Mode = SystemMessageMode.Append, Content = context
                    };
            }
            if (recoveryMode == RuntimeSessionRecoveryMode.NativeCache)
            {
                var options = new ResumeSessionConfig { ContinuePendingWork = false };
                Configure(options);
                session = await client.ResumeSessionAsync(sdkSessionId, options, cancellationToken);
            }
            else
            {
                var options = new SessionConfig { SessionId = sdkSessionId };
                Configure(options);
                session = await client.CreateSessionAsync(options, cancellationToken);
            }
            var currentModel = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
            if (currentModel.ModelId != model.ModelId)
                throw new RuntimeAuthorizationException("runtime_sdk_effective_model_mismatch");
            if (currentModel.ReasoningEffort != reasoningEffort)
                throw new RuntimeAuthorizationException("runtime_sdk_effective_reasoning_effort_mismatch");
#pragma warning restore GHCP001
            if (session.SessionId != sdkSessionId)
                throw new RuntimeAuthorizationException("runtime_sdk_session_mismatch");
            if (!sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
            var facts = new SdkSessionFacts(
                registration.RuntimeInstanceId,
                session.SessionId,
                sdkVersion,
                status.Version,
                acceptedModelSelectionReference,
                currentModel.ModelId,
                catalogHash,
                multiplier,
                model.SourceMode == ModelSourceMode.HostedCopilot ? "hosted-copilot" : "byok",
                model.SourceMode == ModelSourceMode.HostedCopilot
                    ? SdkMeterSources.CopilotNanoAiu : SdkMeterSources.ByokTokens,
                registration.Binding.AcceptedSelectionHash,
                registration.Revision)
            {
                MaxPromptTokens = maxPromptTokens,
                ReasoningEffort = reasoningEffort,
                ModelBindingPin = registration.Binding.ModelBindingPin,
                ResolvedModelBindingPin = resolvedModelBindingPin,
                ByokProvider = model.Provider is { } actualProvider
                    ? new(actualProvider.Type, currentModel.ModelId, _modelBindings.ConfigurationHash)
                    : null
            };
            var consumedNativeCacheAcknowledgment = recoveryMode == RuntimeSessionRecoveryMode.NativeCache
                ? recovery?.Cache?.Material
                    ?? throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid")
                : null;
            var journalEvidence = recoveryMode == RuntimeSessionRecoveryMode.JournalRebuild
                ? recovery?.JournalEvidence
                : null;
            return new RuntimeCopilotSession(client, session, facts, usage, promptCapacityObserver, turns,
                nativeFiles, recoveryMode, recoveryReason, recovery?.JournalRebuild,
                consumedNativeCacheAcknowledgment, journalEvidence, _abortDrainTimeout,
                verifiedHostedReasoningEfforts, verifiedHostedReasoningCatalogAt);
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync();
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Returns the accepted limit capped by native model capacity, or rejects unknown capacity.</summary>
    internal static int? ResolvePromptCapacity(int? acceptedLimit, long? supportedCapacity)
    {
        if (acceptedLimit is null)
            return null;
        if (acceptedLimit is < 1024 or > 200000)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_invalid");
        if (supportedCapacity is null or < 1)
            throw new RuntimeAuthorizationException("runtime_model_prompt_capacity_unavailable");
        return checked((int)Math.Min(acceptedLimit.Value, supportedCapacity.Value));
    }

    private static void RequireSupportedReasoningEffort(
        bool? modelSupportsReasoningEffort,
        IEnumerable<string>? supportedEfforts,
        string requestedEffort)
    {
        if (modelSupportsReasoningEffort is not true ||
            supportedEfforts is null ||
            !supportedEfforts.Contains(requestedEffort, StringComparer.Ordinal))
            throw new RuntimeAuthorizationException("runtime_reasoning_effort_unsupported");
    }

    private static ImmutableArray<string> ResolveHostedReasoningEfforts(
        bool? modelSupportsReasoningEffort,
        IEnumerable<string>? supportedEfforts)
    {
        if (modelSupportsReasoningEffort is not true || supportedEfforts is null)
            return [];

        // Keep the published vocabulary aligned with the values accepted by project configuration.
        var supported = supportedEfforts.ToHashSet(StringComparer.Ordinal);
        return new[] { "low", "medium", "high", "xhigh" }
            .Where(supported.Contains)
            .ToImmutableArray();
    }
}
