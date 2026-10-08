using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeCopilotSessionFactory
{
    private readonly RuntimeConnection _connection;
    private readonly string _baseDirectory;
    private readonly ImmutableDictionary<string, RuntimeModelBinding> _modelBindings;
    private readonly TimeSpan _abortDrainTimeout;
    private readonly string _workingDirectory;
    private readonly string? _expectedRuntimeVersion;

    public string WorkingDirectory => _workingDirectory;

    public RuntimeCopilotSessionFactory(
        RuntimeConnection approvedConnection,
        string baseDirectory,
        IReadOnlyDictionary<string, RuntimeModelBinding> approvedModelBindings,
        TimeSpan? abortDrainTimeout = null,
        string? workingDirectory = null,
        string? expectedRuntimeVersion = null)
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
                process.Environment!.Keys.Any(key =>
                    key is not ("PATH" or "HOME" or "LANG" or "TMPDIR" or "SSL_CERT_FILE" or "SSL_CERT_DIR"))))
            throw new ArgumentException("The image-owned stdio runtime requires an absolute executable and credential-free environment.");
        foreach (var (reference, model) in approvedModelBindings)
        {
            RuntimeContractValidation.ValidateIdentifier(reference);
            ArgumentNullException.ThrowIfNull(model);
            model.Validate();
        }
        _connection = approvedConnection;
        _baseDirectory = baseDirectory;
        _workingDirectory = workingDirectory ?? baseDirectory;
        if (expectedRuntimeVersion is not null)
            RuntimeContractValidation.ValidateIdentifier(expectedRuntimeVersion);
        _expectedRuntimeVersion = expectedRuntimeVersion;
        _modelBindings = approvedModelBindings.ToImmutableDictionary(StringComparer.Ordinal);
        _abortDrainTimeout = abortDrainTimeout ?? TimeSpan.FromSeconds(30);
        if (_abortDrainTimeout <= TimeSpan.Zero || _abortDrainTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(abortDrainTimeout));
    }

    internal async Task<RuntimeCopilotSession> CreateAsync(
        RuntimeRegistration registration,
        string acceptedModelSelectionReference,
        SecretCredential sdkCredential,
        Func<CancellationToken, Task> requireCreationAuthority,
        CancellationToken cancellationToken,
        RuntimeSessionRecovery? recovery = null,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? requireActionAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(requireCreationAuthority);
        RuntimeContractValidation.Validate(registration);
        if (acceptedModelSelectionReference != registration.Binding.ModelSelectionReference ||
            !_modelBindings.TryGetValue(acceptedModelSelectionReference, out var model))
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        if (registration.Binding.ModelSourceMode != model.SourceMode)
            throw new RuntimeAuthorizationException("runtime_model_source_mode_mismatch");
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
#pragma warning disable GHCP001 // Session filesystem RPC options are pinned to SDK 1.0.11.
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
        var turns = new RuntimeCopilotTurnObserver();
        var nativeFiles = new RuntimeNativeSessionFiles(
            token, (_connection as UriRuntimeConnection)?.ConnectionToken ?? "");
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
#pragma warning disable GHCP001 // The pinned SDK marks its RPC policy and permission types as experimental.
            if (model.SourceMode == ModelSourceMode.HostedCopilot)
            {
                var catalog = await client.Rpc.Models.ListAsync(token, cancellationToken);
                var matches = catalog.Models.Where(item => item.Id == model.ModelId).Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Policy?.State != GitHub.Copilot.Rpc.ModelPolicyState.Enabled)
                    throw new RuntimeAuthorizationException("runtime_sdk_model_unavailable");
                catalogHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(catalog));
                multiplier = RuntimeCopilotSession.NullableDecimal(matches[0].Billing?.Multiplier);
            }
            else
            {
                catalogHash = RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(model));
            }
            var sdkVersion = typeof(CopilotClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? throw new InvalidOperationException("The Copilot SDK assembly has no version metadata.");
            var binding = registration.Binding;
            var sdkSessionId = RuntimeContractValidation.NativeSessionId(binding);
            if (recovery is not null)
            {
                if (recovery.CacheMatches(registration, sdkVersion, status.Version, model.ModelId))
                {
                    try
                    {
                        nativeFiles.Restore(recovery.Cache!.Bytes);
                        recoveryMode = RuntimeSessionRecoveryMode.NativeCache;
                    }
                    catch (RuntimeAuthorizationException failure) when (
                        failure.Code is "runtime_sdk_cache_invalid" or "runtime_sdk_cache_unavailable")
                    {
                        recoveryMode = RuntimeSessionRecoveryMode.JournalRebuild;
                        recoveryReason = failure.Code;
                    }
                }
                else if (recovery.Turns.Count > 0 || recovery.Cache is not null)
                {
                    recoveryMode = RuntimeSessionRecoveryMode.JournalRebuild;
                    recoveryReason = recovery.Cache is null ? "runtime_sdk_cache_missing" : "runtime_sdk_cache_incompatible";
                }
            }
            await requireCreationAuthority(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
            void Configure(SessionConfigBase options)
            {
                options.Model = model.ModelId;
                options.GitHubToken = model.SourceMode == ModelSourceMode.HostedCopilot ? token : null;
                options.Provider = model.Provider?.ToSdkProvider(model.ModelId, token);
                options.EnableConfigDiscovery = false;
                options.EnableSessionStore = false;
                options.AvailableTools = requireActionAuthority is null
                    ? [] : ["view", "create", "edit", "bash", "read_bash", "write_bash", "stop_bash"];
                options.WorkingDirectory = _workingDirectory;
                options.CreateSessionFsProvider = _ => nativeFiles;
                options.OnPermissionRequest = async (request, invocation) =>
                {
                    if (requireActionAuthority is null || invocation.SessionId != sdkSessionId)
                        return GitHub.Copilot.Rpc.PermissionDecision.Reject("runtime_tool_authority_unavailable");
                    var actionId = request switch
                    {
                        PermissionRequestRead { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } => "tool.read",
                        PermissionRequestWrite { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } => "tool.write",
                        PermissionRequestShell { RequestSandboxBypass: not true, ManagedApprovalRequired: not true } => "exec.shell",
                        _ => null
                    };
                    if (actionId is null)
                        return GitHub.Copilot.Rpc.PermissionDecision.Reject("runtime_permission_not_registered");
                    try
                    {
                        await requireActionAuthority(actionId, JsonSerializer.SerializeToUtf8Bytes(request),
                            turns.RequireActiveToken()).ConfigureAwait(false);
                        return GitHub.Copilot.Rpc.PermissionDecision.ApproveOnce();
                    }
                    catch (RuntimeAuthorizationException failure)
                    {
                        return GitHub.Copilot.Rpc.PermissionDecision.Reject(failure.Code);
                    }
                };
                if (requireActionAuthority is not null)
                    options.Hooks = new SessionHooks
                    {
                        OnPreToolUse = async (input, invocation) =>
                        {
                            var actionId = input.ToolName switch
                            {
                                "view" => "tool.read",
                                "create" or "edit" => "tool.write",
                                "bash" => "exec.shell",
                                "read_bash" => "exec.read",
                                "write_bash" => "exec.write",
                                "stop_bash" => "exec.stop",
                                _ => null
                            };
                            if (actionId is null || input.SessionId != sdkSessionId || invocation.SessionId != sdkSessionId)
                                return new PreToolUseHookOutput
                                {
                                    PermissionDecision = "deny", PermissionDecisionReason = "runtime_tool_not_registered"
                                };
                            try
                            {
                                await requireActionAuthority(actionId, JsonSerializer.SerializeToUtf8Bytes(input),
                                    turns.RequireActiveToken()).ConfigureAwait(false);
                                return new PreToolUseHookOutput { PermissionDecision = "ask" };
                            }
                            catch (RuntimeAuthorizationException failure)
                            {
                                return new PreToolUseHookOutput
                                {
                                    PermissionDecision = "deny", PermissionDecisionReason = failure.Code
                                };
                            }
                        }
                    };
                options.OnEvent = sessionEvent =>
                {
                    if (sessionEvent is AssistantUsageEvent nativeUsage &&
                        !usage.Writer.TryWrite(new(nativeUsage, null)))
                        usage.Writer.TryComplete(new RuntimeAuthorizationException(
                            "runtime_usage_pending_capacity_exceeded"));
                    turns.Observe(sessionEvent);
                };
                options.RemoteSession = GitHub.Copilot.Rpc.RemoteSessionMode.Off;
                if (recoveryMode == RuntimeSessionRecoveryMode.JournalRebuild)
                    options.SystemMessage = new SystemMessageConfig
                    {
                        Mode = SystemMessageMode.Append, Content = recovery!.RebuiltContext()
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
                registration.Revision);
            return new RuntimeCopilotSession(client, session, facts, usage, turns,
                nativeFiles, recoveryMode, recoveryReason, _abortDrainTimeout);
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync();
            await client.DisposeAsync();
            throw;
        }
    }

}
