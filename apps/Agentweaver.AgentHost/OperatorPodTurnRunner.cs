using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentHost;

/// <summary>
/// Pod-side <see cref="IPodTurnRunner"/> for the narrow AgentHost cutover (#346/#347): runs the
/// operator assistant's MCP/model chat loop (<see cref="IOperatorAssistantAgent"/>) instead of the
/// sandboxed <see cref="CopilotAIAgent"/> workflow turn. Selected by <see cref="RoutingPodTurnRunner"/>
/// when the pod was configured with <see cref="AgentHostPurpose.OperatorAssistant"/>.
///
/// <para>
/// The incoming A2A "task" string is a JSON <see cref="OperatorAssistantTurnEnvelope"/> (packed by
/// the worker-side <c>RemoteOperatorAssistantAgent</c>) carrying the operator message, replay
/// history, and the assembled agent definition — the shared <c>AgentSetupParams</c> transport only
/// has a single free-text task field, so the operator-specific bits travel inside it rather than
/// growing that shared contract.
/// </para>
///
/// <para>
/// Tool-call/result events are forwarded to the worker as plain <see cref="RunEvent"/>s over the
/// existing A2A RunEvent side-channel so the worker's own <c>IOperatorAssistantTurnSink</c> can
/// append them with the correct conversational message id. Tool-APPROVAL events are different: the
/// pod's own <see cref="IToolApprovalGate"/> is the sole source of truth for the pending request (its
/// <c>/tool-approvals</c> / <c>/tool-denials</c> endpoints are what actually unblocks the gated call),
/// so those three event types are also forwarded, but the worker appends them verbatim instead of
/// re-deriving them — mirroring exactly how a sandboxed coordinator subtask's HITL gate (e.g.
/// <c>web_fetch</c>) already round-trips through the existing <c>AgentHostApprovalHttpClient</c>
/// fallback.
/// </para>
/// </summary>
internal sealed class OperatorPodTurnRunner : IPodTurnRunner
{
    /// <summary>Mirrors <see cref="Agentweaver.Api.Assistant.AssistantRunService"/>'s approval wait
    /// bound so the pod's own gate times out on the same schedule as the (now bypassed) in-API one.</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApprovalHeartbeatInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOperatorAssistantAgent _assistant;
    private readonly AgentHostRuntimeState _runtimeState;
    private readonly IToolApprovalGate _approvalGate;
    private readonly ILogger<OperatorPodTurnRunner> _logger;
    private ChannelWriter<RunEvent>? _streamWriter;

    public OperatorPodTurnRunner(
        IOperatorAssistantAgent assistant,
        AgentHostRuntimeState runtimeState,
        IToolApprovalGate approvalGate,
        ILogger<OperatorPodTurnRunner> logger)
    {
        _assistant = assistant ?? throw new ArgumentNullException(nameof(assistant));
        _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
        _approvalGate = approvalGate ?? throw new ArgumentNullException(nameof(approvalGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => _streamWriter = streamWriter;

    public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken)
    {
        OperatorAssistantTurnEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<OperatorAssistantTurnEnvelope>(task, JsonOptions)
                ?? throw new InvalidOperationException("Empty operator turn envelope.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "OperatorPodTurnRunner: failed to decode the operator turn envelope from the A2A task payload.", ex);
        }

        var request = new OperatorAssistantRequest(
            ConversationId: _runtimeState.RunId,
            Message: envelope.Message,
            CallerUser: _runtimeState.UserId,
            GitHubLogin: envelope.GitHubLogin,
            ProjectId: _runtimeState.ProjectId,
            RunId: envelope.ContextRunId,
            ModelId: null,
            AgentDefinition: envelope.AgentDefinition,
            // New API versions provide the platform caller credential separately from the linked
            // GitHub token. Fall back during rolling upgrades from an older API.
            CallerBearerToken: _runtimeState.CallerBearerToken
                ?? _runtimeState.GitHubAccessToken
                ?? string.Empty,
            History: envelope.History);

        // Fail closed rather than silently degrading to an ungated turn: OperatorAssistantAgent only
        // gates consequential MCP tool calls when it is given a non-null sink (a null sink means "no
        // run stream to raise the approval on", so gating is skipped and the tool just runs — see
        // OperatorAssistantAgent.BuildToolDeclarations). The bridge always attaches a stream writer
        // before running a turn; a missing writer here indicates a wiring defect, not an
        // "ungated is fine" situation, so refuse the turn instead of running consequential tools
        // without a human gate.
        var writer = _streamWriter
            ?? throw new InvalidOperationException(
                "OperatorPodTurnRunner: no turn stream writer attached — refusing to run the operator " +
                "turn without a sink, since that would let consequential MCP tool calls run ungated.");
        var sink = new PodOperatorAssistantTurnSink(writer, _approvalGate, _runtimeState.RunId, _logger);

        var response = await _assistant.RunTurnAsync(request, sink, cancellationToken).ConfigureAwait(false);

        await writer.WriteAsync(new RunEvent(0, EventTypes.AgentTurnEnd, new { turnId = "0" }), cancellationToken)
            .ConfigureAwait(false);

        return response.Message;
    }

    public Task ForceStopTurnAsync() => Task.CompletedTask;

    /// <summary>
    /// Translates <see cref="IOperatorAssistantTurnSink"/> callbacks into <see cref="RunEvent"/>s on
    /// the pod's per-turn side-channel. Tool call/result events are informational (the worker's own
    /// sink re-derives the durable event); approval events carry the pod-local
    /// <see cref="IToolApprovalGate"/> request id verbatim since that gate — not the worker's — is
    /// what the operator's grant/deny decision ultimately resolves.
    /// </summary>
    private sealed class PodOperatorAssistantTurnSink(
        ChannelWriter<RunEvent> writer,
        IToolApprovalGate approvalGate,
        string runId,
        ILogger logger) : IOperatorAssistantTurnSink
    {
        public ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken ct) =>
            writer.WriteAsync(
                new RunEvent(0, EventTypes.ToolCall, new { name = toolName, arguments = argumentsJson }), ct);

        public ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken ct) =>
            writer.WriteAsync(
                new RunEvent(0, success ? EventTypes.ToolResult : EventTypes.ToolError, new { name = toolName, success }), ct);

        public async ValueTask<bool> OnApprovalRequiredAsync(
            string requestId, string toolName, string? argumentsJson, CancellationToken ct)
        {
            var displayId = requestId.Length >= 8 ? requestId[..8] : requestId;

            // Register the wait FIRST (synchronously, before the first await) so a fast operator
            // decision arriving via /tool-approvals right after the tool.approval_required event
            // reaches the frontend cannot race ahead of the gate registering the request.
            var waitTask = approvalGate.WaitForApprovalAsync(runId, requestId, toolName, url: null, ApprovalTimeout, ct);

            await writer.WriteAsync(new RunEvent(0, EventTypes.ToolApprovalRequired, new
            {
                requestId,
                displayId,
                toolName,
                arguments = argumentsJson,
                message = $"The assistant wants to run {toolName}. Operator approval required.",
            }), ct).ConfigureAwait(false);

            while (!waitTask.IsCompleted)
            {
                var heartbeat = Task.Delay(ApprovalHeartbeatInterval, ct);
                var completed = await Task.WhenAny(waitTask, heartbeat).ConfigureAwait(false);
                if (completed == waitTask)
                    break;
                await writer.WriteAsync(
                    new RunEvent(0, EventTypes.ToolApprovalPending, new { requestId, displayId, toolName }), ct)
                    .ConfigureAwait(false);
            }

            var approved = await waitTask.ConfigureAwait(false);

            await writer.WriteAsync(
                new RunEvent(0, EventTypes.ToolApprovalResolved, new { requestId, runId, approved }), ct)
                .ConfigureAwait(false);

            logger.LogInformation(
                "OperatorPodTurnRunner: tool-approval {RequestId} for {ToolName} on run {RunId} resolved: approved={Approved}",
                displayId, toolName, runId, approved);

            return approved;
        }
    }
}

/// <summary>
/// Selects, per turn, between the sandboxed <see cref="CopilotPodTurnRunner"/> (Coordinator/workflow
/// purposes) and the <see cref="OperatorPodTurnRunner"/> (narrow AgentHost cutover, #346/#347), based
/// on the pod's configured <see cref="AgentHostPurpose"/>. The pod hosts a single
/// <c>A2ATurnBridgeAgent</c> instance built once at startup, before <c>/configure</c> — and therefore
/// before the run's Purpose is known — so the choice must be made lazily per call rather than at
/// construction time.
/// </summary>
internal sealed class RoutingPodTurnRunner(
    IPodTurnRunner copilotRunner,
    IPodTurnRunner operatorRunner,
    AgentHostRuntimeState runtimeState) : IPodTurnRunner
{
    private IPodTurnRunner Active =>
        runtimeState.Purpose == AgentHostPurpose.OperatorAssistant ? operatorRunner : copilotRunner;

    public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) =>
        Active.SetTurnStreamWriter(streamWriter);

    public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken cancellationToken) =>
        Active.RunTurnAsync(task, isRevision, cancellationToken);

    public bool ApplyPerTurnContext(
        string? systemPromptContext,
        string? projectId,
        string? agentName,
        string? apiBaseUrl = null,
        string? apiKey = null) =>
        Active.ApplyPerTurnContext(systemPromptContext, projectId, agentName, apiBaseUrl, apiKey);

    public Task ForceStopTurnAsync() => Active.ForceStopTurnAsync();
}
