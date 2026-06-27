using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using A2A;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Worker-side <see cref="IWorkflowTurnAgent"/> adapter that forwards each agent turn to a
/// sandbox pod's <c>CopilotAIAgent</c> via the A2A protocol
/// (<c>Microsoft.Agents.AI.A2A</c> / <c>message:stream</c> mode).
///
/// <para>
/// <b>Seam contract (§4.7.5 / §4.7.6):</b> the pod hosts the leaf <c>CopilotAIAgent</c> via
/// <c>MapA2A(agent, "/a2a/agent", agentCard)</c> (Morpheus, <c>Agentweaver.AgentHost</c>).
/// The worker calls <c>POST /a2a/agent/v1/message:stream</c> on the per-run pod.
/// A2A is the <b>sole</b> worker→pod transport. The rollback path is
/// <c>Sandbox:AgentExecutionMode=in-api</c> (revert to <see cref="CopilotAIAgent"/> in-process).
/// </para>
///
/// <para>
/// <b>Checkpoint proxy (Q2):</b> the pod has <b>no</b> <c>ICheckpointStore</c> access and
/// <b>no</b> database connection. All checkpoint and run-event writes flow through the
/// worker process. The MAF graph, <c>CheckpointManager</c>, and <c>RequestPort</c> never
/// leave the worker; only the leaf AIAgent turn is forwarded over A2A. This keeps P1
/// safe on SQLite/replicas:1 without any Postgres dependency.
/// </para>
///
/// <para>
/// <b>Streaming:</b> the pod encodes <see cref="RunEvent"/>s as A2A <c>DataContent</c>
/// parts (media type <c>application/x-agentweaver-run-event+json</c>) on the
/// <c>message:stream</c>. The worker decodes them here and writes to the
/// <c>ChannelWriter&lt;RunEvent&gt;</c> side-channel. <c>RecordingChannelWriter</c>
/// reassigns monotonic sequence numbers in arrival order, preserving SSE ordering.
/// </para>
/// </summary>
public sealed class RemoteAgentProxy : IWorkflowTurnAgent
{
    private const string A2AAgentId = "agentweaver-worker-proxy";
    private const string A2AAgentName = "Agentweaver Worker Agent Proxy";
    private const string A2AAgentDescription = "Worker-side A2A proxy for sandbox pod CopilotAIAgent (spec-018 P1)";

    private readonly ISandboxAgentEndpointResolver _endpointResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteAgentProxy> _logger;

    // Per-run state — populated by SetupAsync, consumed by RunTurnAsync.
    private string _runId = "";
    private string _workingDirectory = "";
    private string _repositoryPath = "";
    private string? _modelId;
    private string? _systemPromptContext;
    private ChannelWriter<RunEvent>? _streamWriter;
    private string? _projectId;
    private string? _agentName;
    private string? _apiBaseUrl;
    private string? _apiKey;
    private string? _userId;

    // Created in SetupAsync, used in RunTurnAsync, disposed in DisposeAsync.
    private A2AAgent? _a2aAgent;
    private A2AAgentSession? _session;
    private HttpClient? _httpClient;

    public RemoteAgentProxy(
        ISandboxAgentEndpointResolver endpointResolver,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<RemoteAgentProxy>();
    }

    /// <inheritdoc />
    public async Task SetupAsync(
        string workingDirectory,
        string repositoryPath,
        string runId,
        string? modelId,
        string? systemPromptContext,
        ChannelWriter<RunEvent>? streamWriter,
        string? projectId,
        string? agentName,
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken ct,
        string? userId = null)
    {
        _workingDirectory = workingDirectory;
        _repositoryPath = repositoryPath;
        _runId = runId;
        _modelId = modelId;
        _systemPromptContext = systemPromptContext;
        _streamWriter = streamWriter;
        _projectId = projectId;
        _agentName = agentName;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _userId = userId;

        // Resolve the per-run pod's A2A base endpoint (e.g. https://10.0.0.5:8080/a2a/agent).
        // Supplied by ISandboxAgentEndpointResolver using the bound SandboxClaim pod name/IP.
        var podEndpointUri = await _endpointResolver.TryResolveEndpointAsync(runId, ct)
            .ConfigureAwait(false);

        if (podEndpointUri is null)
        {
            throw new InvalidOperationException(
                $"RemoteAgentProxy: no A2A endpoint found for run '{runId}'. " +
                "The sandbox pod may not yet be bound (IPodNameRegistry), " +
                "or ISandboxAgentEndpointResolver is not configured for this environment. " +
                "Set Sandbox:AgentExecutionMode=in-api to revert to in-process execution.");
        }

        // HttpClient named "a2a-sandbox-pod" — configured in Program.cs with appropriate
        // TLS/cert settings per H1 (mTLS or bearer). Client lifetime is per-run.
        _httpClient = _httpClientFactory.CreateClient("a2a-sandbox-pod");

        // The pod hosts its A2A endpoints via MapA2AHttpJson (HTTP+JSON transport):
        //   POST {podEndpointUri}/message:stream  and  GET {podEndpointUri}/card.
        // We MUST use the matching A2AHttpJsonClient — the JSON-RPC A2AClient posts to the base
        // path and 404s against the HTTP+JSON routes (the two A2A transports are not interchange-
        // able). Both implement IA2AClient, which A2AAgent wraps.
        var a2aClient = new A2AHttpJsonClient(podEndpointUri, _httpClient);

        // Wrap as A2AAgent — framework-native A2A client seam (spec §4.7.5).
        _a2aAgent = new A2AAgent(
            a2aClient,
            new A2AAgentOptions
            {
                Id = A2AAgentId,
                Name = A2AAgentName,
                Description = A2AAgentDescription,
            },
            _loggerFactory);

        // Use the runId as the A2A contextId for traceability across turns.
        // A2AAgentSession tracks ContextId + TaskId on the A2A layer (ephemeral by design —
        // we do NOT rely on A2A's contextId state for durable resume; that stays our
        // DB-backed ICheckpointStore + serialized session blob, §4.5 / §4.7.3).
        _session = (A2AAgentSession)await _a2aAgent.CreateSessionAsync(runId).ConfigureAwait(false);

        _logger.LogInformation(
            "RemoteAgentProxy: SetupAsync complete — run={RunId}, endpoint={Endpoint}, contextId={ContextId}",
            runId, podEndpointUri, _session.ContextId);
    }

    /// <inheritdoc />
    public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
    {
        if (_a2aAgent is null || _session is null)
        {
            throw new InvalidOperationException(
                "RemoteAgentProxy: SetupAsync must be called before RunTurnAsync.");
        }

        // Encode setup parameters as a JSON DataPart (first content part) so the pod's
        // CopilotAIAgent can call its own SetupAsync before executing the task.
        var setupParams = new AgentSetupParams
        {
            WorkingDirectory = _workingDirectory,
            RepositoryPath = _repositoryPath,
            RunId = _runId,
            ModelId = _modelId,
            SystemPromptContext = _systemPromptContext,
            ProjectId = _projectId,
            AgentName = _agentName,
            ApiBaseUrl = _apiBaseUrl,
            ApiKey = _apiKey,
            UserId = _userId,
            IsRevision = isRevision,
        };

        var setupJson = JsonSerializer.SerializeToUtf8Bytes(
            setupParams, AgentSetupParamsJsonContext.Default.AgentSetupParams);
        var setupPart = new DataContent(new ReadOnlyMemory<byte>(setupJson), AgentSetupParams.MediaType);
        var taskPart = new TextContent(task);

        // Single user message: [setup DataPart, task TextPart].
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new List<AIContent> { setupPart, taskPart })
        };

        // Stream the turn from the pod. The pod emits:
        //   - TextContent updates: assistant text deltas (accumulated for return value)
        //   - DataContent (RunEventDataPartCodec.MediaType): RunEvent side-channel events
        //     forwarded to _streamWriter → RecordingChannelWriter → RunStreamStore → SSE
        var textAccumulator = new StringBuilder();

        _logger.LogDebug(
            "RemoteAgentProxy: RunTurnAsync starting — run={RunId}, isRevision={IsRevision}",
            _runId, isRevision);

        await foreach (var update in _a2aAgent
            .RunStreamingAsync(messages, _session, options: null, ct)
            .ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent textContent &&
                    !string.IsNullOrEmpty(textContent.Text))
                {
                    textAccumulator.Append(textContent.Text);
                }
                else if (content is DataContent dataContent &&
                         _streamWriter is not null)
                {
                    // Decode RunEvent DataPart and forward to the worker's stream.
                    // Sequence is reassigned by RecordingChannelWriter, preserving total
                    // monotonic ordering on the worker side (§4.4).
                    var runEvent = RunEventDataPartCodec.TryDecodeRunEvent(dataContent);
                    if (runEvent is not null)
                    {
                        await _streamWriter.WriteAsync(runEvent, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        var responseText = textAccumulator.ToString();

        _logger.LogDebug(
            "RemoteAgentProxy: RunTurnAsync completed — run={RunId}, textLength={Length}",
            _runId, responseText.Length);

        return responseText;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Dispose the per-run HttpClient. The A2AAgent itself is not IDisposable in this version.
        _httpClient?.Dispose();
        _httpClient = null;
        _a2aAgent = null;
        _session = null;
        return ValueTask.CompletedTask;
    }
}
