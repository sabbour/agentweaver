using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Providers;
using Scaffolder.AgentTools;
using Scaffolder.Domain;
using Scaffolder.SandboxExec;
using Scaffolder.SandboxFs;

namespace Scaffolder.AgentRuntime;

/// <summary>
/// An <see cref="AIAgent"/> implementation that wraps the GitHub Copilot SDK
/// (<c>GitHubCopilotAgent</c>) and threads the SDK session through MAF's checkpoint
/// machinery. By overriding <see cref="SerializeSessionCoreAsync"/> and
/// <see cref="DeserializeSessionCoreAsync"/> to delegate to the inner agent, the Copilot
/// SDK session state is persisted into the workflow's FileSystem checkpoint alongside the
/// workflow state — enabling process-restart durability instead of the fire-and-forget
/// session pattern used by <see cref="GitHubCopilotAgentRunner"/>.
///
/// <para>
/// All governance/sandbox/event-emission logic from <see cref="GitHubCopilotAgentRunner"/>
/// is preserved here. A single turn is executed via <see cref="ExecuteStreamingLoopAsync"/>,
/// which iterates the inner agent's <c>RunStreamingAsync</c> and emits the same run events.
/// </para>
///
/// <para>
/// Lifecycle: <see cref="SetupAsync"/> must be called (by the workflow executor) before
/// <see cref="CreateSessionCoreAsync"/> / <see cref="ExecuteStreamingLoopAsync"/>. One
/// instance is created per workflow build (per run); it owns its inner agent and client and
/// disposes them via <see cref="DisposeAsync"/>.
/// </para>
/// </summary>
public class CopilotAIAgent : AIAgent, IAsyncDisposable, Workflow.IWorkflowTurnAgent
{
    /// <summary>
    /// Scaffolder API tool names that are auto-approved without sandbox governance.
    /// The HTTP call executes in the function body after approval.
    /// </summary>
    private static readonly HashSet<string> ScaffolderApiToolNames = new(StringComparer.Ordinal)
    {
        "submit_decision", "record_memory", "update_session", "submit_inbox_entry",
        "list_inbox", "merge_inbox_entry", "export_memory",
    };

    // Universal runtime contract. Agent identity and tool-usage guidance live in the charter.

    /// <summary>
    /// SDK-internal tools whose lifecycle events are suppressed from the run stream.
    /// These are housekeeping operations (not sandboxed file/shell ops) that would
    /// confuse the frontend if rendered as ToolCallCards. This static allowlist is the
    /// sole suppress decision source — never driven by model-controlled strings.
    /// </summary>
    private static readonly HashSet<string> SuppressedInternalTools =
        new(StringComparer.OrdinalIgnoreCase) { "report_intent", "report_outcome", "glob" };

    private readonly GitHubCopilotClientFactory _factory;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly ISandboxExecutor _executor;
    private readonly ISandboxPolicyStore _sandboxPolicyStore;
    private readonly IShellApprovalStore _approvalStore;
    private readonly IToolApprovalGate _toolApprovalGate;
    protected readonly ILogger<CopilotAIAgent> _logger;

    // --- Per-run config — set by the workflow executor before CreateSessionAsync ---
    protected string _workingDirectory = "";
    protected string _repositoryPath = "";
    protected string _runId = "";
    protected string? _modelId;
    protected string? _systemPromptContext;
    protected string? _projectId;
    protected string? _agentName;
    protected string? _apiBaseUrl;
    protected string? _apiKey;

    /// <summary>The run-event channel writer for the current run (null when no stream attached).</summary>
    public ChannelWriter<RunEvent>? StreamWriter { get; private set; }

    // --- Runtime objects created during SetupAsync — kept alive for serialize/deserialize ---
    private CopilotClient? _client;
    private AIAgent? _inner; // the GitHubCopilotAgent
    private SandboxGovernance? _governance;
    private SandboxToolContext? _toolContext;
    private ISandboxExecutor? _activeExecutor;
    private SandboxPolicy? _sandboxPolicy;

    // --- Per-run run-event emission state (reset in SetupAsync) ---
    private StringBuilder _sb = new();
    private int _seq;
    private readonly object _emitLock = new();
    private int _deltaCount;
    private HashSet<string> _streamedMessageIds = new(StringComparer.Ordinal);
    private bool _anyDeltaEmittedForNullId;
    private ConcurrentDictionary<string, byte> _emittedCalls = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, byte> _emittedTerminals = new(StringComparer.Ordinal);
    private HashSet<string> _suppressedCallIds = new(StringComparer.Ordinal);

    // Sandbox-degradation tracking. The permission handler (which fires on SDK callback
    // threads) records that at least one tool call was denied, plus the first deny reason.
    // run.degraded is emitted exactly once via EmitRunDegradedOnce; _runDegradedEmitted is
    // the 0/1 Interlocked guard. ExecuteStreamingLoopAsync performs a guaranteed flush of
    // this signal AFTER the streaming loop but BEFORE agent.turn.end, so run.degraded is
    // always ordered ahead of the run's completion/await events (and therefore ahead of the
    // SSE `done` sentinel). Without this, a deny emitted late by an out-of-band callback
    // could land in history after live clients already stopped reading on `done` — surfacing
    // green live but amber ("Incomplete") only after a refresh replays the full history.
    private volatile bool _degradedFlagged;
    private string? _degradedToolName;
    private string? _degradedReason;
    private int _runDegradedEmitted;

    public CopilotAIAgent(
        GitHubCopilotClientFactory factory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _toolApprovalGate = toolApprovalGate ?? throw new ArgumentNullException(nameof(toolApprovalGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Provisions the governance kernel, Copilot client, sandbox tool context, and inner
    /// <c>GitHubCopilotAgent</c> for a single run. Must be called before
    /// <see cref="CreateSessionCoreAsync"/> or <see cref="ExecuteStreamingLoopAsync"/>.
    /// </summary>
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
        CancellationToken ct)
    {
        _workingDirectory = workingDirectory;
        _repositoryPath = repositoryPath;
        _runId = runId;
        _modelId = modelId;
        _systemPromptContext = systemPromptContext;
        StreamWriter = streamWriter;
        _projectId = projectId;
        _agentName = agentName;
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;

        // Reset per-run emission state so a reused instance never leaks events across runs.
        _sb = new StringBuilder();
        _seq = 0;
        _deltaCount = 0;
        _streamedMessageIds = new HashSet<string>(StringComparer.Ordinal);
        _anyDeltaEmittedForNullId = false;
        _emittedCalls = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        _emittedTerminals = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        _suppressedCallIds = new HashSet<string>(StringComparer.Ordinal);
        _degradedFlagged = false;
        _degradedToolName = null;
        _degradedReason = null;
        _runDegradedEmitted = 0;

        _logger.LogInformation(
            "SetupAsync entered — workingDirectory={WorkingDirectory}, runId={RunId}, streamIsNull={StreamIsNull}",
            workingDirectory, runId, streamWriter is null);

        // --- Governance kernel (per-run) ---
        var sandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(repositoryPath, ct).ConfigureAwait(false);
        _sandboxPolicy = sandboxPolicy;
        var executor = sandboxPolicy.Direct
            ? new PassthroughExecutor("direct execution — sandbox disabled via settings.yml", _logger)
            : _executor;
        _activeExecutor = executor;
        _governance = SandboxGovernance.Create(workingDirectory, runId, executor, sandboxPolicy, _logger);

        var scope = _scopeProvider.Resolve(userId: null);
        _client = await _factory.CreateClientAsync(scope, modelId, ct).ConfigureAwait(false);
        await _client.StartAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Copilot client started");

        var fileTools = new SandboxedFileTools(workingDirectory);
        var searchTools = new SandboxedSearchTools(workingDirectory);
        var redactor = SandboxOutputRedactor.Default;
        var agentId = $"did:mesh:scaffolder:copilot:{runId}";

        var toolOptions = new SandboxToolOptions(
            ShellEnabled: sandboxPolicy.ShellEnabled)
        {
            AllowedRepositoryRoots = [.. sandboxPolicy.AllowedRepositoryRoots],
            DestructiveCommandPatterns = [.. sandboxPolicy.DestructiveCommandPatterns],
            RequireApprovalForAllShell = sandboxPolicy.RequireApprovalForAllShell,
            NetworkEnabled = sandboxPolicy.NetworkEnabled,
        };
        var toolContext = new SandboxToolContext(
            AgentId: agentId,
            WorkingDirectory: workingDirectory,
            SandboxRoot: workingDirectory,
            Executor: executor,
            FileTools: fileTools,
            SearchTools: searchTools,
            Redactor: redactor,
            Options: toolOptions,
            Logger: _logger,
            EmitEvent: Emit,
            RunId: runId,
            IsCommandApproved: hash => _approvalStore.IsApproved(runId, hash),
            IsCommandDenied: hash => _approvalStore.IsDenied(runId, hash));
        _toolContext = toolContext;

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = BuildPermissionHandler(_governance, runId, workingDirectory, EmitToolCallOnce, EmitToolErrorOnce, Emit, ct),
            WorkingDirectory = workingDirectory,
            EnableConfigDiscovery = false,
            Streaming = true,
            // Deterministic session ID enables history replay via ResumeSessionAsync.
            // Format: "scaffolder-run-{runId}" — unique per run, stable across restarts.
            SessionId = $"scaffolder-run-{runId}",
            Tools = BuildSessionConfigTools(toolContext, _projectId, _agentName, _apiBaseUrl, _apiKey),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = string.IsNullOrEmpty(systemPromptContext)
                    ? AgentBasePrompt.Base
                    : AgentBasePrompt.Base + "\n\n" + systemPromptContext,
            },
            Model = modelId,
        };

        _inner = _client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);

        _logger.LogInformation("Inner Copilot AIAgent created with sandbox governance — runId={RunId}", runId);
    }

    // ----- AIAgent abstract overrides: delegate to the inner GitHubCopilotAgent -----

    /// <summary>MAF entry point to create the initial session. Delegates to the inner agent.</summary>
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before CreateSessionAsync.");
        return _inner.CreateSessionAsync(cancellationToken);
    }

    /// <summary>
    /// Resumes an existing Copilot SDK session so the agent retains conversation history
    /// across reviewer-requested-changes revision cycles. Uses the deterministic session ID
    /// (<c>scaffolder-run-{runId}</c>) set during <see cref="SetupAsync"/>.
    /// </summary>
    public async ValueTask<AgentSession> ResumeSessionAsync(CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before ResumeSessionAsync.");
        // SessionId is already set in SessionConfig ("scaffolder-run-{runId}") so the SDK
        // resumes the persisted session automatically — no raw overload needed.
        return await _inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a single agent turn: creates (or, for a revision, resumes) the SDK session and
    /// drives the streaming loop, returning the accumulated assistant text. This is the
    /// <see cref="Workflow.IWorkflowTurnAgent"/> seam used by the workflow turn executors.
    /// </summary>
    public async Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
    {
        var session = isRevision
            ? await ResumeSessionAsync(ct).ConfigureAwait(false)
            : await CreateSessionAsync(ct).ConfigureAwait(false);
        return await ExecuteStreamingLoopAsync(task, session, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the agent for a turn (non-streaming). Delegates to the inner agent.</summary>
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before running the agent.");
        return _inner.RunAsync(messages, session, options, cancellationToken);
    }

    /// <summary>Runs the agent for a turn (streaming). Delegates to the inner agent.</summary>
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before running the agent.");
        return _inner.RunStreamingAsync(messages, session, options, cancellationToken);
    }

    /// <summary>
    /// Checkpoints the Copilot SDK session by delegating to the inner agent. This is the
    /// core capability the refactor enables: the SDK session lands in the MAF checkpoint.
    /// </summary>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession? session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("No inner agent to serialize — SetupAsync was not called.");
        return _inner.SerializeSessionAsync(session!, jsonSerializerOptions, cancellationToken);
    }

    /// <summary>
    /// Restores a Copilot SDK session from a checkpoint. On resume in a fresh process,
    /// <see cref="SetupAsync"/> may not have run yet; in that case a minimal inner agent is
    /// created solely to deserialize the session state.
    /// </summary>
    protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
    {
        if (_inner is null)
        {
            var scope = _scopeProvider.Resolve(userId: null);
            _client ??= await _factory.CreateClientAsync(scope, _modelId, cancellationToken).ConfigureAwait(false);
            await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            _inner = _client.AsAIAgent(
                new SessionConfig { SessionId = $"scaffolder-run-{_runId}" },
                ownsClient: false, id: null, name: null, description: null);
        }
        return await _inner.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the agent for one turn: emits the sandbox/config snapshot events, iterates the
    /// inner agent's <c>RunStreamingAsync</c>, emits all stream events (deltas, tool calls,
    /// results, errors), and returns the accumulated assistant text.
    /// <see cref="SetupAsync"/> must have been called first.
    /// </summary>
    public async Task<string> ExecuteStreamingLoopAsync(string task, AgentSession session, CancellationToken ct)
    {
        if (_inner is null || _activeExecutor is null || _sandboxPolicy is null)
            throw new InvalidOperationException("SetupAsync must be called before ExecuteStreamingLoopAsync.");

        var executor = _activeExecutor;
        var sandboxPolicy = _sandboxPolicy;

        // --- Emit sandbox backend selection event (T019) ---
        Emit("sandbox.selected", new { backend = executor.BackendName, isRealIsolation = executor.IsRealIsolation, reason = executor.SelectionReason });

        // Emit configuration snapshot for debuggability.
        var fullSystemPrompt = string.IsNullOrEmpty(_systemPromptContext)
            ? AgentBasePrompt.Base
            : AgentBasePrompt.Base + "\n\n" + _systemPromptContext;
        Emit("agent.system_prompt", new { provider = "copilot", prompt = fullSystemPrompt, memoryContextIncluded = !string.IsNullOrEmpty(_systemPromptContext) });
        Emit("agent.task", new { task });
        Emit("agent.tools", new { provider = "copilot", tools = new[] { "bash (native)", "read_file (native)", "write_file (native)", "create_file (native)", "str_replace_editor (native)", "grep (native)", "glob (native)", "report_intent (custom)", "report_outcome (custom)" } });
        if (executor.HasNetworkWarning)
        {
            Emit("sandbox.warning", new { category = "network-open", message = executor.NetworkWarningMessage, backend = executor.BackendName });
        }
        if (sandboxPolicy.NetworkEnabled)
        {
            Emit("sandbox.warning", new
            {
                category = "network-open",
                message = "Sandbox is running with outbound network enabled (network_enabled: true in .scaffolder/settings.yml). " +
                          "Network access is intentional but increases the attack surface. " +
                          "Ensure this is required for the agent's task.",
                backend = executor.BackendName
            });
        }

        try
        {
            await foreach (var chunk in _inner.RunStreamingAsync(task, session, options: null, ct).WithCancellation(ct))
            {
                if (chunk is null) continue;

                var messageId = chunk.MessageId;

                // Incremental token text surfaces as TextContent (AssistantMessageDeltaEvent).
                var deltaText = chunk.Text;
                if (!string.IsNullOrEmpty(deltaText))
                {
                    EmitDelta(deltaText, messageId);
                    if (messageId is not null) _streamedMessageIds.Add(messageId);
                }

                // The final, authoritative message arrives as a non-text AIContent whose
                // RawRepresentation is the SDK AssistantMessageEvent. Surface its content when
                // no token deltas were streamed for this message, so text is never lost (and is
                // not double-counted when deltas already covered it).
                var finalContent = ExtractFinalMessageContent(chunk);
                if (!string.IsNullOrEmpty(finalContent))
                {
                    var alreadyStreamed = messageId is not null
                        ? _streamedMessageIds.Contains(messageId)
                        : _anyDeltaEmittedForNullId;

                    if (!alreadyStreamed)
                    {
                        EmitDelta(finalContent, messageId);
                        if (messageId is not null) _streamedMessageIds.Add(messageId);
                    }
                    else if (messageId is null)
                    {
                        _logger.LogWarning("Final message with null messageId skipped — delta text was already emitted");
                    }
                }

                if (string.IsNullOrEmpty(deltaText) && string.IsNullOrEmpty(finalContent))
                    _logger.LogTrace("RunStreamingAsync non-text chunk — messageId={MessageId}", messageId);

                // The SDK tool-execution lifecycle arrives inline as content raw representations.
                if (chunk.Contents is not null)
                {
                    foreach (var c in chunk.Contents)
                        TranslateToolLifecycle(c.RawRepresentation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunStreamingAsync threw for workingDirectory={WorkingDirectory}", _workingDirectory);
            Emit("run.failed", new { message = "The agent encountered an internal error." });
            throw;
        }

        // Guaranteed flush: if the sandbox denied any tool call this turn, ensure run.degraded
        // is in the run history BEFORE agent.turn.end (and thus before the workflow's terminal
        // /await-review events and the SSE `done` sentinel). The deny branches already emit it
        // inline; this is the dedup-safe safety net that closes the cross-thread window where a
        // permission callback's emit could otherwise interleave after completion. Without it,
        // live clients can stop reading on `done` and miss the event, showing green live while a
        // later refresh (full-history replay) shows the amber "Incomplete" badge.
        if (_degradedFlagged)
            EmitRunDegradedOnce(_degradedToolName ?? "unknown", _degradedReason ?? "Sandbox denied a tool call.");

        Emit("agent.turn.end", new { turnId = "0" });

        if (_suppressedCallIds.Count > 0)
            _logger.LogInformation("Suppressed {Count} SDK-internal tool events", _suppressedCallIds.Count);

        var result = _sb.ToString();
        _logger.LogInformation(
            "Run complete — deltaCount={DeltaCount}, resultLength={ResultLength}",
            _deltaCount, result.Length);

        return result;
    }

    // ----- Thread-safe run-event emission -----
    // The permission handler fires on SDK callback threads concurrently with the MAF
    // streaming loop, so the sequence increment and the channel write are taken under one
    // lock. This keeps event sequence numbers monotonic AND in arrival order.

    private void Emit(string type, object payload)
    {
        var stream = StreamWriter;
        if (stream is null) return;
        lock (_emitLock)
        {
            if (!stream.TryWrite(new RunEvent(++_seq, type, payload)))
                _logger.LogWarning("TryWrite false for {EventType}", type);
        }
    }

    private void EmitToolCallOnce(string callId, string toolName, object? arguments)
    {
        if (_emittedCalls.TryAdd(callId, 0))
            Emit("tool.call", new { callId, toolName, arguments });
    }

    private void EmitToolResultOnce(string callId, string content)
    {
        EmitToolCallOnce(callId, "unknown", null); // defensive call-before-result
        if (_emittedTerminals.TryAdd(callId, 0))
            Emit("tool.result", new { callId, content });
    }

    private void EmitToolErrorOnce(string callId, string errorMessage)
    {
        EmitToolCallOnce(callId, "unknown", null); // defensive call-before-error
        if (_emittedTerminals.TryAdd(callId, 0))
            Emit("tool.error", new { callId, errorMessage });
    }

    /// <summary>
    /// Records a sandbox denial and emits <c>run.degraded</c> at most once per run.
    /// Called from the permission handler (SDK callback threads) at each deny branch and
    /// once more as a guaranteed flush at the end of the streaming loop. The first caller
    /// wins the emit; later calls only ensure the degraded state is captured. Emitting from
    /// the deny branch keeps the event adjacent to its tool.error; the end-of-turn flush
    /// guarantees the event is in history BEFORE agent.turn.end and the run's completion
    /// events, so live SSE clients always receive it ahead of the `done` sentinel.
    /// </summary>
    private void EmitRunDegradedOnce(string toolName, string reason)
    {
        _degradedFlagged = true;
        _degradedToolName ??= toolName;
        _degradedReason ??= reason;
        if (Interlocked.Exchange(ref _runDegradedEmitted, 1) == 0)
            Emit(EventTypes.RunDegraded, new { toolName, reason });
    }

    private void EmitDelta(string text, string? messageId)
    {
        _sb.Append(text);
        if (StreamWriter is null) return;
        Emit("agent.message.delta", new { delta = text, messageId });
        _deltaCount++;
        if (messageId is null) _anyDeltaEmittedForNullId = true;
    }

    /// <summary>
    /// Translates the SDK tool-execution lifecycle (delivered inline through the MAF stream
    /// as chunk content raw representations) into individual tool.call / tool.result /
    /// tool.error run events. Observe-only: it never alters execution. The result content
    /// is the SDK's own execution output for an approved (in-sandbox) call — nothing is
    /// fabricated. Denied calls never execute, so they never reach this path.
    /// </summary>
    private void TranslateToolLifecycle(object? raw)
    {
        switch (raw)
        {
            case ToolExecutionStartEvent start when start.Data is not null:
            {
                var callId = start.Data.ToolCallId ?? Guid.NewGuid().ToString("n");
                var toolName = start.Data.ToolName ?? "";

                // Translate report_intent into an agent.intent event BEFORE general suppression.
                if (string.Equals(toolName, "report_intent", StringComparison.OrdinalIgnoreCase))
                {
                    _suppressedCallIds.Add(callId);
                    try
                    {
                        var argsStr = start.Data.Arguments is string argStr
                            ? argStr
                            : JsonSerializer.Serialize(start.Data.Arguments);
                        using var doc = JsonDocument.Parse(argsStr);
                        if (doc.RootElement.TryGetProperty("intent", out var intentEl))
                        {
                            var intentText = intentEl.GetString();
                            if (!string.IsNullOrWhiteSpace(intentText))
                                Emit("agent.intent", new { intent = intentText });
                        }
                    }
                    catch { /* non-fatal: suppress raw event even if parsing fails */ }
                    break;
                }

                if (SuppressedInternalTools.Contains(toolName))
                {
                    _suppressedCallIds.Add(callId);
                    break;
                }
                EmitToolCallOnce(callId, toolName.Length > 0 ? toolName : "unknown", start.Data.Arguments);
                break;
            }
            case ToolExecutionCompleteEvent complete when complete.Data is not null:
            {
                var callId = complete.Data.ToolCallId ?? Guid.NewGuid().ToString("n");
                if (_suppressedCallIds.Contains(callId))
                    break;
                if (complete.Data.Success)
                    EmitToolResultOnce(callId, complete.Data.Result?.Content ?? string.Empty);
                else
                    EmitToolErrorOnce(callId, complete.Data.Error?.Message ?? "Tool execution failed.");
                break;
            }
        }
    }

    /// <summary>
    /// Builds the permission handler that enforces sandbox containment through
    /// two independent layers: AGT policy evaluation AND direct SandboxPolicyBackend check.
    /// Both must allow for the tool call to proceed. The handler is also a per-tool
    /// observability source. A denied call is surfaced here as a tool.call + tool.error pair
    /// carrying the gate reason, because denied calls never execute and so never reach the
    /// streaming tool-execution lifecycle. An approved call is surfaced from that lifecycle in
    /// the streaming loop (call + real result); the handler only co-emits its tool.call when it
    /// holds the SDK's real ToolCallId, so the two sources dedup instead of diverging.
    /// </summary>
    internal PermissionRequestHandler BuildPermissionHandler(
        SandboxGovernance governance,
        string runId,
        string workingDirectory,
        Action<string, string, object?> emitToolCallOnce,
        Action<string, string> emitToolErrorOnce,
        Action<string, object> emit,
        CancellationToken runCt)
    {
        return (request, invocation) =>
        {
            // URL fetch (web_fetch) — surface a HITL approval gate rather than silently denying.
            if (request is PermissionRequestUrl urlRequest)
            {
                var urlCallId = urlRequest.ToolCallId ?? Guid.NewGuid().ToString("n");
                var requestId = urlCallId;  // full ID is the key — no truncation, no collision risk
                var displayId = requestId.Length >= 8 ? requestId[..8] : requestId;
                var rawUrl = urlRequest.Url ?? "";
                var intention = urlRequest.Intention ?? "";

                emitToolCallOnce(urlCallId, "web_fetch", new Dictionary<string, object>
                {
                    ["url"] = rawUrl,
                });

                // Short-circuit: skip the HITL card if a run-scoped or always-allowed policy already covers this tool+URL.
                if (_toolApprovalGate.IsAutoApproved(runId, "web_fetch", rawUrl))
                {
                    _logger.LogInformation(
                        "Tool HITL auto-approved (policy) — url={Url} runId={RunId}",
                        rawUrl.Length > 80 ? rawUrl[..80] : rawUrl, runId);
                    return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });
                }

                // Atomically register context and gate in one call so GrantAsync can record
                // scope-based allow policies even if approval arrives immediately after registration.
                var approvalTask = _toolApprovalGate.WaitForApprovalAsync(runId, requestId, "web_fetch", rawUrl, TimeSpan.FromMinutes(5), runCt);

                emit(EventTypes.ToolApprovalRequired, new
                {
                    requestId,
                    displayId,
                    toolName = "web_fetch",
                    url = SanitizeUrl(rawUrl),
                    intention = SanitizeIntent(intention),
                    message = "The agent wants to fetch a URL. Operator approval required.",
                });

                _logger.LogInformation(
                    "Tool HITL gate — waiting for operator approval: requestId={RequestId} url={Url} runId={RunId}",
                    displayId, rawUrl.Length > 80 ? rawUrl[..80] : rawUrl, runId);

                var approved = approvalTask.ConfigureAwait(false).GetAwaiter().GetResult();

                if (!approved)
                {
                    emitToolErrorOnce(urlCallId, "URL fetch was denied by the operator.");
                    _logger.LogInformation("Tool HITL denied — requestId={RequestId} runId={RunId}", displayId, runId);
                    return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Rejected });
                }

                _logger.LogInformation("Tool HITL approved — requestId={RequestId} runId={RunId}", displayId, runId);
                return Task.FromResult(new PermissionRequestResult { Kind = PermissionRequestResultKind.Approved });
            }

            // Custom external tools registered in SessionConfig.Tools fire OnPermissionRequest
            // with PermissionRequestCustomTool. Run governance against the tool name + args
            // from the request — same two-layer check as native tools — before approving.
            if (request is PermissionRequestCustomTool customTool)
            {
                var realCustomCallId = customTool.ToolCallId;
                var customCallId = realCustomCallId ?? Guid.NewGuid().ToString("n");
                var toolName = customTool.ToolName ?? "unknown";
                try
                {
                    // report_intent is a side-effect-free observability call: approve without
                    // governance, emit agent.intent (not tool.call / tool.result), and return.
                    if (string.Equals(toolName, "report_intent", StringComparison.Ordinal))
                    {
                        string intentRaw = "";
                        if (customTool.Args is System.Text.Json.JsonElement intentEl &&
                            intentEl.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            intentEl.TryGetProperty("intent", out var intentProp))
                            intentRaw = intentProp.GetString() ?? "";

                        emit(EventTypes.AgentIntent, new { intent = SanitizeIntent(intentRaw) });

                        return Task.FromResult(new PermissionRequestResult
                        {
                            Kind = PermissionRequestResultKind.Approved,
                        });
                    }

                    // report_outcome is a side-effect-free self-assessment call: approve without
                    // governance, emit run.outcome (not tool.call / tool.result), and return.
                    if (string.Equals(toolName, "report_outcome", StringComparison.Ordinal))
                    {
                        bool achieved = false;
                        string reasonRaw = "";
                        if (customTool.Args is System.Text.Json.JsonElement outcomeEl &&
                            outcomeEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (outcomeEl.TryGetProperty("achieved", out var achievedProp) &&
                                (achievedProp.ValueKind == System.Text.Json.JsonValueKind.True ||
                                 achievedProp.ValueKind == System.Text.Json.JsonValueKind.False))
                                achieved = achievedProp.GetBoolean();
                            if (outcomeEl.TryGetProperty("reason", out var reasonProp))
                                reasonRaw = reasonProp.GetString() ?? "";
                        }

                        emit(EventTypes.RunOutcome, new { achieved, reason = SanitizeIntent(reasonRaw) });

                        return Task.FromResult(new PermissionRequestResult
                        {
                            Kind = PermissionRequestResultKind.Approved,
                        });
                    }

                    // Scaffolder API tools: auto-approve without sandbox governance.
                    // The actual HTTP call executes in the function body after approval;
                    // the streaming lifecycle emits tool.result when the function returns.
                    if (ScaffolderApiToolNames.Contains(toolName))
                    {
                        var apiArgs = new Dictionary<string, object>();
                        if (customTool.Args is System.Text.Json.JsonElement apiArgsEl &&
                            apiArgsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var prop in apiArgsEl.EnumerateObject())
                                apiArgs[prop.Name] = prop.Value;
                        }
                        if (realCustomCallId is not null)
                            emitToolCallOnce(customCallId, toolName, apiArgs);
                        return Task.FromResult(new PermissionRequestResult
                        {
                            Kind = PermissionRequestResultKind.Approved,
                        });
                    }

                    // Deserialize the JSON args blob. Stamp tool_name first so it cannot be
                    // overridden by a model-supplied key (Seraph hardening).
                    var args = new Dictionary<string, object>();
                    if (customTool.Args is System.Text.Json.JsonElement argsJson &&
                        argsJson.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in argsJson.EnumerateObject())
                            args[prop.Name] = prop.Value;
                    }
                    args["tool_name"] = toolName;  // overwrite after deserialization

                    // Shell tools need "directory" for SandboxPolicyBackend to validate cwd.
                    if (toolName == "run_command" && !args.ContainsKey("directory"))
                        args["directory"] = workingDirectory;

                    // Emit tool.call only when we hold the real ToolCallId — mirrors the native
                    // path dedup logic. Approved custom tools emit their call via the SDK lifecycle
                    // (ExternalToolRequestedEvent). Denied calls never reach the lifecycle so we
                    // emit the call+error pair below regardless of whether we have a real ID.
                    if (realCustomCallId is not null)
                        emitToolCallOnce(customCallId, toolName, args);

                    var (allowed, reason) = governance.EvaluateToolCall(
                        agentId: $"did:mesh:scaffolder:copilot:{runId}",
                        toolName: toolName,
                        args: args,
                        _logger);

                    if (!allowed)
                    {
                        emitToolCallOnce(customCallId, toolName, args);
                        var denyReason = reason ?? "Operation denied by sandbox policy.";
                        emitToolErrorOnce(customCallId, denyReason);
                        EmitRunDegradedOnce(toolName, denyReason);
                    }

                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = allowed
                            ? PermissionRequestResultKind.Approved
                            : PermissionRequestResultKind.Rejected,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Permission handler exception for custom tool (fail-closed deny) — Tool={ToolName} RunId={RunId}",
                        toolName, runId);
                    emitToolCallOnce(customCallId, toolName, null);
                    var failReason = "Operation denied: internal error evaluating sandbox policy.";
                    emitToolErrorOnce(customCallId, failReason);
                    EmitRunDegradedOnce(toolName, failReason);
                    return Task.FromResult(new PermissionRequestResult
                    {
                        Kind = PermissionRequestResultKind.Rejected,
                    });
                }
            }

            // The real SDK ToolCallId correlates this handler's events with the streaming
            // tool-execution lifecycle (which carries the same id). When it can't be read, we
            // fall back to a synthetic id that is local to this handler — the lifecycle cannot
            // see it. realCallId being null therefore changes which source owns emission below.
            var realCallId = GetToolCallId(request);
            var callId = realCallId ?? Guid.NewGuid().ToString("n");
            try
            {
                var (toolName, args) = MapToToolCall(request);

                // Shell tools need "directory" for SandboxPolicyBackend to validate cwd.
                if (toolName == "run_command" && !args.ContainsKey("directory"))
                    args["directory"] = workingDirectory;

                // Surface the call from this source ONLY when we hold the real ToolCallId, so it
                // dedups against the streaming lifecycle.
                if (realCallId is not null)
                    emitToolCallOnce(callId, toolName, args);

                var (allowed, reason) = governance.EvaluateToolCall(
                    agentId: $"did:mesh:scaffolder:copilot:{runId}",
                    toolName: toolName,
                    args: args,
                    _logger);

                if (!allowed)
                {
                    // A denied call is terminal here and never reaches the lifecycle, so emit a
                    // self-consistent call+error pair, then a run.degraded event so the UI can
                    // show an amber badge regardless of the agent's self-assessment.
                    var denyReason2 = reason ?? "Operation denied by sandbox policy.";
                    emitToolCallOnce(callId, toolName, args);
                    emitToolErrorOnce(callId, denyReason2);
                    EmitRunDegradedOnce(toolName, denyReason2);
                }

                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = allowed
                        ? PermissionRequestResultKind.Approved
                        : PermissionRequestResultKind.Rejected,
                });
            }
            catch (Exception ex)
            {
                // Fail-closed: any failure mapping or evaluating the request denies the tool call.
                _logger.LogError(ex, "Permission handler exception (fail-closed deny) — RunId={RunId}", runId);
                var failReason2 = "Operation denied: internal error evaluating sandbox policy.";
                emitToolCallOnce(callId, request.Kind ?? "unknown", null);
                emitToolErrorOnce(callId, failReason2);
                EmitRunDegradedOnce(request.Kind ?? "unknown", failReason2);
                return Task.FromResult(new PermissionRequestResult
                {
                    Kind = PermissionRequestResultKind.Rejected,
                });
            }
        };
    }

    /// <summary>
    /// Reads the <c>ToolCallId</c> carried by every concrete Copilot SDK
    /// <see cref="PermissionRequest"/> subtype. The base type exposes only
    /// <see cref="PermissionRequest.Kind"/>, so the id is read reflectively.
    /// </summary>
    private static string? GetToolCallId(PermissionRequest request)
        => request.GetType().GetProperty("ToolCallId")?.GetValue(request) as string;

    /// <summary>
    /// Maps a Copilot SDK <see cref="PermissionRequest"/> to an AGT tool-call
    /// representation (tool name + arguments dictionary).
    /// </summary>
    internal static (string toolName, Dictionary<string, object> args) MapToToolCall(
        PermissionRequest request)
    {
        return request switch
        {
            PermissionRequestRead read => MapReadRequest(read),
            PermissionRequestWrite write => ("write_file", new Dictionary<string, object>
            {
                ["path"] = write.FileName ?? "",
            }),
            PermissionRequestShell shell => ("run_command", new Dictionary<string, object>
            {
                ["command"] = shell.FullCommandText ?? "",
            }),
            PermissionRequestMcp mcp => ("mcp", new Dictionary<string, object>
            {
                ["tool"] = mcp.ToolName ?? "",
            }),
            _ => (request.Kind ?? "unknown", new Dictionary<string, object>()),
        };
    }

    /// <summary>
    /// Disambiguates a read permission request into either "read_file" or "list_directory".
    /// Heuristic: trailing directory separator OR <see cref="Directory.Exists"/> → list_directory.
    /// </summary>
    internal static (string toolName, Dictionary<string, object> args) MapReadRequest(
        PermissionRequestRead request)
    {
        var path = request.Path ?? "";
        var args = new Dictionary<string, object> { ["path"] = path };

        if (path.Length > 0 &&
            (path[^1] == Path.DirectorySeparatorChar ||
             path[^1] == Path.AltDirectorySeparatorChar ||
             Directory.Exists(path)))
        {
            return ("list_directory", args);
        }

        return ("read_file", args);
    }

    /// <summary>
    /// Wraps an <see cref="AIFunction"/> and injects
    /// <see cref="CopilotTool.OverridesBuiltInToolKey"/> into <see cref="AITool.AdditionalProperties"/>
    /// so the Copilot SDK accepts tools whose names match a native built-in.
    /// </summary>
    private sealed class CopilotOverrideAIFunction(AIFunction inner) : AIFunction
    {
        private const string OverridesBuiltInToolKey = "overridesBuiltInTool";

        private readonly IReadOnlyDictionary<string, object?> _additionalProperties =
            new Dictionary<string, object?>(inner.AdditionalProperties)
            {
                [OverridesBuiltInToolKey] = true,
            };

        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _additionalProperties;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            inner.InvokeAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Builds the tool list for <see cref="SessionConfig.Tools"/>:
    /// <c>report_intent</c> and <c>report_outcome</c> (wrapped as native overrides so the SDK accepts them),
    /// plus Scaffolder API tools when <paramref name="projectId"/> and <paramref name="agentName"/> are set.
    /// </summary>
    internal static IList<AIFunction> BuildSessionConfigTools(
        SandboxToolContext context,
        string? projectId = null,
        string? agentName = null,
        string? apiBaseUrl = null,
        string? apiKey = null)
    {
        var all = SandboxToolRegistry.Build(context);
        var intentFn = all.First(f => string.Equals(f.Name, "report_intent", StringComparison.Ordinal));
        var outcomeFn = all.First(f => string.Equals(f.Name, "report_outcome", StringComparison.Ordinal));

        var tools = new List<AIFunction>
        {
            new CopilotOverrideAIFunction(intentFn),
            new CopilotOverrideAIFunction(outcomeFn),
        };

        if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(agentName))
        {
            var effectiveBaseUrl = apiBaseUrl ?? "http://localhost:5000";
            tools.AddRange(ScaffolderApiTools.Build(projectId, agentName, effectiveBaseUrl, apiKey));
        }

        return tools;
    }

    /// <summary>
    /// Strips userinfo credentials from a URL and caps its length at 200 characters.
    /// </summary>
    internal static string SanitizeUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return rawUrl.Length > 200 ? rawUrl[..200] + "…" : rawUrl;

        var builder = new UriBuilder(uri) { UserName = "", Password = "" };
        var sanitized = builder.Uri.ToString();
        return sanitized.Length > 200 ? sanitized[..200] + "…" : sanitized;
    }

    /// <summary>
    /// Sanitizes an intent string received from the model before surfacing it in the
    /// run stream. Keeps only printable characters plus horizontal tab and newline;
    /// normalizes all line endings to LF; caps at 2000 characters.
    /// </summary>
    internal static string SanitizeIntent(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.Length > 2000) raw = raw[..2000];
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
                 .Replace("\r", "\n", StringComparison.Ordinal);
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == '\t' || c == '\n') { sb.Append(c); continue; }
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the full assistant message text from the SDK <see cref="AssistantMessageEvent"/>
    /// carried as the <see cref="AIContent.RawRepresentation"/> of a chunk.
    /// </summary>
    private static string? ExtractFinalMessageContent(AgentResponseUpdate chunk)
    {
        if (chunk.Contents is null) return null;

        foreach (var content in chunk.Contents)
        {
            if (content.RawRepresentation is AssistantMessageEvent message)
                return message.Data?.Content;
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _governance?.Dispose();
        if (!string.IsNullOrEmpty(_runId))
        {
            _approvalStore.Clear(_runId);
            _toolApprovalGate.Clear(_runId);
        }
        // Dispose (not delete) the inner agent so the SDK persists session events for history replay.
        if (_inner is IAsyncDisposable disposableAgent)
            await disposableAgent.DisposeAsync().ConfigureAwait(false);
        if (_client is IAsyncDisposable disposableClient)
            await disposableClient.DisposeAsync().ConfigureAwait(false);
    }
}
