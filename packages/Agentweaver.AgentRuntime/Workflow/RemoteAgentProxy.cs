using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using A2A;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.Domain;

namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>Worker-side deadlines for streaming A2A turns.</summary>
public sealed class RemoteAgentProxyOptions
{
    internal static readonly TimeSpan ReadIdleSafetyMargin = TimeSpan.FromMinutes(5);

    /// <summary>Absolute worker-side backstop for one pod turn. Zero disables it.</summary>
    public TimeSpan TotalTurnTimeout { get; set; } = TimeSpan.FromMinutes(70);

    /// <summary>
    /// Maximum gap between A2A stream updates. Zero disables it. The worker cannot see the pod's
    /// shell-aware liveness, so this defaults to the authoritative in-pod idle window plus a safety
    /// margin. Once this expires, the pod should already have emitted progress or a structured
    /// timeout; continued silence indicates a dead pod or transport.
    /// </summary>
    public TimeSpan ReadIdleTimeout { get; set; } =
        CopilotAIAgent.DefaultStreamIdleTimeout + ReadIdleSafetyMargin;

    /// <summary>Test seam for observing deadline timer lifetime.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } =
        static (delay, ct) => Task.Delay(delay, ct);
}

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
public sealed class RemoteAgentProxy : IWorkflowTurnAgent, IPreparedWritebackSource
{
    public const string StreamingHttpClientName = "a2a-sandbox-pod-streaming";

    private const string A2AAgentId = "agentweaver-worker-proxy";
    private const string A2AAgentName = "Agentweaver Worker Agent Proxy";
    private const string A2AAgentDescription = "Worker-side A2A proxy for sandbox pod CopilotAIAgent (spec-018 P1)";

    private readonly ISandboxAgentEndpointResolver _endpointResolver;
    private readonly IAgentHostTurnTokenRegistry? _turnTokenRegistry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteAgentProxy> _logger;
    private readonly RemoteAgentProxyOptions _options;
    private readonly string _remoteApiBaseUrl;

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
    private bool _preparedWritebackRequired;
    private bool _preparedWritebackEnvelopeSeen;
    private bool _preparedWritebackEnvelopeInvalid;
    private PreparedWriteback? _preparedWriteback;

    // Created in SetupAsync, used in RunTurnAsync, disposed in DisposeAsync.
    private A2AAgent? _a2aAgent;
    private A2AAgentSession? _session;
    private HttpClient? _httpClient;

    internal string RemoteApiBaseUrl => _remoteApiBaseUrl;

    public RemoteAgentProxy(
        ISandboxAgentEndpointResolver endpointResolver,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        string remoteApiBaseUrl,
        IAgentHostTurnTokenRegistry? turnTokenRegistry = null,
        RemoteAgentProxyOptions? options = null)
    {
        _endpointResolver = endpointResolver ?? throw new ArgumentNullException(nameof(endpointResolver));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _remoteApiBaseUrl = string.IsNullOrWhiteSpace(remoteApiBaseUrl)
            ? throw new ArgumentException("A remote API base URL is required.", nameof(remoteApiBaseUrl))
            : remoteApiBaseUrl;
        _turnTokenRegistry = turnTokenRegistry;
        _options = options ?? new RemoteAgentProxyOptions();
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
        _apiBaseUrl = _remoteApiBaseUrl;
        _apiKey = apiKey;
        _userId = userId;
        _preparedWritebackRequired = false;
        _preparedWritebackEnvelopeSeen = false;
        _preparedWritebackEnvelopeInvalid = false;
        _preparedWriteback = null;

        // Resolve the per-run pod's A2A base endpoint (e.g. https://10.0.0.5:8080/a2a/agent).
        // Supplied by ISandboxAgentEndpointResolver using the bound SandboxClaim pod name/IP.
        var podEndpointUri = await _endpointResolver.TryResolveEndpointAsync(runId, ct)
            .ConfigureAwait(false);

        if (podEndpointUri is null)
        {
            throw new WorkflowAgentInfrastructureException(
                "a2a_endpoint_unavailable",
                $"RemoteAgentProxy: no A2A endpoint found for run '{runId}'. " +
                "The sandbox pod may not yet be bound (IPodNameRegistry), " +
                "or ISandboxAgentEndpointResolver is not configured for this environment. " +
                "Set Sandbox:AgentExecutionMode=in-api to revert to in-process execution.");
        }

        _preparedWritebackRequired = await _endpointResolver
            .RequiresPreparedWritebackAsync(runId, ct)
            .ConfigureAwait(false);

        // Streaming has a dedicated infinite-transport-timeout client. The worker deadlines in
        // RunTurnAsync remain authoritative even if the pod dies after response headers arrive.
        _httpClient = _httpClientFactory.CreateClient(StreamingHttpClientName);
        var turnToken = _turnTokenRegistry?.TryGetTurnToken(runId);
        if (!string.IsNullOrEmpty(turnToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", turnToken);

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
        _preparedWritebackEnvelopeSeen = false;
        _preparedWritebackEnvelopeInvalid = false;
        _preparedWriteback = null;
        StructuredRunFailure? lastStructuredFailure = null;

        // #242 defense-in-depth (AgentHost terminal-emission gap): track whether the pod streamed
        // its definitive per-turn completion marker (agent.turn.end). If the A2A stream ends cleanly
        // WITHOUT it, the pod was torn down / the transport truncated mid-turn — the turn did NOT
        // finish, and returning a phantom success here would let the workflow complete with NO
        // terminal WorkflowOutputEvent, which the coordinator misreads as a false-positive stall.
        var sawTurnEnd = false;

        // Rolling trail of the last few update "shapes" seen on this turn. The A2A SDK
        // (Microsoft.Agents.AI.A2A.A2AAgent.RunCoreStreamingAsync) throws NotSupportedException
        // ("Received: None") when it dequeues a wire StreamResponse whose oneof payload is unset
        // (StreamResponseCase.None) — see #267. Empirically (see A2ARoundTripIntegrationTests) this
        // frame is emitted ONLY when the pod's A2A stream terminates abnormally / is truncated
        // (e.g. the pod turn threw); a normally-completing turn never produces it. It is therefore
        // an abnormal-stream-termination signal, NOT an "unsupported event kind" and NOT the
        // original 1.9.0-vs-1.11.1 version skew (that pin alignment already shipped in v0.9.43-rc1).
        // The pod bridge (A2ATurnBridgeAgent) now emits a synthetic structured RunFailed on any
        // otherwise-unstructured turn abort so this path carries a real errorCode; the trail below
        // still correlates the *next* bare occurrence with the content that immediately preceded it.
        var recentUpdateTrail = new Queue<string>();
        const int recentUpdateTrailCapacity = 5;

        void RecordUpdateTrail(string shape)
        {
            recentUpdateTrail.Enqueue(shape);
            while (recentUpdateTrail.Count > recentUpdateTrailCapacity)
                recentUpdateTrail.Dequeue();
        }

        _logger.LogDebug(
            "RemoteAgentProxy: RunTurnAsync starting — run={RunId}, isRevision={IsRevision}",
            _runId, isRevision);

        try
        {
            await foreach (var update in WithWorkerStreamDeadline(
                streamToken => _a2aAgent.RunStreamingAsync(
                    messages, _session, options: null, streamToken),
                _options,
                _runId,
                ct)
                .ConfigureAwait(false))
            {
                if (update.Contents.Count == 0)
                {
                    RecordUpdateTrail("empty-update");
                }

                foreach (var content in update.Contents)
                {
                    if (content is TextContent textContent &&
                        !string.IsNullOrEmpty(textContent.Text))
                    {
                        textAccumulator.Append(textContent.Text);
                        RecordUpdateTrail($"text:{textContent.Text.Length}");
                    }
                    else if (content is DataContent dataContent)
                    {
                        if (TryCapturePreparedWritebackEnvelope(dataContent))
                        {
                            RecordUpdateTrail("writeback-envelope");
                            continue;
                        }

                        // Decode RunEvent DataPart and forward to the worker's stream.
                        // Sequence is reassigned by RecordingChannelWriter, preserving total
                        // monotonic ordering on the worker side (§4.4).
                        var runEvent = RunEventDataPartCodec.TryDecodeRunEvent(dataContent);
                        if (runEvent is not null)
                        {
                            RecordUpdateTrail($"runEvent:{runEvent.Type}:{EstimatePayloadSize(runEvent.Payload)}b");

                            if (string.Equals(runEvent.Type, EventTypes.AgentTurnEnd, StringComparison.Ordinal))
                                sawTurnEnd = true;

                            var forwardedEvent = runEvent;
                            if (string.Equals(runEvent.Type, EventTypes.RunFailed, StringComparison.Ordinal))
                            {
                                if (StructuredRunFailureTerminal.TryRead(runEvent) is null)
                                {
                                    forwardedEvent =
                                        StructuredRunFailureTerminal.NormalizeUnstructuredFailure(runEvent);
                                    _logger.LogWarning(
                                        "RemoteAgentProxy: normalized an unstructured run.failed event for " +
                                        "run '{RunId}' to {ErrorCode}.",
                                        _runId,
                                        StructuredRunFailureTerminal.InternalErrorCode);
                                }

                                lastStructuredFailure =
                                    StructuredRunFailureTerminal.TryRead(forwardedEvent);
                            }

                            if (_streamWriter is not null)
                                await _streamWriter.WriteAsync(forwardedEvent, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            RecordUpdateTrail($"data:{dataContent.MediaType}:{dataContent.Data.Length}b");
                        }
                    }
                    else
                    {
                        RecordUpdateTrail($"content:{content.GetType().Name}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (WorkflowAgentInfrastructureException) { throw; }
        catch (Exception ex)
        {
            if (lastStructuredFailure is not null)
            {
                throw new WorkflowAgentInfrastructureException(
                    lastStructuredFailure.ErrorCode,
                    lastStructuredFailure.Message,
                    ex,
                    lastStructuredFailure.IsRetryable);
            }

            if (IsUnsupportedA2aEvent(ex))
            {
                var trail = recentUpdateTrail.Count > 0
                    ? string.Join(" -> ", recentUpdateTrail)
                    : "(no prior updates observed this turn)";
                var terminal = StructuredRunFailureTerminal.CreateInternalError(
                    "Agent turn ended unexpectedly before a structured terminal response was received.",
                    $"{ex.GetType().Name}: {ex.Message}; recent update trail: {trail}");

                _logger.LogWarning(
                    ex,
                    "RemoteAgentProxy: A2A stream ended with an unsupported or unset event for run " +
                    "'{RunId}'; synthesizing {ErrorCode}. Recent update trail: {Trail}",
                    _runId,
                    StructuredRunFailureTerminal.InternalErrorCode,
                    trail);

                if (_streamWriter is not null && !_streamWriter.TryWrite(terminal))
                {
                    _logger.LogWarning(
                        "RemoteAgentProxy: could not forward the synthesized {ErrorCode} terminal for " +
                        "run '{RunId}' because the run-event writer was closed.",
                        StructuredRunFailureTerminal.InternalErrorCode,
                        _runId);
                }

                throw new WorkflowAgentInfrastructureException(
                    StructuredRunFailureTerminal.InternalErrorCode,
                    "Agent turn ended unexpectedly before the pod reported a structured terminal failure. " +
                    $"Diagnostic: {StructuredRunFailureTerminal.SanitizeDiagnostic(ex.Message)} " +
                    $"(recent update trail: {trail})",
                    ex,
                    isRetryable: true);
            }

            throw new WorkflowAgentInfrastructureException(
                "a2a_transport_failure",
                $"RemoteAgentProxy: A2A turn failed for run '{_runId}': {ex.Message}",
                ex,
                isRetryable: IsTransientA2aTransportFailure(ex, ct));
        }

        // The A2A stream ended WITHOUT the pod ever faulting. Two residual seams must be closed here
        // so a pod-teardown / transport truncation cannot masquerade as a clean success and silently
        // drop the child's terminal WorkflowOutputEvent (issue #242 — false-positive stall root cause).
        //
        // (1) A structured run.failed was streamed but the stream then closed cleanly instead of
        //     faulting: surface it as the authoritative terminal failure rather than a phantom success.
        if (lastStructuredFailure is not null)
        {
            throw new WorkflowAgentInfrastructureException(
                lastStructuredFailure.ErrorCode,
                lastStructuredFailure.Message,
                innerException: null,
                lastStructuredFailure.IsRetryable);
        }

        // (2) The definitive per-turn completion marker (agent.turn.end) never arrived: the pod A2A
        //     stream ended (clean EOF) before the turn finished — a pod-teardown / checkpoint-resume /
        //     transport-truncation race. Fail RETRYABLY so the graph terminalizes VISIBLY (routed to
        //     the child-turn-failed terminal via emitTerminalFailureOutput) and the coordinator can
        //     consciously redispatch, instead of the workflow completing with no terminal event and
        //     the coordinator's stall detector firing a 5-minute false positive.
        if (!sawTurnEnd)
        {
            throw new WorkflowAgentInfrastructureException(
                "agent_host_turn_incomplete",
                $"RemoteAgentProxy: the pod A2A stream for run '{_runId}' completed without the terminal " +
                "'agent.turn.end' marker — the turn did not finish (pod teardown / transport truncation); " +
                "refusing to report a phantom success that would strand the run without a terminal event.",
                innerException: null,
                isRetryable: true);
        }

        var responseText = textAccumulator.ToString();

        _logger.LogDebug(
            "RemoteAgentProxy: RunTurnAsync completed — run={RunId}, textLength={Length}",
            _runId, responseText.Length);

        return responseText;
    }

    internal bool TryCapturePreparedWritebackEnvelope(DataContent content)
    {
        if (!PreparedWritebackDataPartCodec.IsWritebackContent(content))
            return false;

        if (_preparedWritebackEnvelopeSeen)
        {
            _preparedWritebackEnvelopeInvalid = true;
            _preparedWriteback = null;
            return true;
        }

        _preparedWritebackEnvelopeSeen = true;
        var envelope = PreparedWritebackDataPartCodec.DecodeEnvelope(content);
        _preparedWriteback = envelope.Writeback;
        _preparedWritebackEnvelopeInvalid =
            envelope.Status == PreparedWritebackEnvelopeStatus.Invalid;
        return true;
    }

    public PreparedWritebackEnvelope TakePreparedWritebackEnvelope()
    {
        var writeback = _preparedWriteback;
        var status = _preparedWritebackEnvelopeInvalid
            ? PreparedWritebackEnvelopeStatus.Invalid
            : _preparedWritebackEnvelopeSeen && writeback is not null
                ? PreparedWritebackEnvelopeStatus.Valid
                : _preparedWritebackRequired
                    ? PreparedWritebackEnvelopeStatus.Missing
                    : PreparedWritebackEnvelopeStatus.NotRequired;

        _preparedWritebackEnvelopeSeen = false;
        _preparedWritebackEnvelopeInvalid = false;
        _preparedWriteback = null;
        return new PreparedWritebackEnvelope(status, writeback);
    }

    internal static async IAsyncEnumerable<T> WithWorkerStreamDeadline<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        RemoteAgentProxyOptions options,
        string runId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(options);

        var totalTimeout = options.TotalTurnTimeout > TimeSpan.Zero
            ? options.TotalTurnTimeout
            : Timeout.InfiniteTimeSpan;
        var idleTimeout = options.ReadIdleTimeout > TimeSpan.Zero
            ? options.ReadIdleTimeout
            : Timeout.InfiniteTimeSpan;
        var totalDeadline = totalTimeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow.Add(totalTimeout);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var enumerator = streamFactory(streamCts.Token).GetAsyncEnumerator(streamCts.Token);
        Task<bool>? pendingMove = null;
        var abandonEnumerator = false;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var remainingTotal = totalDeadline - DateTimeOffset.UtcNow;
                if (remainingTotal <= TimeSpan.Zero)
                {
                    abandonEnumerator = true;
                    streamCts.Cancel();
                    throw CreateStreamTimeout(
                        "a2a_turn_timeout",
                        $"RemoteAgentProxy: A2A turn for run '{runId}' exceeded the worker total deadline of {totalTimeout.TotalMinutes:n0} minutes.",
                        runId);
                }

                pendingMove = enumerator.MoveNextAsync().AsTask();
                Task completed;
                Task idleTask;
                Task totalTask;
                using (var iterationCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idleTask = idleTimeout == Timeout.InfiniteTimeSpan
                        ? options.DelayAsync(Timeout.InfiniteTimeSpan, iterationCts.Token)
                        : options.DelayAsync(idleTimeout, iterationCts.Token);
                    totalTask = totalDeadline == DateTimeOffset.MaxValue
                        ? options.DelayAsync(Timeout.InfiniteTimeSpan, iterationCts.Token)
                        : options.DelayAsync(remainingTotal, iterationCts.Token);

                    try
                    {
                        completed = await Task.WhenAny(pendingMove, idleTask, totalTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        iterationCts.Cancel();
                    }

                    await ObserveCancelledDeadlineTasksAsync(idleTask, totalTask).ConfigureAwait(false);
                }

                if (ReferenceEquals(completed, pendingMove))
                {
                    var moved = await pendingMove.ConfigureAwait(false);
                    pendingMove = null;
                    if (!moved)
                        yield break;

                    yield return enumerator.Current;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                abandonEnumerator = true;
                streamCts.Cancel();

                if (ReferenceEquals(completed, totalTask))
                {
                    throw CreateStreamTimeout(
                        "a2a_turn_timeout",
                        $"RemoteAgentProxy: A2A turn for run '{runId}' exceeded the worker total deadline of {totalTimeout.TotalMinutes:n0} minutes.",
                        runId);
                }

                throw CreateStreamTimeout(
                    "a2a_stream_idle_timeout",
                    $"RemoteAgentProxy: A2A stream for run '{runId}' produced no update for {idleTimeout.TotalMinutes:n1} minutes; the pod or network may be unavailable.",
                    runId);
            }
        }
        finally
        {
            if (abandonEnumerator && pendingMove is not null)
            {
                await DisposeAbandonedStreamAsync(pendingMove, enumerator).ConfigureAwait(false);
            }
            else
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveCancelledDeadlineTasksAsync(Task idleTask, Task totalTask)
    {
        try
        {
            await Task.WhenAll(idleTask, totalTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The per-iteration deadline CTS intentionally tears down whichever delay lost the race.
        }
    }

    private static WorkflowAgentInfrastructureException CreateStreamTimeout(
        string reason,
        string message,
        string runId) =>
        new(reason, message, new TimeoutException($"Worker A2A stream deadline elapsed for run '{runId}'."), isRetryable: true);

    private static async Task DisposeAbandonedStreamAsync<T>(
        Task<bool> pendingMove,
        IAsyncEnumerator<T> enumerator)
    {
        var cleanupDelay = Task.Delay(TimeSpan.FromSeconds(1));
        if (ReferenceEquals(await Task.WhenAny(pendingMove, cleanupDelay).ConfigureAwait(false), pendingMove))
        {
            try
            {
                await pendingMove.ConfigureAwait(false);
            }
            catch
            {
                // The typed worker-side timeout is already being surfaced.
            }
        }

        try
        {
            var disposeTask = enumerator.DisposeAsync().AsTask();
            if (ReferenceEquals(
                    await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false),
                    disposeTask))
            {
                await disposeTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Deadline cleanup is best-effort and must never replace the typed timeout.
        }
    }

    internal static bool IsUnsupportedA2aEvent(Exception exception) =>
        exception is NotSupportedException &&
        exception.Message.Contains("Only message, task, task update events are supported", StringComparison.Ordinal);

    /// <summary>
    /// Cheap, best-effort size estimate for a <see cref="RunEvent"/> payload, used only for the
    /// diagnostic update-trail captured around #267 ("Received: None") failures. Never throws.
    /// </summary>
    internal static int EstimatePayloadSize(object payload)
    {
        try
        {
            return payload switch
            {
                null => 0,
                string s => s.Length,
                JsonElement je => je.GetRawText().Length,
                _ => JsonSerializer.Serialize(payload).Length,
            };
        }
        catch
        {
            return -1;
        }
    }

    internal static bool IsTransientA2aTransportFailure(Exception exception, CancellationToken callerCancellation)
    {
        if (callerCancellation.IsCancellationRequested)
            return false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or System.Net.Sockets.SocketException)
                return true;
            if (current is OperationCanceledException)
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Dispose the per-run HttpClient. The A2AAgent itself is not IDisposable in this version.
        _httpClient?.Dispose();
        _httpClient = null;
        _a2aAgent = null;
        _session = null;
        _preparedWritebackRequired = false;
        _preparedWritebackEnvelopeSeen = false;
        _preparedWritebackEnvelopeInvalid = false;
        _preparedWriteback = null;
        return ValueTask.CompletedTask;
    }
}
