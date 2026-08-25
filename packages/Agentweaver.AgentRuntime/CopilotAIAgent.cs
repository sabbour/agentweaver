using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.SandboxFs;

namespace Agentweaver.AgentRuntime;

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
    private static readonly ActivitySource ActivitySource = new("Agentweaver");
    private static readonly Meter Meter = new("Agentweaver", "1.0.0");
    private static readonly Counter<long> TokenUsage =
        Meter.CreateCounter<long>("agentweaver.token.usage", "nano_aiu", "AI credit usage by agent and model");

    /// <summary>
    /// Agentweaver API/MCP-equivalent tool names that are auto-approved without sandbox governance.
    /// The HTTP call executes in the function body after approval and still authenticates against
    /// the loopback API using the configured Agentweaver API key.
    /// </summary>
    private static readonly ISet<string> AgentweaverApiToolNames = AgentweaverApiTools.ToolNames;

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
    private readonly IQuestionGate? _questionGate;
    private readonly IRunOptionsStore? _runOptions;
    private readonly IEnumerable<IAgentRuntimeToolProvider> _toolProviders;

    // Names of tools built by an IAgentRuntimeToolProvider and wrapped in
    // InstrumentedCustomAIFunction (populated fresh on every RebuildInnerAgent call). The
    // permission handler consults this to avoid emitting a second, orphaned tool.call for these
    // tools — the wrapper already records tool.call/tool.result/tool.error around the real
    // invocation, with its own correlated callId and execute_tool span (see #850 follow-up).
    private readonly HashSet<string> _instrumentedToolNames = new(StringComparer.Ordinal);
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
    protected string? _apiCapabilityToken;
    protected string? _userId;

    /// <summary>The run-event channel writer for the current run (null when no stream attached).</summary>
    public ChannelWriter<RunEvent>? StreamWriter { get; private set; }

    /// <summary>
    /// Attaches (or, with <see langword="null"/>, detaches) the run-event side-channel writer for
    /// the current turn <b>without</b> re-running the expensive <see cref="SetupAsync"/>.
    ///
    /// <para>
    /// Used by the pod-side A2A bridge (spec-018 P1.5, <c>A2ATurnBridgeAgent</c>): the bridge
    /// installs a per-turn channel before calling <see cref="RunTurnAsync"/> so every emitted
    /// <see cref="RunEvent"/> is forwarded back to the worker as an A2A <c>DataPart</c>, then
    /// clears it at end of turn. One run per pod and turns are serialized, so swapping the writer
    /// on this singleton is race-free in the pod host.
    /// </para>
    /// </summary>
    public void SetTurnStreamWriter(ChannelWriter<RunEvent>? streamWriter) => StreamWriter = streamWriter;

    // --- Runtime objects created during SetupAsync — kept alive for serialize/deserialize ---
    private CopilotClient? _client;
    private AIAgent? _inner; // the GitHubCopilotAgent
    private SandboxGovernance? _governance;
    private SandboxToolContext? _toolContext;
    private ISandboxExecutor? _activeExecutor;
    private SandboxPolicy? _sandboxPolicy;
    private IReadOnlyList<string> _registeredToolNames = [];
    // Whether list_decisions/get_memory/list_inbox/submit_decision are registered for this
    // session (see BuildSessionConfigTools) — gates whether the prompt tells the agent about
    // them (#268: prompt/tool mismatch caused hallucinated tool calls).
    private bool _includeTeamCoordinationPrompt;
    private GitHubTokenScope? _tokenScope;
    private SessionConfig? _sessionConfig;
    private ShellExecutionTracker? _shellExecutionTracker;
    // Whether this run uses the controlled Build/Test shell surface (purpose == AssemblyBuildTest).
    // Captured in SetupAsync so the inner agent can be rebuilt per turn (ApplyPerTurnContext).
    private bool _controlledBuildTestShell;
    // The CancellationToken SetupAsync was invoked with — reused when the inner agent is rebuilt
    // per turn so the permission handler binds the identical run-scoped token (no behavior change).
    private CancellationToken _setupCt;

    // --- Per-run run-event emission state (reset in SetupAsync) ---
    private StringBuilder _sb = new();
    private int _seq;
    private long _turnInputTokens;
    private long _turnOutputTokens;
    private long _turnNanoAiu;
    private string? _turnModelId;
    private long? _turnTimeToFirstTokenMs;
    private readonly object _emitLock = new();
    private int _deltaCount;
    private HashSet<string> _streamedMessageIds = new(StringComparer.Ordinal);
    private bool _anyDeltaEmittedForNullId;
    private ConcurrentDictionary<string, byte> _emittedCalls = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, byte> _emittedTerminals = new(StringComparer.Ordinal);
    private HashSet<string> _suppressedCallIds = new(StringComparer.Ordinal);

    // Active OpenTelemetry child spans for in-flight tool executions, keyed by tool call id.
    // A "execute_tool" span is opened when the SDK reports ToolExecutionStart and closed on the
    // matching ToolExecutionComplete, giving each tool call a proper child span under the agent
    // turn span (gen_ai.* semantic conventions) so the transaction trace tree can render it.
    private readonly ConcurrentDictionary<string, Activity> _activeToolSpans = new(StringComparer.Ordinal);

    // The current turn's span, captured explicitly so tool spans (and the usage-event model
    // tag) can be parented/targeted to it deterministically regardless of what Activity.Current
    // happens to be at the moment a tool call starts. Overlapping tool executions keep their own
    // span open (from ToolExecutionStart to the matching ToolExecutionComplete) via
    // _activeToolSpans above; while one is open it *is* Activity.Current, so relying on ambient
    // parenting would nest a second, concurrently-started tool span under the first instead of
    // under the turn. Marked volatile because tool-execution callbacks can arrive on SDK
    // callback threads distinct from the thread running RunStreamingAsync.
    private volatile Activity? _turnActivity;

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
    private int _shellTimeoutFailureEmitted;
    private int _nativeShellDenyAttempts;
    private long _shellExecutionGeneration;
    private volatile bool _denyNativeShellLifecycleToolCalls;

    /// <summary>
    /// Inactivity watchdog window for a streaming turn. If the Copilot SDK yields no chunk within
    /// this span and no shell is active, the turn is aborted (retryable) instead of hanging forever
    /// and stranding the run in <c>in_progress</c>. Active shells use their separate hard deadline
    /// and heartbeat policy. Default 15 min; override with the
    /// <c>AGENTWEAVER_AGENT_TURN_IDLE_TIMEOUT_SECONDS</c> environment variable (0 disables it).
    /// Settable for tests.
    /// </summary>
    internal TimeSpan StreamIdleTimeout { get; set; } = ResolveStreamIdleTimeoutDefault();

    /// <summary>
    /// Authoritative default inactivity window inside the AgentHost pod. Worker-side transport
    /// deadlines must remain strictly longer because they cannot observe active-shell liveness.
    /// </summary>
    internal static readonly TimeSpan DefaultStreamIdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hard wall-clock limit for an active shell. While a shell is active this replaces the normal
    /// stream-idle window. Override with
    /// <c>AGENTWEAVER_SHELL_EXECUTION_HARD_TIMEOUT_SECONDS</c> (0 disables the hard deadline).
    /// </summary>
    internal TimeSpan ShellExecutionHardTimeout { get; set; } = ResolveTimeoutDefault(
        "AGENTWEAVER_SHELL_EXECUTION_HARD_TIMEOUT_SECONDS",
        TimeSpan.FromMinutes(30));

    /// <summary>
    /// Total wall-clock bound for one model turn, independent of stream activity. Override with
    /// <c>AGENTWEAVER_AGENT_TURN_TOTAL_TIMEOUT_SECONDS</c> (0 disables it).
    /// </summary>
    internal TimeSpan TotalTurnTimeout { get; set; } = ResolveTimeoutDefault(
        "AGENTWEAVER_AGENT_TURN_TOTAL_TIMEOUT_SECONDS",
        TimeSpan.FromMinutes(60));

    /// <summary>Cadence for active-shell progress events. Settable for focused tests.</summary>
    internal TimeSpan ShellHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>Test seam; production defaults to force-stopping the Copilot CLI process tree.</summary>
    internal Func<Task>? ShellTimeoutTerminator { get; set; }
    internal ShellExecutionTracker? ShellExecutionTrackerForTesting
    {
        get => _shellExecutionTracker;
        set => _shellExecutionTracker = value;
    }

    /// <summary>
    /// Cadence for the <see cref="EventTypes.ToolApprovalPending"/> heartbeat emitted while the
    /// permission handler is blocked on a tool-approval gate. Must stay well under the parent
    /// coordinator's <c>Coordinator:SubtaskStallTimeoutMinutes</c> (default 5 min) so each wait
    /// window is punctuated by an event that keeps the outbound stream flowing and resets the
    /// stall timer (issue #212).
    /// </summary>
    internal static readonly TimeSpan ApprovalHeartbeatInterval = TimeSpan.FromSeconds(20);

    private static TimeSpan ResolveStreamIdleTimeoutDefault()
    {
        return ResolveTimeoutDefault(
            "AGENTWEAVER_AGENT_TURN_IDLE_TIMEOUT_SECONDS",
            DefaultStreamIdleTimeout);
    }

    private static TimeSpan ResolveTimeoutDefault(string variableName, TimeSpan fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (int.TryParse(raw, out var seconds))
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        return fallback;
    }

    public CopilotAIAgent(
        GitHubCopilotClientFactory factory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IQuestionGate? questionGate = null,
        IRunOptionsStore? runOptions = null,
        IEnumerable<IAgentRuntimeToolProvider>? toolProviders = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sandboxPolicyStore = sandboxPolicyStore ?? throw new ArgumentNullException(nameof(sandboxPolicyStore));
        _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
        _toolApprovalGate = toolApprovalGate ?? throw new ArgumentNullException(nameof(toolApprovalGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _questionGate = questionGate;
        _runOptions = runOptions;
        _toolProviders = toolProviders ?? [];
    }

    /// <summary>
    /// Provisions the governance kernel, Copilot client, sandbox tool context, and inner
    /// <c>GitHubCopilotAgent</c> for a single run. Must be called before
    /// <see cref="CreateSessionCoreAsync"/> or <see cref="ExecuteStreamingLoopAsync"/>.
    /// </summary>
    Task Workflow.IWorkflowTurnAgent.SetupAsync(
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
        string? userId) =>
        SetupAsync(
            workingDirectory,
            repositoryPath,
            runId,
            modelId,
            systemPromptContext,
            streamWriter,
            projectId,
            agentName,
            apiBaseUrl,
            apiKey,
            ct,
            userId,
            AgentHostPurpose.Default);

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
        string? userId = null,
        AgentHostPurpose purpose = AgentHostPurpose.Default,
        string? apiCapabilityToken = null)
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
        _apiCapabilityToken = apiCapabilityToken;
        _userId = string.IsNullOrWhiteSpace(userId) ? null : userId;
        _setupCt = ct;

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
        _turnInputTokens = 0;
        _turnOutputTokens = 0;
        _turnNanoAiu = 0;
        _turnModelId = null;
        _turnTimeToFirstTokenMs = null;
        _shellTimeoutFailureEmitted = 0;
        _nativeShellDenyAttempts = 0;
        _denyNativeShellLifecycleToolCalls = false;

        _logger.LogInformation(
            "SetupAsync entered — workingDirectory={WorkingDirectory}, runId={RunId}, streamIsNull={StreamIsNull}",
            workingDirectory, runId, streamWriter is null);

        // --- Governance kernel (per-run) ---
        var sandboxPolicy = await _sandboxPolicyStore.GetPolicyAsync(repositoryPath, ct).ConfigureAwait(false);
        _sandboxPolicy = sandboxPolicy;
        var executor = sandboxPolicy.Direct
            ? new PassthroughExecutor("direct execution — sandbox disabled via settings.yml", _logger)
            : _executor;
        if (executor is IRunWorkspaceRegistrar workspaceRegistrar)
            workspaceRegistrar.RegisterTrustedWorkspace(workingDirectory);
        _activeExecutor = executor;
        _governance = SandboxGovernance.Create(workingDirectory, runId, executor, sandboxPolicy, _logger);

        var scope = await ResolveTokenScopeAsync(_userId, _projectId, ct).ConfigureAwait(false);
        _tokenScope = scope;
        _client = await _factory.CreateClientAsync(scope, modelId, ct).ConfigureAwait(false);
        try
        {
            await _client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ClassifyProviderFailure(ex, "starting client") is { } providerFailure)
                throw providerFailure;
            throw;
        }

        _logger.LogInformation("Copilot client started");

        var fileTools = new SandboxedFileTools(workingDirectory, sandboxPolicy.MaxOutputBytes);
        var searchTools = new SandboxedSearchTools(workingDirectory, sandboxPolicy.MaxOutputBytes);
        var redactor = SandboxOutputRedactor.Default;
        var agentId = $"did:mesh:agentweaver:copilot:{runId}";

        var controlledBuildTestShell = purpose == AgentHostPurpose.AssemblyBuildTest;
        _controlledBuildTestShell = controlledBuildTestShell;
        var toolOptions = new SandboxToolOptions(
            ShellEnabled: sandboxPolicy.ShellEnabled,
            DefaultTimeoutMs: controlledBuildTestShell
                ? (int)TimeSpan.FromMinutes(10).TotalMilliseconds
                : (int)TimeSpan.FromMinutes(5).TotalMilliseconds)
        {
            AllowedRepositoryRoots = [.. sandboxPolicy.AllowedRepositoryRoots],
            DestructiveCommandPatterns = [.. sandboxPolicy.DestructiveCommandPatterns],
            RequireApprovalForAllShell = sandboxPolicy.RequireApprovalForAllShell,
            NetworkEnabled = sandboxPolicy.NetworkEnabled,
            RejectDestructiveCommands = controlledBuildTestShell,
            RejectBackgroundCommands = controlledBuildTestShell,
            MaximumTimeoutMs = controlledBuildTestShell
                ? (int)TimeSpan.FromMinutes(10).TotalMilliseconds
                : 0,
            // #313: floor Build/Test command timeouts at 10 min so an optimistically short
            // model-supplied timeout_ms (e.g. 3 min) can't kill a legitimate long build under
            // scheduling contention. Only applied in the controlled Build/Test tool context.
            MinimumTimeoutMs = controlledBuildTestShell
                ? (int)TimeSpan.FromMinutes(10).TotalMilliseconds
                : 0,
        };
        _shellExecutionTracker?.Dispose();
        _shellExecutionTracker = new ShellExecutionTracker();
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
            IsCommandDenied: hash => _approvalStore.IsDenied(runId, hash),
            QuestionGate: _questionGate,
            ShellExecutionTracker: _shellExecutionTracker,
            ScratchDirectory: Environment.GetEnvironmentVariable("AGENTWEAVER_SCRATCH")
                ?? Environment.GetEnvironmentVariable("AGENTWEAVER_SCRATCH_DIR"));
        _toolContext = toolContext;

        RebuildInnerAgent();
    }

    /// <summary>
    /// (Re)builds the inner Copilot <c>AIAgent</c> from the current per-run/per-turn context
    /// (<see cref="_systemPromptContext"/>, <see cref="_projectId"/>, <see cref="_agentName"/>,
    /// <see cref="_modelId"/>) and the already-provisioned <see cref="_client"/>,
    /// <see cref="_toolContext"/>, and <see cref="_governance"/>.
    ///
    /// <para>
    /// Called once at the end of <see cref="SetupAsync"/> and again by
    /// <see cref="ApplyPerTurnContext"/> when the pod-side A2A bridge delivers per-turn context
    /// (spec-018 / #336). The expensive Copilot client provisioning + governance setup are NOT
    /// repeated — only the session's tool set and system message are recomputed, because both
    /// depend on the (identity + prompt) context that a warm pod only learns per turn.
    /// </para>
    /// </summary>
    private void RebuildInnerAgent()
    {
        var toolContext = _toolContext
            ?? throw new InvalidOperationException("SetupAsync must run before RebuildInnerAgent.");
        var client = _client
            ?? throw new InvalidOperationException("SetupAsync must run before RebuildInnerAgent.");
        var governance = _governance
            ?? throw new InvalidOperationException("SetupAsync must run before RebuildInnerAgent.");

        _instrumentedToolNames.Clear();
        var sessionTools = BuildSessionConfigTools(
            toolContext,
            _projectId,
            _agentName,
            _apiBaseUrl,
            _apiKey,
            _toolProviders,
            // SECURITY (native shell bypass): shell must ALWAYS be routed through the sandboxed
            // run_command tool (ISandboxExecutor-backed), for every run purpose — not only
            // AssemblyBuildTest. Register it whenever the registry exposes it (real isolation +
            // policy shell enabled); native shell is denied below so this is the only shell path.
            includeControlledRunCommand: true,
            runCapabilityToken: _apiCapabilityToken,
            // #850 follow-up: instrument every IAgentRuntimeToolProvider tool (start_preview and
            // its preview-lifecycle siblings) so their tool.call/tool.result/tool.error RunEvents
            // and execute_tool span are recorded directly around the real invocation — see
            // InstrumentedCustomAIFunction.
            instrumentProviderTool: tool =>
            {
                _instrumentedToolNames.Add(tool.Name);
                return new InstrumentedCustomAIFunction(
                    tool, EmitToolCallOnce, EmitToolResultOnce, EmitToolErrorOnce, StartToolSpan, CompleteToolSpan);
            });
        _registeredToolNames = sessionTools.Select(t => t.Name).ToList();
        // list_decisions/get_memory/list_inbox/submit_decision are only registered when
        // Agentweaver API tools were built (projectId + agentName both supplied). Only tell the
        // agent about them in the prompt when they're actually callable, or it hallucinates
        // calls to nonexistent tools (#268).
        _includeTeamCoordinationPrompt = _registeredToolNames.Contains("list_decisions");

        const bool denyNativeShell = true;
        // Keep the SDK lifecycle translator aligned with the permission handler: when native
        // shell is denied for this run, any lifecycle start event for the SDK's built-in shell
        // must be surfaced to the frontend as run_command instead of the raw native tool name.
        // This avoids "bash" winning the first-write dedupe race against the handler's relabeled
        // synthetic tool.call.
        _denyNativeShellLifecycleToolCalls = denyNativeShell;

        var sessionConfig = new SessionConfig
        {
            OnPermissionRequest = BuildPermissionHandler(
                governance,
                _runId,
                _workingDirectory,
                EmitToolCallOnce,
                EmitToolErrorOnce,
                Emit,
                _setupCt,
                // SECURITY (native shell bypass): deny the SDK's native shell for EVERY run, not
                // just AssemblyBuildTest. The native shell executes in-process and bypasses the
                // per-command ISandboxExecutor/bubblewrap filesystem confinement (the permission
                // handler validates only the working directory, never the command text). All shell
                // must instead go through the sandboxed run_command tool registered above.
                denyNativeShell: denyNativeShell),
            WorkingDirectory = _workingDirectory,
            EnableConfigDiscovery = false,
            Streaming = true,
            // Deterministic session ID enables history replay via ResumeSessionAsync.
            // Format: "agentweaver-run-{runId}" — unique per run, stable across restarts.
            SessionId = $"agentweaver-run-{_runId}",
            Tools = sessionTools.Cast<AIFunctionDeclaration>().ToList(),
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = string.IsNullOrEmpty(_systemPromptContext)
                    ? BuildBasePrompt(_includeTeamCoordinationPrompt)
                    : BuildBasePrompt(_includeTeamCoordinationPrompt) + "\n\n" + _systemPromptContext,
            },
            Model = _modelId,
            // Disable persistent session store (copilot-sdk#1814): one-shot runs do not need
            // cross-session retrieval and the shared SQLite store causes "database is locked" under
            // concurrent load with multiple replicas.
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
        };
        _sessionConfig = sessionConfig;

        _inner = client.AsAIAgent(sessionConfig, ownsClient: false, id: null, name: null, description: null);

        _logger.LogInformation("Inner Copilot AIAgent created with sandbox governance — runId={RunId}", _runId);
    }

    /// <summary>
    /// Applies the per-turn agent context delivered by the pod-side A2A bridge
    /// (<c>A2ATurnBridgeAgent</c>, spec-018 / #336) onto an already-provisioned agent, rebuilding
    /// the inner agent's tool set and system message when anything actually changed.
    ///
    /// <para>
    /// Warm-pool pods run <see cref="SetupAsync"/> once at <c>/configure</c> time with only the
    /// static, image-baked pod context (sandbox manifest) and empty identity. The per-run charter,
    /// memory context, assigned skills, real project/agent identity, AND the Agentweaver API
    /// base URL + key the loopback tools call are delivered by the worker on every turn via
    /// <c>AgentSetupParams</c>. Without applying them here, that per-run context — including
    /// assigned skills (#336), the project/agent identity that gates the memory/decision tools
    /// (#335), and the API base URL those tools POST/GET against (#335 P1) — never reaches the
    /// agent in <c>pod-per-run</c> mode.
    /// </para>
    ///
    /// <para>
    /// The AgentHost pod template injects no static <c>AgentHost__ApiBaseUrl</c>/<c>ApiKey</c>
    /// (see <c>k8s/sandbox-template-agenthost.yaml</c>), so a warm pod's startup
    /// <see cref="SetupAsync"/> leaves <see cref="_apiBaseUrl"/> null. The Agentweaver API tools
    /// then default their <c>HttpClient</c> base address to <c>http://localhost:5000</c>, which is
    /// unreachable from inside the pod — every <c>record_memory</c>/<c>get_memory</c> call throws a
    /// connection-refused transport exception that the SDK reports as an opaque
    /// "Tool execution failed". Threading the per-turn base URL/key here points the tools at the
    /// real worker-tier API.
    /// </para>
    ///
    /// <para>
    /// Returns <see langword="true"/> when the inner agent was rebuilt. Identity/prompt/endpoint
    /// values are only overridden when the incoming turn supplies non-empty values, and the rebuild
    /// is skipped when nothing changed so a resumed revision session is left untouched.
    /// </para>
    /// </summary>
    public bool ApplyPerTurnContext(
        string? systemPromptContext,
        string? projectId,
        string? agentName,
        string? apiBaseUrl = null,
        string? apiKey = null)
    {
        // Not provisioned yet (no SetupAsync). Nothing to re-apply onto — the startup path will
        // build the inner agent from these same fields.
        if (_client is null || _toolContext is null || _governance is null)
            return false;

        var newProjectId = string.IsNullOrWhiteSpace(projectId) ? _projectId : projectId;
        var newAgentName = string.IsNullOrWhiteSpace(agentName) ? _agentName : agentName;
        // The API base URL/key are per-run values the worker packs into every turn. Warm pods have
        // no static value, so keep any existing value only when the turn omits one (#335 P1).
        var newApiBaseUrl = string.IsNullOrWhiteSpace(apiBaseUrl) ? _apiBaseUrl : apiBaseUrl;
        var newApiKey = string.IsNullOrWhiteSpace(apiKey) ? _apiKey : apiKey;
        var newContext = systemPromptContext;

        var changed =
            !string.Equals(_systemPromptContext, newContext, StringComparison.Ordinal) ||
            !string.Equals(_projectId, newProjectId, StringComparison.Ordinal) ||
            !string.Equals(_agentName, newAgentName, StringComparison.Ordinal) ||
            !string.Equals(_apiBaseUrl, newApiBaseUrl, StringComparison.Ordinal) ||
            !string.Equals(_apiKey, newApiKey, StringComparison.Ordinal);

        if (!changed)
            return false;

        _systemPromptContext = newContext;
        _projectId = newProjectId;
        _agentName = newAgentName;
        _apiBaseUrl = newApiBaseUrl;
        _apiKey = newApiKey;
        RebuildInnerAgent();

        _logger.LogInformation(
            "Applied per-turn agent context — runId={RunId}, projectId={ProjectId}, agentName={AgentName}, apiBaseUrlSet={ApiBaseUrlSet}, systemPromptChars={Chars}",
            _runId, _projectId, _agentName, !string.IsNullOrWhiteSpace(_apiBaseUrl), _systemPromptContext?.Length ?? 0);
        return true;
    }

    // ----- AIAgent abstract overrides: delegate to the inner GitHubCopilotAgent -----

    /// <summary>MAF entry point to create the initial session. Delegates to the inner agent.</summary>
    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before CreateSessionAsync.");
        try
        {
            return await _inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ClassifyProviderFailure(ex, "creating session") is { } providerFailure)
                throw providerFailure;
            throw;
        }
    }

    /// <summary>
    /// Resumes an existing Copilot SDK session so the agent retains conversation history
    /// across reviewer-requested-changes revision cycles. Uses the deterministic session ID
    /// (<c>agentweaver-run-{runId}</c>) set during <see cref="SetupAsync"/>.
    /// </summary>
    public async ValueTask<AgentSession> ResumeSessionAsync(CancellationToken cancellationToken)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before ResumeSessionAsync.");
        // SessionId is already set in SessionConfig ("agentweaver-run-{runId}") so the SDK
        // resumes the persisted session automatically — no raw overload needed.
        try
        {
            return await _inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ClassifyProviderFailure(ex, "resuming session") is { } providerFailure)
                throw providerFailure;
            throw;
        }
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
            var scope = await ResolveTokenScopeAsync(_userId, _projectId, cancellationToken).ConfigureAwait(false);
            _tokenScope = scope;
            _client ??= await _factory.CreateClientAsync(scope, _modelId, cancellationToken).ConfigureAwait(false);
            try
            {
                await _client.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ClassifyProviderFailure(ex, "starting client for session restore") is { } providerFailure)
                    throw providerFailure;
                throw;
            }
            _inner = _client.AsAIAgent(
                new SessionConfig
                {
                    SessionId = $"agentweaver-run-{_runId}",
                    // Disable persistent session store (copilot-sdk#1814).
                    EnableSessionStore = false,
                    InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                },
                ownsClient: false, id: null, name: null, description: null);
        }
        return await _inner.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubTokenScope> ResolveTokenScopeAsync(
        string? userId,
        string? projectId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                $"Run {_runId} cannot start: no submitting user identity is available. " +
                "Pass the authenticated user's ID to SetupAsync so the correct Copilot-entitled " +
                "token is resolved. Using the installation token is not permitted.",
                isRetryable: false);

        var scope = await _scopeProvider.ResolveAsync(userId, projectId, ct).ConfigureAwait(false);
        if (string.Equals(scope.Key, GitHubTokenScope.Installation.Key, StringComparison.Ordinal))
            throw new AgentProviderException(
                ModelSource.GitHubCopilot,
                AgentProviderFailureKind.Authorization,
                "github_copilot_auth_required",
                $"Run {_runId} cannot start: the token scope provider resolved the installation " +
                "scope for a Copilot model turn. GitHub App installation tokens are not Copilot " +
                "model credentials; configure a user-token scope provider and pass the submitting user.",
                isRetryable: false);

        return scope;
    }

    /// <summary>
    /// True when <paramref name="ex"/> (or any inner exception) is the GitHub Copilot SDK's
    /// "Session was not created with authentication info or custom provider" error — i.e. the
    /// resolved token has no usable Copilot authentication. Used to surface a clear, actionable
    /// failure instead of the opaque SDK message.
    /// </summary>
    internal static bool IsMissingCopilotAuth(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (!string.IsNullOrEmpty(message) &&
                (message.Contains("was not created with authentication info", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("authentication info or custom provider", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private AgentProviderException? ClassifyProviderFailure(Exception ex, string phase)
    {
        var providerFailure = AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, _runId);
        if (providerFailure is null)
            return null;

        _logger.LogWarning(
            ex,
            "GitHub Copilot provider failure while {Phase} — runId={RunId} kind={Kind} code={Code}",
            phase,
            _runId,
            providerFailure.FailureKind,
            providerFailure.ErrorCode);
        EmitProviderFailure(providerFailure);
        return providerFailure;
    }

    private void EmitProviderFailure(AgentProviderException providerFailure)
    {
        Emit(EventTypes.RunFailed, new
        {
            message = providerFailure.UserMessage,
            category = providerFailure.FailureKind.ToString(),
            errorCode = providerFailure.ErrorCode,
            retryable = providerFailure.IsRetryable,
        });
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
            ? BuildBasePrompt(_includeTeamCoordinationPrompt)
            : BuildBasePrompt(_includeTeamCoordinationPrompt) + "\n\n" + _systemPromptContext;
        Emit("agent.system_prompt", new { provider = "copilot", prompt = fullSystemPrompt, memoryContextIncluded = !string.IsNullOrEmpty(_systemPromptContext), skillsContextIncluded = Agentweaver.Domain.Skills.SkillPromptMarkers.ContainsSkillContext(_systemPromptContext) });
        Emit("agent.task", new { task });
        Emit("agent.tools", new { provider = "copilot", tools = _registeredToolNames });
        if (executor.HasNetworkWarning)
        {
            Emit("sandbox.warning", new { category = "network-open", message = executor.NetworkWarningMessage, backend = executor.BackendName });
        }
        if (sandboxPolicy.NetworkEnabled)
        {
            Emit("sandbox.warning", new
            {
                category = "network-open",
                message = "Sandbox is running with outbound network enabled (network_enabled: true in .agentweaver/settings.yml). " +
                          "Network access is intentional but increases the attack surface. " +
                          "Ensure this is required for the agent's task.",
                backend = executor.BackendName
            });
        }

        using var turnActivity = StartModelTurnActivity();
        _turnActivity = turnActivity;
        var turnStarted = Stopwatch.GetTimestamp();
        var turnStartedAt = DateTimeOffset.UtcNow;
        using var totalTurnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (TotalTurnTimeout > TimeSpan.Zero)
            totalTurnCts.CancelAfter(TotalTurnTimeout);
        var turnCt = totalTurnCts.Token;
        var shellExecutionGeneration = _shellExecutionTracker?.BeginObservedTurn() ?? 0;
        _shellExecutionGeneration = shellExecutionGeneration;
        try
        {
            var rateLimitRetryAttempt = 0;
            var unauthorizedRetried = false;
            while (true)
            {
                try
                {
                    session = await EnsureFreshClientForAiCallAsync(session, turnCt).ConfigureAwait(false);
                    await StreamTurnOnceAsync(task, session, turnStarted, turnStartedAt, turnCt).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (GitHubCopilotClientFactory.IsUnauthorized(ex) && !unauthorizedRetried)
                {
                    unauthorizedRetried = true;
                    _logger.LogWarning(ex, "GitHub Copilot streaming call returned 401 for run {RunId}; refreshing token and retrying once", _runId);
                    session = await RecreateInnerAgentSessionAsync(turnCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (GitHubCopilotClientFactory.IsRateLimited(ex)
                                           && GitHubCopilotClientFactory.GetRateLimitRetryDelay(rateLimitRetryAttempt + 1) is { } delay)
                {
                    rateLimitRetryAttempt++;
                    _factory.LogAiRetry(ex, rateLimitRetryAttempt, delay, "HTTP 429/rate limit");
                    await Task.Delay(delay, turnCt).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsMissingCopilotAuth(ex))
                {
                    // Deliverable: replace the opaque SDK "Session was not created with authentication
                    // info or custom provider" with a clear, actionable failure. This happens when the
                    // pod resolved a non-Copilot token (typically the installation fallback because no
                    // submitting user / AgentHost__UserId was available).
                    _logger.LogError(
                        ex,
                        "Run {RunId} could not authenticate to GitHub Copilot: the resolved GitHub token " +
                        "is not Copilot-entitled (likely the installation fallback). Ensure the submitting " +
                        "user is signed in and AgentHost__UserId is injected into the pod.",
                        _runId);
                    var failure = new AgentProviderException(
                        ModelSource.GitHubCopilot,
                        AgentProviderFailureKind.Authorization,
                        "github_copilot_auth_required",
                        $"Run {_runId} has no Copilot-entitled credentials: the resolved GitHub token is not " +
                        "authorized for GitHub Copilot. Ensure the submitting user is signed in and " +
                        "AgentHost__UserId is injected into the pod.",
                        isRetryable: false,
                        ex);
                    EmitProviderFailure(failure);
                    throw failure;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                    when (totalTurnCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    var failure = new AgentProviderException(
                        ModelSource.GitHubCopilot,
                        AgentProviderFailureKind.ProviderUnavailable,
                        "github_copilot_turn_timeout",
                        $"The GitHub Copilot turn exceeded its total deadline of {TotalTurnTimeout.TotalMinutes:n0} minutes and was aborted.",
                        isRetryable: true,
                        ex);
                    EmitProviderFailure(failure);
                    throw failure;
                }
                catch (Exception ex)
                {
                    var providerFailure = AgentProviderException.Classify(ModelSource.GitHubCopilot, ex, _runId);
                    if (providerFailure is not null)
                    {
                        _logger.LogWarning(
                            ex,
                            "GitHub Copilot provider failure during RunStreamingAsync — runId={RunId} kind={Kind} code={Code}",
                            _runId,
                            providerFailure.FailureKind,
                            providerFailure.ErrorCode);
                        if (!string.Equals(providerFailure.ErrorCode, "shell_execution_timeout", StringComparison.Ordinal) ||
                            Volatile.Read(ref _shellTimeoutFailureEmitted) == 0)
                        {
                            EmitProviderFailure(providerFailure);
                        }
                        throw providerFailure;
                    }

                    _logger.LogError(ex, "RunStreamingAsync threw for workingDirectory={WorkingDirectory}", _workingDirectory);
                    Emit("run.failed", new { message = "The agent encountered an internal error." });
                    throw;
                }
            }
        }
        finally
        {
            _shellExecutionTracker?.ClearObservedExecution(shellExecutionGeneration);
            // Close any tool spans still open (e.g. a tool whose completion event never arrived
            // because the turn faulted) so no span is leaked as perpetually in-flight.
            foreach (var callId in _activeToolSpans.Keys.ToArray())
                CompleteToolSpan(callId, success: false, error: "Tool execution did not report completion.");
            CompleteModelTurnTelemetry(turnActivity);
            _turnActivity = null;
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

        Emit(EventTypes.AgentTurnUsage, new
        {
            inputTokens = _turnInputTokens,
            outputTokens = _turnOutputTokens,
            totalTokens = _turnInputTokens + _turnOutputTokens,
            totalNanoAiu = _turnNanoAiu,
            modelId = _turnModelId ?? _modelId,
            durationMs = Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds,
            timeToFirstTokenMs = _turnTimeToFirstTokenMs
        });

        Emit(EventTypes.AgentTurnEnd, new { turnId = "0" });

        if (_suppressedCallIds.Count > 0)
            _logger.LogInformation("Suppressed {Count} SDK-internal tool events", _suppressedCallIds.Count);

        var result = _sb.ToString();
        _logger.LogInformation(
            "Run complete — deltaCount={DeltaCount}, resultLength={ResultLength}",
            _deltaCount, result.Length);

        return result;
    }

    private async Task StreamTurnOnceAsync(
        string task,
        AgentSession session,
        long turnStarted,
        DateTimeOffset turnStartedAt,
        CancellationToken ct)
    {
        if (_inner is null)
            throw new InvalidOperationException("SetupAsync must be called before ExecuteStreamingLoopAsync.");

        await foreach (var chunk in _inner.RunStreamingAsync(task, session, options: null, ct)
                   .WithToolAwareWatchdog(
                       new StreamWatchdogOptions(
                           StreamIdleTimeout,
                           TotalTurnTimeout,
                           ShellHeartbeatInterval),
                       _shellExecutionTracker,
                       _runId ?? "unknown",
                       _logger,
                       EmitShellExecutionPending,
                       HandleShellExecutionTimeoutAsync,
                       turnStartedAt,
                       ct))
        {
            if (chunk is null) continue;

            var messageId = chunk.MessageId;

            // Incremental token text surfaces as TextContent (AssistantMessageDeltaEvent).
            var deltaText = chunk.Text;
            if (!string.IsNullOrEmpty(deltaText))
            {
                _turnTimeToFirstTokenMs ??= (long)Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds;
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
                {
                    TranslateToolLifecycle(c.RawRepresentation);
                    if (c.RawRepresentation is AssistantUsageEvent usageEvent && usageEvent.Data is not null)
                    {
                        _turnInputTokens += usageEvent.Data.InputTokens ?? 0;
                        _turnOutputTokens += usageEvent.Data.OutputTokens ?? 0;
                        _turnNanoAiu += (long)(usageEvent.Data.CopilotUsage?.TotalNanoAiu ?? 0.0);
                        _turnModelId ??= usageEvent.Data.Model;
                        // Target the turn span explicitly (not Activity.Current) — if a tool
                        // span is open concurrently, Activity.Current would be the tool span,
                        // misplacing this model tag onto it instead of the turn.
                        _turnActivity?.SetTag("gen_ai.response.model", usageEvent.Data.Model);
                        _turnActivity?.SetTag("model", usageEvent.Data.Model);
                    }
                }
            }
        }
    }

    private void EmitShellExecutionPending(ShellExecutionSnapshot snapshot)
    {
        Emit(EventTypes.ToolExecutionPending, new
        {
            toolCallId = snapshot.ToolCallId,
            commandHash = snapshot.CommandHash,
            startedAtUtc = snapshot.StartedAt,
            deadlineUtc = snapshot.Deadline,
            elapsedSeconds = (DateTimeOffset.UtcNow - snapshot.StartedAt).TotalSeconds,
        });
    }

    internal async Task HandleShellExecutionTimeoutAsync(ShellExecutionSnapshot snapshot)
    {
        var tracker = _shellExecutionTracker;
        if (tracker is not null && !tracker.TryBeginObservedTermination(snapshot))
        {
            _logger.LogWarning(
                "shell_lifecycle_stale_generation — timeout ignored because the shell slot no longer matches; runId={RunId}, toolCallId={ToolCallId}, generation={Generation}",
                _runId,
                snapshot.ToolCallId,
                snapshot.Generation);
            return;
        }

        var terminate = ShellTimeoutTerminator ?? ForceStopCopilotProcessTreeAsync;
        try
        {
            await terminate().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Force-stopping the Copilot CLI process tree failed after shell deadline — runId={RunId}, toolCallId={ToolCallId}",
                _runId,
                snapshot.ToolCallId);
        }
        finally
        {
            if (tracker is not null && !tracker.FenceObservedExecution(snapshot))
            {
                _logger.LogWarning(
                    "shell_lifecycle_stale_generation — timeout cleanup did not match an active shell; runId={RunId}, toolCallId={ToolCallId}, generation={Generation}",
                    _runId,
                    snapshot.ToolCallId,
                    snapshot.Generation);
            }
        }

        var failure = new AgentProviderException(
            ModelSource.GitHubCopilot,
            AgentProviderFailureKind.ProviderUnavailable,
            "shell_execution_timeout",
            $"Shell execution exceeded its hard deadline of {(snapshot.Deadline - snapshot.StartedAt).TotalMinutes:n0} minutes and was terminated.",
            isRetryable: true);
        Interlocked.Exchange(ref _shellTimeoutFailureEmitted, 1);
        EmitProviderFailure(failure);
    }

    public async Task ForceStopCopilotProcessTreeAsync()
    {
        var client = _client;
        if (client is null)
            return;

        try
        {
            await client.ForceStopAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _inner = null;
            }
        }
    }

    private Activity? StartModelTurnActivity()
    {
        var activity = ActivitySource.StartActivity("Agentweaver model turn", ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag("agentweaver.span.kind", "agent_turn");
        activity.SetTag("run_id", _runId);
        activity.SetTag("run.id", _runId);
        activity.SetTag("agent_name", string.IsNullOrWhiteSpace(_agentName) ? "unknown" : _agentName);
        activity.SetTag("gen_ai.agent.name", string.IsNullOrWhiteSpace(_agentName) ? "unknown" : _agentName);
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("model", _modelId);
        activity.SetTag("model_id", _modelId);
        activity.SetTag("gen_ai.request.model", _modelId);
        if (!string.IsNullOrWhiteSpace(_projectId))
            activity.SetTag("project.id", _projectId);
        return activity;
    }

    private void CompleteModelTurnTelemetry(Activity? activity)
    {
        var model = _turnModelId ?? _modelId ?? "unknown";
        var agent = string.IsNullOrWhiteSpace(_agentName) ? "unknown" : _agentName!;
        activity?.SetTag("agentweaver.span.kind", "agent_turn");
        activity?.SetTag("agent_name", agent);
        activity?.SetTag("gen_ai.agent.name", agent);
        activity?.SetTag("model", model);
        activity?.SetTag("model_id", model);
        activity?.SetTag("gen_ai.request.model", model);
        activity?.SetTag("gen_ai.response.model", model);
        activity?.SetTag("gen_ai.usage.input_tokens", _turnInputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", _turnOutputTokens);
        activity?.SetTag("gen_ai.usage.total_tokens", _turnInputTokens + _turnOutputTokens);
        activity?.SetTag("agentweaver.aiu.nano", _turnNanoAiu);
        if (_turnTimeToFirstTokenMs is { } ttft)
        {
            activity?.SetTag("time_to_first_token_ms", ttft);
            activity?.SetTag("ttft_ms", ttft);
            activity?.SetTag("gen_ai.response.ttft_ms", ttft);
        }

        if (_turnNanoAiu > 0)
        {
            var tags = new List<KeyValuePair<string, object?>>
            {
                new("agent_name", agent),
                new("gen_ai.agent.name", agent),
                new("model", model),
                new("model_id", model),
                new("gen_ai.request.model", model),
                new("gen_ai.response.model", model),
                new("run_id", _runId),
                new("run.id", _runId),
                new("gen_ai.usage.input_tokens", _turnInputTokens),
                new("gen_ai.usage.output_tokens", _turnOutputTokens),
            };
            if (!string.IsNullOrWhiteSpace(_projectId))
                tags.Add(new("project.id", _projectId));

            TokenUsage.Add(_turnNanoAiu, tags.ToArray());
        }
    }

    private async Task<AgentSession> EnsureFreshClientForAiCallAsync(AgentSession session, CancellationToken ct)
    {
        if (_tokenScope is null)
            return session;

        if (!await _factory.ShouldRefreshBeforeAiCallAsync(_tokenScope, ct).ConfigureAwait(false))
            return session;

        _logger.LogInformation("GitHub Copilot token is expired or near expiry for run {RunId}; refreshing before streaming call", _runId);
        return await RecreateInnerAgentSessionAsync(ct).ConfigureAwait(false);
    }

    private async Task<AgentSession> RecreateInnerAgentSessionAsync(CancellationToken ct)
    {
        if (_tokenScope is null)
            throw new InvalidOperationException("GitHub token scope is unavailable; SetupAsync must be called first.");
        if (_sessionConfig is null)
            throw new InvalidOperationException("SessionConfig is unavailable; SetupAsync must be called first.");

        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);

        _client = await _factory.CreateClientAsync(_tokenScope, _modelId, ct).ConfigureAwait(false);
        try
        {
            await _client.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ClassifyProviderFailure(ex, "restarting client") is { } providerFailure)
                throw providerFailure;
            throw;
        }
        _inner = _client.AsAIAgent(_sessionConfig, ownsClient: false, id: null, name: null, description: null);
        try
        {
            return await _inner.CreateSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (ClassifyProviderFailure(ex, "recreating session") is { } providerFailure)
                throw providerFailure;
            throw;
        }
    }

    // ----- Thread-safe run-event emission -----
    // The permission handler fires on SDK callback threads concurrently with the MAF
    // streaming loop, so the sequence increment and the channel write are taken under one
    // lock. This keeps event sequence numbers monotonic AND in arrival order.

    internal void Emit(string type, object payload)
    {
        var stream = StreamWriter;
        if (stream is null) return;
        lock (_emitLock)
        {
            if (!stream.TryWrite(new RunEvent(++_seq, type, payload)))
                _logger.LogWarning("TryWrite false for {EventType}", type);
        }
    }

    internal void EmitToolCallOnce(string callId, string toolName, object? arguments)
    {
        if (_emittedCalls.TryAdd(callId, 0))
            Emit("tool.call", new { callId, toolName, arguments = SensitiveDataRedactor.RedactObject(arguments) });
    }

    internal void EmitToolResultOnce(string callId, string content)
    {
        EmitToolCallOnce(callId, "unknown", null); // defensive call-before-result
        if (_emittedTerminals.TryAdd(callId, 0))
            Emit("tool.result", new { callId, content = SensitiveDataRedactor.RedactJsonStringIfApplicable(content) });
    }

    internal void EmitToolErrorOnce(string callId, string errorMessage)
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
    /// fabricated. The one exception is the SDK's native shell start event: when this run
    /// hard-denies native shell, the lifecycle still surfaces a start callback using the raw
    /// built-in tool name (for example <c>bash</c>). That callback is normalized to
    /// <c>run_command</c> here so the append-only event stream stays consistent with the
    /// permission handler's denial path regardless of which callback arrives first.
    /// </summary>
    private void TranslateToolLifecycle(object? raw)
    {
        switch (raw)
        {
            case ToolExecutionStartEvent start when start.Data is not null:
                ObserveToolExecutionStarted(
                    start.Data.ToolCallId,
                    start.Data.ToolName,
                    start.Data.Arguments,
                    start.Timestamp);
                break;
            case ToolExecutionCompleteEvent complete when complete.Data is not null:
            {
                var callId = complete.Data.ToolCallId ?? Guid.NewGuid().ToString("n");
                if (_shellExecutionTracker is { } tracker &&
                    !tracker.CompleteObservedExecution(callId, _shellExecutionGeneration) &&
                    tracker.IsObservedGenerationFenced(_shellExecutionGeneration))
                {
                    _logger.LogWarning(
                        "shell_lifecycle_stale_generation — completion ignored for a fenced shell generation; runId={RunId}, toolCallId={ToolCallId}, generation={Generation}",
                        _runId,
                        callId,
                        _shellExecutionGeneration);
                }
                if (_suppressedCallIds.Contains(callId))
                    break;
                if (complete.Data.Success)
                {
                    CompleteToolSpan(callId, success: true, error: null, endTime: complete.Timestamp);
                    EmitToolResultOnce(callId, complete.Data.Result?.Content ?? string.Empty);
                }
                else
                {
                    var error = complete.Data.Error?.Message ?? "Tool execution failed.";
                    CompleteToolSpan(callId, success: false, error: error, endTime: complete.Timestamp);
                    EmitToolErrorOnce(callId, error);
                }
                break;
            }
        }
    }

    internal void ObserveToolExecutionStarted(
        string? toolCallId,
        string? toolName,
        object? arguments,
        DateTimeOffset? startTime)
    {
        var callId = toolCallId ?? Guid.NewGuid().ToString("n");
        var rawToolName = toolName ?? "";

        // Translate report_intent into an agent.intent event BEFORE general suppression.
        if (string.Equals(rawToolName, "report_intent", StringComparison.OrdinalIgnoreCase))
        {
            _suppressedCallIds.Add(callId);
            try
            {
                if (arguments is JsonElement argsEl &&
                    argsEl.TryGetProperty("intent", out var intentEl))
                {
                    var intentText = intentEl.GetString();
                    if (!string.IsNullOrWhiteSpace(intentText))
                        Emit("agent.intent", new { intent = intentText });
                }
            }
            catch { /* non-fatal: suppress raw event even if parsing fails */ }
            return;
        }

        if (SuppressedInternalTools.Contains(rawToolName))
        {
            _suppressedCallIds.Add(callId);
            return;
        }

        var resolvedToolName = rawToolName.Length > 0 ? rawToolName : "unknown";
        if (_denyNativeShellLifecycleToolCalls && IsNativeShellLifecycleToolName(resolvedToolName))
        {
            // The SDK can surface a native-shell start callback even though the permission handler
            // rejects that exact ToolCallId immediately afterwards. Emit the normalized
            // run_command label here so whichever source wins the first-write race yields the same
            // frontend-visible tool name, then suppress any later lifecycle terminal for this id.
            _suppressedCallIds.Add(callId);
            EmitToolCallOnce(callId, "run_command", arguments);
            return;
        }

        if (IsShellToolName(resolvedToolName) &&
            _shellExecutionTracker?.ActiveExecution is null)
        {
            TrackApprovedShell(callId, callId);
        }
        StartToolSpan(callId, resolvedToolName, startTime);
        EmitToolCallOnce(callId, resolvedToolName, arguments);
    }

    /// <summary>
    /// Opens an OpenTelemetry <c>execute_tool</c> child span for a tool call as a child of the
    /// current agent-turn span. Tags follow the gen AI semantic conventions
    /// (<c>gen_ai.tool.name</c>, <c>gen_ai.operation.name = execute_tool</c>) plus the
    /// Agentweaver span-kind marker so the transaction-trace tree can classify it as a tool node.
    /// The parent is the turn span's captured <see cref="ActivityContext"/> (<see cref="_turnActivity"/>),
    /// passed explicitly rather than relying on ambient <c>Activity.Current</c> — if a different
    /// tool call's span is still open when this one starts (overlapping tool calls), ambient
    /// parenting would nest this span under that other tool span instead of under the turn.
    /// </summary>
    private void StartToolSpan(string callId, string toolName, DateTimeOffset? startTime = null)
    {
        var activity = StartToolSpanCore(_turnActivity, toolName, startTime);
        if (activity is null) return;
        ConfigureToolSpanTags(activity, toolName, callId, _agentName, _runId);
        if (!_activeToolSpans.TryAdd(callId, activity))
            activity.Dispose();
    }

    /// <summary>
    /// Starts the <c>execute_tool</c> <see cref="Activity"/>, parented explicitly to
    /// <paramref name="turnActivity"/>'s <see cref="ActivityContext"/> when available, rather
    /// than relying on ambient <c>Activity.Current</c>. This is the core of the overlapping
    /// tool-call fix (issue #200): if a different tool call's span is still open when this one
    /// starts, <c>Activity.Current</c> would be that other tool span, and ambient parenting
    /// would incorrectly nest this new span under it instead of under the turn. Extracted as an
    /// internal static helper (mirroring <see cref="ConfigureToolSpanTags"/>) so the parenting
    /// behavior can be unit-tested without constructing the heavyweight <see cref="CopilotAIAgent"/>.
    /// <para>
    /// <paramref name="startTime"/> is the SDK <c>ToolExecutionStartEvent.Timestamp</c> — the
    /// moment the tool lifecycle actually began at the source. When supplied it stamps the span's
    /// start time rather than defaulting to "now" (when our single-consumer stream loop happened
    /// to observe the event). This is the start half of the issue #546 fix: because the GitHub
    /// Copilot SDK dispatches tool calls sequentially and a blocked sibling (e.g. a
    /// <c>web_fetch</c> waiting out its 5-minute HITL approval deadline) stalls delivery of every
    /// other tool's lifecycle events, observation-time bounding inflates innocent fast tools'
    /// durations to the same wall-clock value. Anchoring to the SDK timestamp decouples the
    /// recorded duration from that consumer-loop back-pressure.
    /// </para>
    /// </summary>
    internal static Activity? StartToolSpanCore(Activity? turnActivity, string toolName, DateTimeOffset? startTime = null)
    {
        var activity = turnActivity is not null
            ? ActivitySource.StartActivity($"execute_tool {toolName}", ActivityKind.Internal, turnActivity.Context)
            : ActivitySource.StartActivity($"execute_tool {toolName}", ActivityKind.Internal);
        if (activity is not null && startTime is { } ts && ts != default)
            activity.SetStartTime(ts.UtcDateTime);
        return activity;
    }

    /// <summary>
    /// Applies the gen AI semantic-convention tags to an <c>execute_tool</c> span. Extracted as an
    /// internal static helper so it can be unit-tested independently of the (heavyweight) agent
    /// lifecycle.
    /// </summary>
    internal static void ConfigureToolSpanTags(Activity activity, string toolName, string callId, string? agentName, string? runId)
    {
        activity.SetTag("agentweaver.span.kind", "tool_call");
        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.name", toolName);
        activity.SetTag("tool_name", toolName);
        activity.SetTag("tool.call.id", callId);
        activity.SetTag("run_id", runId);
        activity.SetTag("run.id", runId);
        if (!string.IsNullOrWhiteSpace(agentName))
            activity.SetTag("gen_ai.agent.name", agentName);
    }

    /// <summary>
    /// Closes the <c>execute_tool</c> span previously opened for <paramref name="callId"/>,
    /// recording success/error status. No-op if the span was never opened (e.g. defensive
    /// call-before-result paths or suppressed tools).
    /// <para>
    /// <paramref name="endTime"/> is the SDK <c>ToolExecutionCompleteEvent.Timestamp</c> — the
    /// moment the tool lifecycle actually completed at the source. When supplied it stamps the
    /// span's end time rather than defaulting to "now" (when our single-consumer stream loop
    /// happened to observe the completion). This is the end half of the issue #546 fix (see
    /// <see cref="StartToolSpanCore"/>): it keeps a fast tool's recorded duration honest even when
    /// its completion event was delivered late behind a blocked sibling. The SDK timestamp is
    /// clamped to be no earlier than the span's own start so a clock skew can never yield a
    /// negative duration; if it would, we fall back to observation-time (Dispose default).
    /// </para>
    /// </summary>
    private void CompleteToolSpan(string callId, bool success, string? error, DateTimeOffset? endTime = null)
    {
        if (!_activeToolSpans.TryRemove(callId, out var activity))
            return;
        CompleteToolSpanCore(activity, success, error, endTime);
    }

    /// <summary>
    /// Applies success/error status and the SDK completion timestamp to an already-open
    /// <c>execute_tool</c> span, then disposes it. Extracted as an internal static helper
    /// (mirroring <see cref="StartToolSpanCore"/> and <see cref="ConfigureToolSpanTags"/>) so the
    /// issue #546 end-time-anchoring behavior can be unit-tested without constructing the
    /// heavyweight <see cref="CopilotAIAgent"/>. The end timestamp is clamped to be no earlier
    /// than the span's start so clock skew can never produce a negative duration; when the SDK
    /// timestamp is absent, default, or would go backwards, the span falls back to
    /// observation-time bounding (the <see cref="Activity.Dispose"/> default).
    /// </summary>
    internal static void CompleteToolSpanCore(Activity activity, bool success, string? error, DateTimeOffset? endTime)
    {
        activity.SetTag("gen_ai.tool.call.success", success);
        activity.SetStatus(success ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error);
        if (!success && !string.IsNullOrWhiteSpace(error))
            activity.SetTag("error.message", error);
        if (endTime is { } ts && ts != default)
        {
            var endUtc = ts.UtcDateTime;
            if (endUtc >= activity.StartTimeUtc)
                activity.SetEndTime(endUtc);
        }
        activity.Dispose();
    }

    /// <summary>
    /// Builds the permission handler that enforces sandbox containment through
    /// two independent layers: AGT policy evaluation AND direct SandboxPolicyBackend check.
    /// Both must allow for the tool call to proceed. The handler is also a per-tool
    /// observability source. A denied call is surfaced here as a tool.call + tool.error pair
    /// carrying the gate reason. Approved calls surface from the streaming lifecycle (call + real
    /// result); the handler only co-emits its tool.call when it holds the SDK's real ToolCallId,
    /// so the two sources dedup instead of diverging. Native-shell denials are a special case:
    /// the SDK can still emit a raw shell <c>tool.call</c> start event for the same ToolCallId,
    /// so the lifecycle translator normalizes that start event to <c>run_command</c> to keep the
    /// first event the frontend sees consistent with this handler's hard denial.
    /// </summary>
    internal Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildPermissionHandler(
        SandboxGovernance governance,
        string runId,
        string workingDirectory,
        Action<string, string, object?> emitToolCallOnce,
        Action<string, string> emitToolErrorOnce,
        Action<string, object> emit,
        CancellationToken runCt,
        bool denyNativeShell = true)
    {
        _denyNativeShellLifecycleToolCalls = denyNativeShell;
        return (request, invocation) =>
        {
            if (denyNativeShell && request is PermissionRequestShell)
            {
                var shellCallId = GetToolCallId(request) ?? Guid.NewGuid().ToString("n");
                var (_, shellArgs) = MapToToolCall(request);
                shellArgs["directory"] = workingDirectory;
                var denyReason = BuildNativeShellDenyReason(
                    Interlocked.Increment(ref _nativeShellDenyAttempts));
                emitToolCallOnce(shellCallId, "run_command", shellArgs);
                emitToolErrorOnce(shellCallId, denyReason);
                EmitRunDegradedOnce("run_command", denyReason);
                return Task.FromResult<PermissionDecision>(
                    PermissionDecision.Reject(denyReason));
            }

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
                    return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
                }

                // Auto-approve-tools run option: grant the allow-with-approval request without an
                // operator. SAFETY: this branch is only reached for PermissionRequestUrl (web_fetch),
                // an allow-with-approval tool. Policy-DENIED tools are rejected in the custom/native
                // governance branches below and never reach this gate, so auto-approve can never
                // override a deny. Every auto-grant is logged on the timeline for audit.
                if (_runOptions?.Get(runId).AutoApproveTools == true)
                {
                    emit(EventTypes.ToolAutoApproved, new { requestId, toolName = "web_fetch", url = SanitizeUrl(rawUrl) });
                    _logger.LogInformation(
                        "Tool HITL auto-approved (run option) — requestId={RequestId} runId={RunId}", displayId, runId);
                    return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
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

                // Heartbeat-punctuated wait: block the SDK callback thread on the gate, but wake
                // every ApprovalHeartbeatInterval to emit a lightweight tool.approval_pending frame.
                // The bridge drains the run-event channel on a separate task, so each heartbeat is
                // flushed over A2A/SSE immediately — keeping the pod's outbound stream moving while
                // the operator decides so the buffered tool.approval_required is delivered + durably
                // persisted promptly and the parent coordinator's stall timer is reset (issue #212).
                while (!approvalTask.Wait((int)ApprovalHeartbeatInterval.TotalMilliseconds))
                {
                    emit(EventTypes.ToolApprovalPending, new
                    {
                        requestId,
                        displayId,
                        toolName = "web_fetch",
                    });
                }

                var approved = approvalTask.ConfigureAwait(false).GetAwaiter().GetResult();

                if (!approved)
                {
                    const string denyReason = "URL fetch was denied by the operator.";
                    emitToolErrorOnce(urlCallId, denyReason);
                    _logger.LogInformation("Tool HITL denied — requestId={RequestId} runId={RunId}", displayId, runId);
                    return Task.FromResult(PermissionDecision.Reject(denyReason));
                }

                _logger.LogInformation("Tool HITL approved — requestId={RequestId} runId={RunId}", displayId, runId);
                return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
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

                        return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
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

                        return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
                    }

                    // Agentweaver API tools: auto-approve without sandbox governance.
                    // The actual HTTP call executes in the function body after approval;
                    // the streaming lifecycle emits tool.result when the function returns.
                    if (AgentweaverApiToolNames.Contains(toolName))
                    {
                        var apiArgs = new Dictionary<string, object>();
                        if (customTool.Args is System.Text.Json.JsonElement apiArgsEl &&
                            apiArgsEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            foreach (var prop in apiArgsEl.EnumerateObject())
                                apiArgs[prop.Name] = prop.Value;
                        }
                        // Instrumented tools (IAgentRuntimeToolProvider-built, e.g. start_preview)
                        // already record their own tool.call/tool.result/tool.error directly around
                        // the real invocation (see InstrumentedCustomAIFunction) — skip the
                        // pre-emptive emit here so the trace doesn't show a second, orphaned call.
                        if (realCustomCallId is not null && !_instrumentedToolNames.Contains(toolName))
                            emitToolCallOnce(customCallId, toolName, apiArgs);
                        return Task.FromResult<PermissionDecision>(new PermissionDecisionApproveOnce());
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
                    // Instrumented tools (see InstrumentedCustomAIFunction) emit their own tool.call
                    // right before invocation, so skip this pre-emptive emit for them on the
                    // approve path — but keep it for the deny/exception paths below, since a denied
                    // or failed-to-evaluate call never reaches the wrapper's InvokeCoreAsync and
                    // would otherwise go unrecorded entirely.
                    if (realCustomCallId is not null && !_instrumentedToolNames.Contains(toolName))
                        emitToolCallOnce(customCallId, toolName, args);

                    var (allowed, reason) = governance.EvaluateToolCall(
                        agentId: $"did:mesh:agentweaver:copilot:{runId}",
                        toolName: toolName,
                        args: args,
                        _logger);

                    if (!allowed)
                    {
                        emitToolCallOnce(customCallId, toolName, args);
                        var denyReason = reason ?? "Operation denied by sandbox policy.";
                        emitToolErrorOnce(customCallId, denyReason);
                        EmitRunDegradedOnce(toolName, denyReason);
                        return Task.FromResult(PermissionDecision.Reject(denyReason));
                    }

                    return Task.FromResult<PermissionDecision>(PermissionDecision.ApproveOnce());
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
                    return Task.FromResult(PermissionDecision.Reject(failReason));
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
                    agentId: $"did:mesh:agentweaver:copilot:{runId}",
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
                    return Task.FromResult(PermissionDecision.Reject(denyReason2));
                }
                else if (request is PermissionRequestShell shell && realCallId is not null)
                {
                    TrackApprovedShell(realCallId, shell.FullCommandText ?? string.Empty);
                }

                return Task.FromResult<PermissionDecision>(PermissionDecision.ApproveOnce());
            }
            catch (Exception ex)
            {
                // Fail-closed: any failure mapping or evaluating the request denies the tool call.
                _logger.LogError(ex, "Permission handler exception (fail-closed deny) — RunId={RunId}", runId);
                var failReason2 = "Operation denied: internal error evaluating sandbox policy.";
                emitToolCallOnce(callId, request.Kind ?? "unknown", null);
                emitToolErrorOnce(callId, failReason2);
                EmitRunDegradedOnce(request.Kind ?? "unknown", failReason2);
                return Task.FromResult(PermissionDecision.Reject(failReason2));
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

    private void TrackApprovedShell(string toolCallId, string command)
    {
        if (_shellExecutionTracker is null)
            return;

        var commandHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(command)))[..16]
            .ToLowerInvariant();
        if (!_shellExecutionTracker.TryStartObservedExecution(
                toolCallId,
                commandHash,
                ShellExecutionHardTimeout,
                _shellExecutionGeneration))
        {
            _logger.LogWarning(
                "shell_lifecycle_stale_generation — shell start ignored because another shell is active or the generation is fenced; runId={RunId}, toolCallId={ToolCallId}, generation={Generation}",
                _runId,
                toolCallId,
                _shellExecutionGeneration);
        }
    }

    private static bool IsShellToolName(string toolName) =>
        string.Equals(toolName, "run_command", StringComparison.OrdinalIgnoreCase) ||
        toolName.Contains("shell", StringComparison.OrdinalIgnoreCase);

    internal static bool IsNativeShellLifecycleToolName(string toolName) =>
        string.Equals(toolName, "bash", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "sh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "shell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "powershell", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "pwsh", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, "cmd", StringComparison.OrdinalIgnoreCase);

    internal static string BuildNativeShellDenyReason(int attemptNumber)
    {
        const string baseReason =
            "Native Copilot shell is disabled; use the sandboxed run_command tool (routed through the sandbox executor).";
        if (attemptNumber <= 1)
            return baseReason;

        return $"{baseReason} This is attempt {attemptNumber} to use the disabled native shell in this run; stop retrying it and use run_command for any remaining shell commands.";
    }

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
            (path[^1] == '\\' ||
             path[^1] == '/' ||
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
    /// Wraps a custom <see cref="AIFunction"/> so its <c>tool.call</c> / <c>tool.result</c> /
    /// <c>tool.error</c> RunEvents and its <c>execute_tool</c> OTel span are recorded directly
    /// around the real invocation, instead of relying on the SDK's external-tool lifecycle
    /// events (<c>ExternalToolRequestedEvent</c> / <c>ExternalToolCompletedEvent</c>).
    ///
    /// <para>
    /// That SDK lifecycle pair exists for tools registered outside the SDK's own built-in set
    /// (which is how every custom <see cref="AIFunction"/>, including <c>start_preview</c>, is
    /// registered), but <c>ExternalToolCompletedEvent</c> carries no result content — only a bare
    /// <c>RequestId</c> acknowledgement — and no <c>execute_tool</c> span is ever opened for this
    /// pairing (that only happens for <c>ToolExecutionStartEvent</c>/<c>ToolExecutionCompleteEvent</c>,
    /// the SDK's native-tool lifecycle). For <c>start_preview</c> specifically this meant the trace
    /// panel could never find a matching span/RunEvent pair, so both Arguments and Output showed as
    /// "not recorded" even though the tool executed successfully — the prior claim that issue #850's
    /// PR #853 fully fixed this was wrong; #853 only built the frontend correlation logic and assumed
    /// every tool populates both halves of the join.
    /// </para>
    ///
    /// <para>
    /// Instrumenting the invocation directly sidesteps the gap entirely: this wrapper mints the
    /// <c>callId</c> itself, so the span tag and the RunEvents are guaranteed to share the same id,
    /// and the recorded arguments/content are exactly what was passed to and returned by the tool —
    /// nothing depends on SDK-internal event shapes or timing.
    /// </para>
    /// </summary>
    // internal (not private) so StartPreviewToolTests can construct it directly and assert the
    // emitted callId/args/content pairing without spinning up a full CopilotAIAgent instance.
    internal sealed class InstrumentedCustomAIFunction(
        AIFunction inner,
        Action<string, string, object?> emitToolCallOnce,
        Action<string, string> emitToolResultOnce,
        Action<string, string> emitToolErrorOnce,
        Action<string, string, DateTimeOffset?> startToolSpan,
        Action<string, bool, string?, DateTimeOffset?> completeToolSpan) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;
        public override JsonElement JsonSchema => inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var callId = Guid.NewGuid().ToString("n");
            var argsDict = arguments.Count > 0
                ? new Dictionary<string, object?>(arguments)
                : null;

            var startTime = DateTimeOffset.UtcNow;
            startToolSpan(callId, inner.Name, startTime);
            emitToolCallOnce(callId, inner.Name, argsDict);

            try
            {
                var result = await inner.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                var content = result switch
                {
                    null => string.Empty,
                    string s => s,
                    _ => JsonSerializer.Serialize(result),
                };
                completeToolSpan(callId, true, null, DateTimeOffset.UtcNow);
                emitToolResultOnce(callId, content);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                completeToolSpan(callId, false, ex.Message, DateTimeOffset.UtcNow);
                emitToolErrorOnce(callId, ex.Message);
                throw;
            }
        }
    }

    /// <summary>
    /// Builds the full base system prompt, optionally including the TEAM COORDINATION section.
    /// That section references list_decisions/get_memory/list_inbox/submit_decision, which are
    /// only registered as tools when Agentweaver API tools were built (see
    /// <see cref="BuildSessionConfigTools"/>). Pass <c>false</c> when those tools are not part of
    /// the session's tool list to avoid the agent hallucinating calls to them (#268).
    /// </summary>
    internal static string BuildBasePrompt(bool includeTeamCoordination) =>
        includeTeamCoordination
            ? AgentBasePrompt.Base + AgentBasePrompt.TeamCoordination
            : AgentBasePrompt.Base;

    /// <summary>
    /// Builds the tool list for <see cref="SessionConfig.Tools"/>:
    /// <c>report_intent</c> and <c>report_outcome</c> (wrapped as native overrides so the SDK accepts them),
    /// plus Agentweaver API tools when <paramref name="projectId"/> and <paramref name="agentName"/> are set.
    /// </summary>
    internal static IList<AIFunction> BuildSessionConfigTools(
        SandboxToolContext context,
        string? projectId = null,
        string? agentName = null,
        string? apiBaseUrl = null,
        string? apiKey = null,
        IEnumerable<IAgentRuntimeToolProvider>? toolProviders = null,
        bool includeControlledRunCommand = false,
        string? runCapabilityToken = null,
        Func<AIFunction, AIFunction>? instrumentProviderTool = null)
    {
        var all = SandboxToolRegistry.Build(context);
        var intentFn = all.First(f => string.Equals(f.Name, "report_intent", StringComparison.Ordinal));
        var outcomeFn = all.First(f => string.Equals(f.Name, "report_outcome", StringComparison.Ordinal));

        var tools = new List<AIFunction>
        {
            new CopilotOverrideAIFunction(intentFn),
            new CopilotOverrideAIFunction(outcomeFn),
        };

        // ask_question blocks on the question gate; only present it when a gate is wired so the
        // model never calls a tool that cannot resolve.
        if (context.QuestionGate is not null)
        {
            var askFn = all.First(f => string.Equals(f.Name, "ask_question", StringComparison.Ordinal));
            tools.Add(new CopilotOverrideAIFunction(askFn));
        }

        if (includeControlledRunCommand)
        {
            // run_command is present in the registry only when the executor provides real isolation
            // (or direct mode) AND policy shell is enabled (see SandboxToolRegistry.Build). When shell
            // is disabled or the executor offers no isolation, it is intentionally absent — combined
            // with the native-shell denial in BuildPermissionHandler, that leaves NO shell path, which
            // is the correct fail-closed behavior for a shell-disabled run.
            var commandFn = all.FirstOrDefault(f => string.Equals(f.Name, "run_command", StringComparison.Ordinal));
            if (commandFn is not null)
                tools.Add(commandFn);
        }

        if (!string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(agentName))
        {
            var effectiveBaseUrl = apiBaseUrl ?? "http://localhost:5000";
            tools.AddRange(AgentweaverApiTools.Build(
                projectId,
                agentName,
                effectiveBaseUrl,
                apiKey,
                runId: context.RunId,
                runCapabilityToken: runCapabilityToken));
        }

        if (toolProviders is not null)
        {
            // #335 P1 (start_preview gap): providers like PreviewRunnerToolProvider previously only
            // saw the immutable, image-baked context (no ApiBaseUrl/ApiKey on warm pods), so their
            // API-calling tools fell back to an unreachable http://localhost:5000 and threw an
            // uncaught transport exception — surfaced to the model as an opaque "Tool execution
            // failed". Thread the same per-turn apiBaseUrl/apiKey already resolved for
            // AgentweaverApiTools above into the context every provider builds against.
            var providerContext = context with { ApiBaseUrl = apiBaseUrl, ApiKey = apiKey };
            foreach (var provider in toolProviders)
            {
                foreach (var providerTool in provider.BuildTools(providerContext))
                {
                    // #850 follow-up: tools built by IAgentRuntimeToolProvider implementations
                    // (start_preview, start_preview_process, observe_bound_port, health_check,
                    // stop_preview_process) never had any tool.call/tool.result RunEvent or
                    // execute_tool span instrumentation — see InstrumentedCustomAIFunction for why
                    // that made start_preview's trace card show "No arguments/output recorded".
                    tools.Add(instrumentProviderTool?.Invoke(providerTool) ?? providerTool);
                }
            }
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
            _questionGate?.Clear(_runId);
            _runOptions?.Clear(_runId);
        }
        // Dispose (not delete) the inner agent so the SDK persists session events for history replay.
        if (_inner is IAsyncDisposable disposableAgent)
            await disposableAgent.DisposeAsync().ConfigureAwait(false);
        if (_client is IAsyncDisposable disposableClient)
            await disposableClient.DisposeAsync().ConfigureAwait(false);
        _shellExecutionTracker?.Dispose();
        _shellExecutionTracker = null;
    }
}
