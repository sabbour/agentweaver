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
    private readonly ImmutableDictionary<string, string> _modelBindings;

    public RuntimeCopilotSessionFactory(
        RuntimeConnection approvedConnection,
        string baseDirectory,
        IReadOnlyDictionary<string, string> approvedModelBindings)
    {
        ArgumentNullException.ThrowIfNull(approvedConnection);
        ArgumentNullException.ThrowIfNull(approvedModelBindings);
        if (approvedConnection is not UriRuntimeConnection { ConnectionToken.Length: > 0 } ||
            !Path.IsPathFullyQualified(baseDirectory))
            throw new ArgumentException("A registered memory-authenticated SDK connection and absolute private base directory are required.");
        foreach (var (reference, modelId) in approvedModelBindings)
        {
            RuntimeContractValidation.ValidateIdentifier(reference);
            RuntimeContractValidation.ValidateIdentifier(modelId);
        }
        _connection = approvedConnection;
        _baseDirectory = baseDirectory;
        _modelBindings = approvedModelBindings.ToImmutableDictionary(StringComparer.Ordinal);
    }

    internal async Task<RuntimeCopilotSession> CreateHostedAsync(
        RuntimeRegistration registration,
        string acceptedModelSelectionReference,
        SecretCredential sdkCredential,
        Func<CancellationToken, Task> requireCreationAuthority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requireCreationAuthority);
        RuntimeContractValidation.Validate(registration);
        if (!_modelBindings.TryGetValue(acceptedModelSelectionReference, out var requestedModel))
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            Connection = _connection,
            BaseDirectory = _baseDirectory,
            Logger = NullLogger.Instance,
            LogLevel = CopilotLogLevel.None
        });
        CopilotSession? session = null;
        var usage = Channel.CreateBounded<AssistantUsageEvent>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        try
        {
            await client.StartAsync(cancellationToken);
            // Per-request/session tokens stay on the SDK wire, not child-process environments.
            var catalog = await client.Rpc.Models.ListAsync(sdkCredential.GetValue(), cancellationToken);
            var matches = catalog.Models.Where(model => model.Id == requestedModel).Take(2).ToArray();
#pragma warning disable GHCP001 // The pinned SDK marks its RPC policy and permission types as experimental.
            if (matches.Length != 1 || matches[0].Policy?.State != GitHub.Copilot.Rpc.ModelPolicyState.Enabled)
                throw new RuntimeAuthorizationException("runtime_sdk_model_unavailable");
            var selected = matches[0];
            var status = await client.GetStatusAsync(cancellationToken);
            var sdkSessionId = $"agentweaver-runtime-{registration.RuntimeInstanceId:D}";
            await requireCreationAuthority(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
            session = await client.CreateSessionAsync(new SessionConfig
            {
                SessionId = sdkSessionId,
                Model = selected.Id,
                GitHubToken = sdkCredential.GetValue(),
                EnableConfigDiscovery = false,
                EnableSessionStore = false,
                AvailableTools = [],
                OnPermissionRequest = (_, _) => Task.FromResult(
                    GitHub.Copilot.Rpc.PermissionDecision.Reject("Runtime bootstrap does not authorize tool execution.")),
                OnEvent = sessionEvent =>
                {
                    if (sessionEvent is AssistantUsageEvent nativeUsage && !usage.Writer.TryWrite(nativeUsage))
                        usage.Writer.TryComplete(new RuntimeAuthorizationException(
                            "runtime_usage_pending_capacity_exceeded"));
                },
                RemoteSession = GitHub.Copilot.Rpc.RemoteSessionMode.Off
            }, cancellationToken);
            var currentModel = await session.Rpc.Model.GetCurrentAsync(cancellationToken);
            if (currentModel.ModelId != selected.Id)
                throw new RuntimeAuthorizationException("runtime_sdk_effective_model_mismatch");
#pragma warning restore GHCP001
            if (session.SessionId != sdkSessionId)
                throw new RuntimeAuthorizationException("runtime_sdk_session_mismatch");
            if (!sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_sdk_credential_unavailable");
            var sdkVersion = typeof(CopilotClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? throw new InvalidOperationException("The Copilot SDK assembly has no version metadata.");
            var facts = new SdkSessionFacts(
                registration.RuntimeInstanceId,
                session.SessionId,
                sdkVersion,
                status.Version,
                acceptedModelSelectionReference,
                currentModel.ModelId,
                RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(catalog)),
                RuntimeCopilotSession.NullableDecimal(selected.Billing?.Multiplier),
                "hosted-copilot",
                SdkMeterSources.CopilotNanoAiu,
                registration.Binding.AcceptedSelectionHash,
                registration.Revision);
            return new RuntimeCopilotSession(client, session, facts, usage);
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
