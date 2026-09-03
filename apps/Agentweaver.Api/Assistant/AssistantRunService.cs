using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Options;

namespace Agentweaver.Api.Assistant;

/// <summary>
/// Tunables for the operator assistant run substrate (#346). Bound from the <c>Assistant</c>
/// configuration section.
/// </summary>
public sealed class AssistantRunOptions
{
    /// <summary>Maximum number of concurrently-open operator runs a single user may hold. A new
    /// start is rejected with 429 once the user is at this bound.</summary>
    public int MaxConcurrentRunsPerUser { get; set; } = 3;

    /// <summary>How long an operator run may sit without a new message before it is auto-closed
    /// (transitioned to Completed and its stream completed), releasing the concurrency slot.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often the idle sweeper runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// De-dupe window for <see cref="IAssistantRunService.StartRunAsync"/>. <c>StartRunAsync</c> runs
    /// the opening turn SYNCHRONOUSLY before the HTTP response returns, so a first turn with several
    /// tool calls can outlast a client-side fetch/proxy timeout; if the client then retries with the
    /// identical opening message believing the first attempt failed, the retry would otherwise mint a
    /// second, fully independent run while the original is still executing (observed live: two
    /// Operator runs with an identical opening message, 64 seconds apart). When a still-InProgress run
    /// for the same user with the identical opening message started within this window, the retry is
    /// treated as a duplicate and the existing run is returned instead of starting a new one.</summary>
    public TimeSpan DuplicateStartWindow { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>Raised when a user tries to open more concurrent operator runs than
/// <see cref="AssistantRunOptions.MaxConcurrentRunsPerUser"/> allows. Mapped to HTTP 429.</summary>
public sealed class AssistantConcurrencyLimitException(int limit)
    : Exception($"Operator run limit reached: at most {limit} concurrent assistant conversations per user.")
{
    public int Limit { get; } = limit;
}

/// <summary>Raised when a message targets a run that is not an operator run, is not owned by the
/// caller, or has already been closed. Carries the HTTP status the endpoint should return.</summary>
public sealed class AssistantRunHttpException(int statusCode, string error, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
}

public sealed record StartAssistantRunResult(RunId RunId, RunStatus Status, OperatorAssistantResponse? FirstTurn);

/// <summary>A single operator conversation in a caller's recent-conversations list.</summary>
public sealed record AssistantRunSummary(string RunId, RunStatus Status, string Title, DateTimeOffset CreatedAt);

public interface IAssistantRunService
{
    /// <summary>Creates a new operator run for the caller and, when <paramref name="firstMessage"/> is
    /// supplied, runs the opening turn. Enforces the per-user concurrency bound. When
    /// <paramref name="resumeFromRunId"/> is supplied, the new run's model context is pre-loaded with
    /// that prior operator run's conversation history (typically a run the user's previous session was
    /// idle-closed) — the old run itself is never modified or revived.</summary>
    Task<StartAssistantRunResult> StartRunAsync(
        CallerContext caller,
        string callerBearerToken,
        string? firstMessage,
        string? projectId,
        string? contextRunId,
        string? modelId,
        CancellationToken ct,
        string? resumeFromRunId = null);

    /// <summary>Runs the next conversational turn on an existing operator run owned by the caller.</summary>
    Task<OperatorAssistantResponse> SendMessageAsync(
        CallerContext caller,
        string callerBearerToken,
        string runId,
        string message,
        CancellationToken ct);

    /// <summary>Lists the caller's own operator conversations, newest-first, capped at
    /// <paramref name="limit"/>. Scoped to the authenticated caller — never returns other users'
    /// runs.</summary>
    Task<IReadOnlyList<AssistantRunSummary>> ListRunsAsync(
        CallerContext caller,
        int limit,
        CancellationToken ct);
}

/// <summary>
/// Backend service (#346) that models an MCP-driven operator chat as a lightweight "operator run":
/// a persisted <see cref="Run"/> with <c>AgentName == "Operator"</c>, no work plan and no children,
/// whose turns stream onto the existing <see cref="IRunEventStream"/> so the unchanged
/// <c>GET /api/runs/{id}/stream</c> and <c>/events</c> endpoints serve the transcript.
///
/// It wires <see cref="IOperatorAssistantAgent"/> (the in-API Copilot loop sourcing its tools from the
/// AgentweaverMCP server) to run one turn at a time using the caller's platform bearer token, threaded
/// through per call — no token is cached or shared across users. An in-memory per-user concurrency
/// bound and an idle-timeout sweep keep the number of live Copilot/MCP sessions bounded (v1: single
/// instance; a distributed bound is a fast-follow if the API scales out).
///
/// This is additive: it does not touch the existing <c>/api/console/turn</c> facade path.
/// </summary>
public sealed class AssistantRunService : IAssistantRunService, IDisposable
{
    /// <summary>Sentinel AgentName that marks a run as an operator chat (mirrors "Coordinator").</summary>
    public const string OperatorAgentName = "Operator";

    private const int MaxHistoryMessages = 24;

    private readonly IRunStore _runStore;
    private readonly IRunEventStream _eventStream;
    private readonly IOperatorAssistantAgent _assistant;
    private readonly IToolApprovalGate _approvalGate;
    private readonly AssistantRunOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _agentHostEnabled;
    private readonly ILogger<AssistantRunService> _logger;

    /// <summary>How long a gated tool call waits for an operator decision before it is treated as
    /// denied. Mirrors the sandbox web_fetch approval gate's convention.</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How often a <c>tool.approval_pending</c> heartbeat is emitted while the turn blocks on
    /// an operator decision, so the run's SSE stream keeps flowing and the buffered
    /// <c>tool.approval_required</c> frame is delivered promptly.</summary>
    private static readonly TimeSpan ApprovalHeartbeatInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, OperatorRunState> _runs = new(StringComparer.Ordinal);
    private readonly object _startLock = new();
    private readonly Timer _idleSweeper;

    public AssistantRunService(
        IRunStore runStore,
        IRunEventStream eventStream,
        IOperatorAssistantAgent assistant,
        IToolApprovalGate approvalGate,
        IOptions<AssistantRunOptions> options,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<AssistantRunService> logger)
    {
        _runStore = runStore;
        _eventStream = eventStream;
        _assistant = assistant;
        _approvalGate = approvalGate;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _agentHostEnabled = string.Equals(
            configuration["Sandbox:AgentExecutionMode"],
            "pod-per-run",
            StringComparison.OrdinalIgnoreCase);
        _logger = logger;

        var interval = _options.SweepInterval > TimeSpan.Zero ? _options.SweepInterval : TimeSpan.FromMinutes(1);
        _idleSweeper = new Timer(_ => SweepIdleRunsSafe(), state: null, interval, interval);
    }

    public async Task<StartAssistantRunResult> StartRunAsync(
        CallerContext caller,
        string callerBearerToken,
        string? firstMessage,
        string? projectId,
        string? contextRunId,
        string? modelId,
        CancellationToken ct,
        string? resumeFromRunId = null)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var now = DateTimeOffset.UtcNow;

        // "Auto-seed" resume (#347 follow-up): when the caller's previous operator conversation was
        // idle-closed (or otherwise no longer reachable via RehydrateRunAsync), the frontend starts a
        // BRAND-NEW run and passes that old run's id here so the new conversation's model context is
        // pre-loaded with the prior transcript. This validates and reads the OLD run only — it is never
        // written to, revived, or otherwise modified; the sealed-run guard in RehydrateRunAsync is
        // completely untouched. When resumeFromRunId is null/blank (the default, existing path) none of
        // this runs and behavior is unchanged.
        IReadOnlyList<ConsoleFacadeHistoryMessage>? resumeHistory = null;
        if (!string.IsNullOrWhiteSpace(resumeFromRunId))
        {
            if (!RunId.TryParse(resumeFromRunId, out var parsedResumeRunId))
                throw new AssistantRunHttpException(StatusCodes.Status404NotFound, "run_not_found",
                    "No active operator run with that id. It may have been closed after an idle timeout.");

            var oldRun = await _runStore.GetAsync(parsedResumeRunId, ct).ConfigureAwait(false);
            if (oldRun is null || !string.Equals(oldRun.AgentName, OperatorAgentName, StringComparison.Ordinal))
                throw new AssistantRunHttpException(StatusCodes.Status404NotFound, "run_not_found",
                    "No active operator run with that id. It may have been closed after an idle timeout.");

            if (!caller.Owns(oldRun.SubmittingUser))
                throw new AssistantRunHttpException(StatusCodes.Status403Forbidden, "forbidden",
                    "You do not own this operator run.");

            var oldEvents = await _eventStream
                .GetPersistedEventsAsync(resumeFromRunId, fromSequence: 0, ct)
                .ConfigureAwait(false);
            resumeHistory = BuildHistoryFromEvents(oldEvents);
        }

        // De-dupe guard against a retry-while-still-processing double submit (see
        // AssistantRunOptions.DuplicateStartWindow for why this is needed). DB-backed (not the local
        // _runs cache) so a retry landing on a different API replica is still caught. Only matches a
        // run that is STILL InProgress — a genuinely-finished conversation with the same opening line
        // is not treated as a duplicate.
        if (!string.IsNullOrWhiteSpace(firstMessage))
        {
            var recent = await _runStore
                .GetRunsBySubmittingUserAsync(caller.User, OperatorAgentName, limit: 5, ct)
                .ConfigureAwait(false);
            var duplicate = recent.FirstOrDefault(r =>
                r.Status == RunStatus.InProgress &&
                now - r.StartedAt <= _options.DuplicateStartWindow &&
                string.Equals(r.Task, firstMessage, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                _logger.LogInformation(
                    "StartRunAsync: request for user {User} matches still-running run {RunId} with an " +
                    "identical opening message started {SecondsAgo:0}s ago; returning it instead of " +
                    "starting a duplicate.",
                    caller.User, duplicate.Id, (now - duplicate.StartedAt).TotalSeconds);
                return new StartAssistantRunResult(duplicate.Id, duplicate.Status, FirstTurn: null);
            }
        }

        var runId = RunId.New();
        var key = runId.ToString();

        // Reserve the concurrency slot atomically before any IO so two parallel starts cannot both
        // slip past the bound.
        lock (_startLock)
        {
            var active = _runs.Values.Count(s => string.Equals(s.User, caller.User, StringComparison.Ordinal));
            if (active >= _options.MaxConcurrentRunsPerUser)
                throw new AssistantConcurrencyLimitException(_options.MaxConcurrentRunsPerUser);

            _runs[key] = new OperatorRunState(caller.User, projectId, modelId, now, seedHistory: resumeHistory);
        }

        try
        {
            ProjectId? project = ProjectId.TryParse(projectId, out var pid) ? pid : null;
            var modelSource = await ResolveAssistantModelSourceAsync(ct).ConfigureAwait(false);
            var run = new Run
            {
                Id = runId,
                // Operator runs drive the platform via MCP tools; they have no worktree/repo. The
                // empty placeholders satisfy the required Run fields without implying a workspace.
                RepositoryPath = string.Empty,
                OriginatingBranch = string.Empty,
                ModelSource = modelSource,
                Task = firstMessage ?? "Operator assistant conversation",
                SubmittingUser = caller.User,
                Status = RunStatus.InProgress,
                StartedAt = now,
                ProjectId = project,
                ModelId = modelId,
                AgentName = OperatorAgentName,
                ParentRunId = null,
                SubtaskId = null,
            };

            await PrepareAgentHostCapabilityAsync(run, ct).ConfigureAwait(false);
            await _runStore.InsertAsync(run, ct).ConfigureAwait(false);
        }
        catch
        {
            _runs.TryRemove(key, out _);
            throw;
        }

        await AppendAsync(key, EventTypes.RunStarted, new
        {
            runId = key,
            kind = "operator",
            agentName = OperatorAgentName,
            projectId,
            contextRunId,
            resumedFromRunId = resumeFromRunId,
        }, ct).ConfigureAwait(false);

        OperatorAssistantResponse? firstTurn = null;
        if (!string.IsNullOrWhiteSpace(firstMessage))
            firstTurn = await RunTurnAsync(caller, callerBearerToken, key, firstMessage!, contextRunId, ct)
                .ConfigureAwait(false);

        return new StartAssistantRunResult(runId, RunStatus.InProgress, firstTurn);
    }

    /// <summary>
    /// Resolves the model source for an Assistant run via the shared
    /// <see cref="EffectiveModelProviderResolver"/>, deliberately at PLATFORM scope
    /// (<c>projectId: null</c>) — deployment BYOK first, else the platform-default GitHub Copilot
    /// binding.
    ///
    /// <para>
    /// Passing the session's <c>ProjectId</c> here would be wrong, and was the cause of a live bug:
    /// per the resolver's documented precedence an active project Copilot binding ALWAYS beats
    /// platform-level BYOK, so a lingering project binding silently pinned a session to Copilot even
    /// after the deployment was switched to BYOK. Worse, that selection disagreed with the credential
    /// CHECK for the same run, which is deliberately platform-scoped
    /// (<see cref="PrepareAgentHostCapabilityAsync"/> — an Assistant session's <c>ProjectId</c> is
    /// only incidental UI context, never repo-scoped work). Selection and validation must agree, and
    /// platform scope is the one that matches what these sessions actually are; the incidental
    /// <c>ProjectId</c> stays on the run purely as MCP/UI context.
    /// </para>
    ///
    /// <para>
    /// This label only drives bookkeeping and the agent-host capability gate; a genuinely unavailable
    /// provider is NOT fatal here — the in-API operator loop can still turn using the caller's own
    /// live bearer token, exactly as before this resolver existed.
    /// <see cref="PrepareAgentHostCapabilityAsync"/> remains the sole place that fails fast when
    /// agent-host mode actually needs a redeemable capability and none is available.
    /// </para>
    /// </summary>
    private async Task<ModelSource> ResolveAssistantModelSourceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var modelProviderResolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        var effectiveProvider = await modelProviderResolver.ResolveAsync(projectId: null, ct).ConfigureAwait(false);
        return effectiveProvider is EffectiveModelProviderResult.Byok
            ? ModelSource.Byok
            : ModelSource.GitHubCopilot;
    }

    /// <summary>
    /// Re-resolves the effective model provider at the START OF EVERY TURN and repoints the persisted
    /// run at it when it has changed since the previous turn.
    ///
    /// <para>
    /// Without this the provider was pinned at session-creation time forever: <c>ModelSource</c> was
    /// computed once in <see cref="StartRunAsync"/>, and neither <see cref="SendMessageAsync"/> nor
    /// <see cref="RehydrateRunAsync"/> (which rebuilds state from the stored row) ever revisited it —
    /// so switching the deployment to BYOK mid-conversation had no visible effect until the user
    /// started a brand-new session.
    /// </para>
    ///
    /// <para>
    /// A change is applied transparently to the NEXT turn: the conversation keeps its history (which
    /// is replayed as plain text on every turn and is not provider-specific), and the persisted
    /// <c>ModelSource</c> is what the downstream per-turn credential fence reads — including
    /// <c>RemoteOperatorAssistantAgent.EnsureAgentHostCapabilityAsync</c>, which loads the run row
    /// fresh each turn — so selection and validation stay in agreement after the switch too. When the
    /// new provider is GitHub Copilot the capability gate is re-run immediately so an unusable
    /// platform connection fails fast with the "Connect GitHub" CTA rather than deep inside the turn.
    /// </para>
    /// </summary>
    private async Task ReresolveModelSourceForTurnAsync(string runId, CancellationToken ct)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
            return;

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
        if (run is null)
            return;

        var modelSource = await ResolveAssistantModelSourceAsync(ct).ConfigureAwait(false);
        if (modelSource == run.ModelSource)
            return;

        _logger.LogInformation(
            "Operator run {RunId}: effective model provider changed {Previous} -> {Current} since the last " +
            "turn; the new provider takes effect for this turn.",
            runId, run.ModelSource.ToApiString(), modelSource.ToApiString());

        var repointed = run with { ModelSource = modelSource };
        await PrepareAgentHostCapabilityAsync(repointed, ct).ConfigureAwait(false);
        await _runStore.UpdateModelSourceAsync(parsedRunId, modelSource, ct).ConfigureAwait(false);
    }

    private async Task PrepareAgentHostCapabilityAsync(Run run, CancellationToken ct)
    {
        if (!_agentHostEnabled || run.ModelSource != ModelSource.GitHubCopilot)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<RunGitHubCapabilitySnapshotLifecycle>();

        // Operator/Assistant runs are personal sessions, not project-scoped work: run.ProjectId (when
        // present) is only incidental UI context (the project the caller happened to be viewing), so
        // credential resolution must always go through the PLATFORM-level Copilot connection rather
        // than that project's own (possibly broken/missing) binding. Hence platformScoped: true, and
        // a failure always surfaces the platform-settings CTA, never a project-specific one. This is
        // the SAME scope ResolveAssistantModelSourceAsync selects the provider at, so selection and
        // validation cannot disagree.
        if (!await lifecycle.PrepareForUnattendedCopilotLaunchAsync(run, ct, platformScoped: true)
                .ConfigureAwait(false))
            throw new ModelProviderConnectionRequiredException();
    }

    public Task<OperatorAssistantResponse> SendMessageAsync(
        CallerContext caller,
        string callerBearerToken,
        string runId,
        string message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(message))
            throw new AssistantRunHttpException(StatusCodes.Status400BadRequest, "message_required", "message is required.");

        return RunTurnAsync(caller, callerBearerToken, runId, message, contextRunId: null, ct);
    }

    public async Task<IReadOnlyList<AssistantRunSummary>> ListRunsAsync(
        CallerContext caller,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var cappedLimit = Math.Clamp(limit, 1, 200);

        // Caller-scoped: the store query filters on submitting_user and the Operator sentinel agent,
        // so only this caller's own operator conversations are returned (never another user's runs).
        var runs = await _runStore
            .GetRunsBySubmittingUserAsync(caller.User, OperatorAgentName, cappedLimit, ct)
            .ConfigureAwait(false);

        return runs
            .Where(r => caller.Owns(r.SubmittingUser))
            .Select(r => new AssistantRunSummary(
                r.Id.ToString(),
                r.Status,
                BuildTitle(r.Task),
                r.StartedAt))
            .ToList();
    }

    /// <summary>Derives a short single-line conversation title from the run's opening task text.</summary>
    private static string BuildTitle(string? task)
    {
        var trimmed = task?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "Operator assistant conversation";

        var firstLine = trimmed.Split('\n', 2)[0].Trim();
        const int maxLength = 80;
        return firstLine.Length <= maxLength ? firstLine : firstLine[..maxLength].TrimEnd() + "\u2026";
    }

    private async Task<OperatorAssistantResponse> RunTurnAsync(
        CallerContext caller,
        string callerBearerToken,
        string runId,
        string message,
        string? contextRunId,
        CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var state))
            state = await RehydrateRunAsync(caller, runId, ct).ConfigureAwait(false);

        if (!caller.Owns(state.User))
            throw new AssistantRunHttpException(StatusCodes.Status403Forbidden, "forbidden",
                "You do not own this operator run.");

        // Serialize turns within a single conversation so history and streamed events stay ordered.
        await state.Turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.Touch();

            // Re-resolve the effective provider for THIS turn (a platform provider switch must take
            // effect on the next message, not only on a brand-new session).
            await ReresolveModelSourceForTurnAsync(runId, ct).ConfigureAwait(false);

            var userMessageId = Guid.NewGuid().ToString("N");
            await AppendAsync(runId, EventTypes.AgentMessage, new
            {
                messageId = userMessageId,
                role = "user",
                content = message,
            }, ct).ConfigureAwait(false);

            var request = new OperatorAssistantRequest(
                ConversationId: runId,
                Message: message,
                CallerUser: caller.User,
                GitHubLogin: caller.GitHubLogin,
                ProjectId: state.ProjectId,
                RunId: contextRunId,
                ModelId: state.ModelId,
                AgentDefinition: AgentDefinitionTemplate.Content,
                CallerBearerToken: callerBearerToken,
                History: state.HistorySnapshot());

            var assistantMessageId = Guid.NewGuid().ToString("N");
            var sink = new RunEventSink(this, runId, assistantMessageId, ct);

            OperatorAssistantResponse response;
            try
            {
                response = await _assistant.RunTurnAsync(request, sink, ct).ConfigureAwait(false);
            }
            catch (AgentProviderException ex)
            {
                await AppendAsync(runId, EventTypes.RunError, new
                {
                    error = ex.ErrorCode,
                    message = ex.UserMessage,
                    kind = ex.FailureKind.ToString(),
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await AppendAsync(runId, EventTypes.AgentMessage, new
            {
                messageId = assistantMessageId,
                role = "assistant",
                content = response.Message,
                toolsInvoked = response.ToolNamesInvoked,
            }, ct).ConfigureAwait(false);

            state.Append("user", message);
            state.Append("assistant", response.Message);
            state.Touch();

            return response;
        }
        finally
        {
            state.Turn.Release();
        }
    }

    /// <summary>
    /// Rebuilds an <see cref="OperatorRunState"/> from durable storage when a run is missing from the
    /// in-memory <see cref="_runs"/> cache — the entry may have been evicted by the idle sweeper, lost
    /// to a pod restart, or simply live on the OTHER replica (no session affinity). Preserves the
    /// existing 404/403 semantics for a genuinely-missing run, a non-operator run, or a run the caller
    /// doesn't own; only a legitimate cache-miss-but-durable-hit is rehydrated.
    /// </summary>
    private async Task<OperatorRunState> RehydrateRunAsync(CallerContext caller, string runId, CancellationToken ct)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
            throw new AssistantRunHttpException(StatusCodes.Status404NotFound, "run_not_found",
                "No active operator run with that id. It may have been closed after an idle timeout.");

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
        if (run is null || !string.Equals(run.AgentName, OperatorAgentName, StringComparison.Ordinal))
            throw new AssistantRunHttpException(StatusCodes.Status404NotFound, "run_not_found",
                "No active operator run with that id. It may have been closed after an idle timeout.");

        if (!caller.Owns(run.SubmittingUser))
            throw new AssistantRunHttpException(StatusCodes.Status403Forbidden, "forbidden",
                "You do not own this operator run.");

        var events = await _eventStream.GetPersistedEventsAsync(runId, fromSequence: 0, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        // "Genuinely unresumable" means a REAL terminal seal, NOT ordinary idle-timeout. A run is
        // sealed only when its durable event stream carries an actual terminal `run.completed` marker
        // (reserved for a true end-of-conversation) — or its DB status is the legacy-terminal
        // RunStatus.Completed (defensive guard against any pre-dormancy zombie that a prior buggy
        // revival may have left behind). Reviving such a run — which the very first buggy version did
        // unconditionally — created a permanent "zombie": the DB row read `in_progress` (so the
        // Sessions UI showed the conversation as live) while the stream was already sealed at the
        // terminal marker, so it could never actually resume.
        //
        // Idle-timeout no longer seals: CloseIdleRunAsync now PARKS the conversation as the
        // non-terminal RunStatus.Idle with a NON-terminal `run.idle` marker and leaves the stream
        // open. An Idle run therefore is NOT sealed and falls through to the normal rebuild path
        // below, then is woken back to InProgress — continuing as the SAME run with history intact.
        var isSealed = run.Status == RunStatus.Completed
            || events.Any(e => string.Equals(e.Type, EventTypes.RunCompleted, StringComparison.Ordinal));
        if (isSealed)
            throw new AssistantRunHttpException(StatusCodes.Status409Conflict, "operator_run_closed",
                "This operator conversation was closed and cannot be resumed. " +
                "Start a new conversation to continue.");

        // Wake a dormant (Idle) conversation. Idle is a non-terminal pause, so we rebuild history from
        // the durable events exactly as for a cache-miss and CAS the DB status Idle -> InProgress. The
        // CAS is single-winner across replicas (mirrors CloseIdleRunAsync's park CAS): if another
        // replica already woke it, our CAS returns false and we simply continue — the run is
        // InProgress either way, so the conversation resumes exactly once no matter which replica
        // handles the waking message.
        if (run.Status == RunStatus.Idle)
        {
            var woke = await _runStore.TryWakeFromIdleAsync(parsedRunId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                woke
                    ? "Woke dormant operator run {RunId} (idle -> in_progress); resuming the same conversation."
                    : "Dormant operator run {RunId} was already woken by another replica; resuming the same conversation.",
                runId);
        }

        var history = BuildHistoryFromEvents(events);

        // Concurrency accounting: rehydration does NOT count against MaxConcurrentRunsPerUser. The
        // limit exists to bound how many conversations a user can have OPEN AT ONCE (enforced in
        // StartRunAsync when a brand-new run is created); it is not meant to make an existing
        // conversation permanently unresumable just because the user has since started other
        // conversations that now occupy their quota. Applying the same check here would let a run
        // rehydrating from a cache-miss be blocked by its own resumption, and (unlike StartRunAsync)
        // there is no "start a different one instead" escape hatch — the caller is asking for THIS
        // conversation specifically. We still use the same _startLock used by StartRunAsync so a
        // concurrent StartRunAsync's active-count snapshot and this insert cannot race each other.
        lock (_startLock)
        {
            // Another turn/rehydration may have already inserted this run while we were doing the
            // (async) durable reads above — if so, reuse that instance rather than clobbering its
            // live state (in-flight Turn semaphore, freshly-appended history) with a stale rebuild.
            if (_runs.TryGetValue(runId, out var existing))
                return existing;

            var rebuilt = new OperatorRunState(
                run.SubmittingUser,
                run.ProjectId?.ToString(),
                run.ModelId,
                now,
                seedHistory: history);
            _runs[runId] = rebuilt;

            _logger.LogInformation(
                "Rehydrated operator run {RunId} from durable storage ({HistoryCount} history messages restored).",
                runId, history.Count);

            return rebuilt;
        }
    }

    /// <summary>Rebuilds the bounded <see cref="ConsoleFacadeHistoryMessage"/> history the assistant
    /// replays each turn from persisted <see cref="EventTypes.AgentMessage"/> events, taking at most
    /// the most recent <see cref="MaxHistoryMessages"/> in chronological order (events are already
    /// persisted/read in ascending sequence order).</summary>
    private static IReadOnlyList<ConsoleFacadeHistoryMessage> BuildHistoryFromEvents(IReadOnlyList<RunEvent> events)
    {
        var messages = new List<ConsoleFacadeHistoryMessage>();
        foreach (var evt in events)
        {
            if (evt.Type != EventTypes.AgentMessage)
                continue;
            if (evt.Payload is not JsonElement payload)
                continue;
            if (!payload.TryGetProperty("role", out var roleProp) || !payload.TryGetProperty("content", out var contentProp))
                continue;

            var role = roleProp.GetString();
            var content = contentProp.GetString();
            if (string.IsNullOrEmpty(role) || content is null)
                continue;

            messages.Add(new ConsoleFacadeHistoryMessage(role, content));
        }

        if (messages.Count > MaxHistoryMessages)
            messages.RemoveRange(0, messages.Count - MaxHistoryMessages);

        return messages;
    }

    /// <summary>Closes runs that have been idle beyond the configured timeout. Exposed for tests so
    /// the sweep can be driven deterministically without waiting on the timer.</summary>
    internal void SweepIdleRuns(DateTimeOffset now)
    {
        foreach (var (key, state) in _runs.ToArray())
        {
            if (now - state.LastActivityUtc < _options.IdleTimeout)
                continue;

            // Never idle-close a run that is actively blocked on a human tool-approval decision. A
            // conversation parked on an approval card is NOT abandoned — it is waiting on the
            // accountable operator (who may have simply stepped away), exactly like the coordinator's
            // AssemblyReviewGate indefinite-safe wait. Closing here would call _approvalGate.Clear(key)
            // and seal the stream, permanently destroying the in-flight approval so the run could
            // never resume — a run must not die from human-response wait time. Leave it resident and
            // resumable; once the operator responds (or the underlying approval times out on its own),
            // normal activity resumes and the idle clock restarts from the next real activity.
            if (_approvalGate.HasArmedApproval(key))
            {
                _logger.LogInformation(
                    "Skipping idle-close for operator run {RunId}: an armed tool-approval is awaiting the operator.",
                    key);
                continue;
            }

            if (!_runs.TryRemove(key, out _))
                continue;

            _ = CloseIdleRunAsync(key);
        }
    }

    private async Task CloseIdleRunAsync(string key)
    {
        try
        {
            if (!RunId.TryParse(key, out var runId))
                return;

            // Multi-replica-safe DORMANCY (not termination). The API runs multiple replicas (k8s
            // api-deployment replicas: 2), each with its OWN in-memory `_runs` map, its OWN idle-sweep
            // timer, and NO session affinity — so both replicas can independently rehydrate the same
            // run and then each, on its own schedule, decide it is idle. Gate the whole park on a
            // compare-and-set InProgress -> Idle transition so only ONE replica wins: the loser gets
            // `false` and does nothing (no duplicate run.idle marker).
            //
            // Crucially this PARKS the conversation, it does NOT end it. A standing product directive
            // is that no Assistant/Operator run may die from human-response wait time — an idle
            // conversation must be able to un-sleep and continue as the SAME run. So unlike a genuine
            // terminal close we (a) CAS to the non-terminal RunStatus.Idle (not Completed) leaving
            // ended_at NULL, (b) append a NON-terminal run.idle marker (not run.completed), and
            // (c) deliberately do NOT call _eventStream.CompleteAsync — sealing the SSE stream is
            // reserved for a genuine end-of-conversation, which does not currently exist for Operator
            // runs. The next message rehydrates and wakes the run (RehydrateRunAsync: Idle ->
            // InProgress) and the conversation resumes transparently.
            var parked = await _runStore
                .TryTransitionToIdleAsync(runId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!parked)
            {
                _logger.LogInformation(
                    "Idle operator run {RunId} was not InProgress (already parked or resumed by another replica); skipping duplicate park.",
                    key);
                return;
            }

            await AppendAsync(key, EventTypes.RunIdle, new
            {
                runId = key,
                reason = "idle_timeout",
            }, CancellationToken.None).ConfigureAwait(false);

            // NOTE: no _eventStream.CompleteAsync (would seal the stream terminally) and no
            // _approvalGate.Clear (SweepIdleRuns already skips parking while an approval is armed, and
            // dormancy preserves all conversational state for the wake).
            _logger.LogInformation(
                "Parked idle operator run {RunId} to dormant (idle_timeout); resumable on the next message.", key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to park idle operator run {RunId}", key);
        }
    }

    private void SweepIdleRunsSafe()
    {
        try { SweepIdleRuns(DateTimeOffset.UtcNow); }
        catch (Exception ex) { _logger.LogWarning(ex, "Operator run idle sweep failed."); }
    }

    private async ValueTask AppendAsync(string runId, string type, object payload, CancellationToken ct) =>
        _ = await _eventStream.AppendAsync(runId, new RunEvent(0, type, payload), ct).ConfigureAwait(false);

    public void Dispose() => _idleSweeper.Dispose();

    /// <summary>Per-conversation in-memory state: owner, context, activity timestamp, bounded history,
    /// and a per-run gate that serializes turns.</summary>
    private sealed class OperatorRunState(
        string user,
        string? projectId,
        string? modelId,
        DateTimeOffset startedAt,
        IReadOnlyList<ConsoleFacadeHistoryMessage>? seedHistory = null)
    {
        // Rehydration (durable cache-miss recovery) seeds this with history rebuilt from persisted
        // AgentMessage events; a normal fresh start leaves it empty.
        private readonly List<ConsoleFacadeHistoryMessage> _history =
            seedHistory is { Count: > 0 } ? new List<ConsoleFacadeHistoryMessage>(seedHistory) : [];
        private readonly Lock _lock = new();

        public string User { get; } = user;
        public string? ProjectId { get; } = projectId;
        public string? ModelId { get; } = modelId;
        public SemaphoreSlim Turn { get; } = new(1, 1);
        public DateTimeOffset LastActivityUtc { get; private set; } = startedAt;

        public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

        public void Append(string role, string text)
        {
            lock (_lock)
            {
                _history.Add(new ConsoleFacadeHistoryMessage(role, text));
                if (_history.Count > MaxHistoryMessages)
                    _history.RemoveRange(0, _history.Count - MaxHistoryMessages);
            }
        }

        public IReadOnlyList<ConsoleFacadeHistoryMessage> HistorySnapshot()
        {
            lock (_lock)
                return _history.ToList();
        }
    }

    /// <summary>Projects each streamed assistant/tool step onto the run event stream in order. Text
    /// deltas are not persisted individually (the full assistant message is appended once the turn
    /// completes); tool calls/results are appended as discrete durable events.</summary>
    private sealed class RunEventSink(AssistantRunService owner, string runId, string assistantMessageId, CancellationToken ct)
        : IOperatorAssistantTurnSink
    {
        public ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken _) => ValueTask.CompletedTask;

        public ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken _) =>
            owner.AppendAsync(runId, EventTypes.ToolCall, new
            {
                messageId = assistantMessageId,
                name = toolName,
                arguments = argumentsJson,
            }, ct);

        public ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken _) =>
            owner.AppendAsync(runId, success ? EventTypes.ToolResult : EventTypes.ToolError, new
            {
                messageId = assistantMessageId,
                name = toolName,
                success,
            }, ct);

        public async ValueTask<bool> OnApprovalRequiredAsync(
            string requestId, string toolName, string? argumentsJson, CancellationToken _)
        {
            var displayId = requestId.Length >= 8 ? requestId[..8] : requestId;

            // Start the gate wait FIRST: the gate registers the approval context synchronously (before
            // its first await), so the tool-approvals endpoint can already resolve this requestId by
            // the time the frontend reacts to the tool.approval_required event emitted just below.
            var waitTask = owner._approvalGate.WaitForApprovalAsync(
                runId, requestId, toolName, url: null, ApprovalTimeout, ct);

            await owner.AppendAsync(runId, EventTypes.ToolApprovalRequired, new
            {
                requestId,
                displayId,
                toolName,
                arguments = argumentsJson,
                message = $"The assistant wants to run {toolName}. Operator approval required.",
            }, ct).ConfigureAwait(false);

            // Heartbeat-punctuated wait so the run's SSE stream keeps moving (and the parent stall
            // timers stay reset) while the operator decides.
            while (!waitTask.IsCompleted)
            {
                var heartbeat = Task.Delay(ApprovalHeartbeatInterval, ct);
                var completed = await Task.WhenAny(waitTask, heartbeat).ConfigureAwait(false);
                if (completed == waitTask)
                    break;
                await owner.AppendAsync(runId, EventTypes.ToolApprovalPending, new
                {
                    requestId,
                    displayId,
                    toolName,
                }, ct).ConfigureAwait(false);
            }

            var approved = await waitTask.ConfigureAwait(false);

            // Emit the resolution on the operator run's own stream so the frontend drops the pending
            // approval card even on a reload (derivePendingApprovals matches this by requestId).
            await owner.AppendAsync(runId, EventTypes.ToolApprovalResolved, new
            {
                requestId,
                runId,
                approved,
            }, ct).ConfigureAwait(false);

            return approved;
        }
    }
}
