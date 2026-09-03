using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Sandbox;
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
    /// <summary>Maximum number of GENUINELY ACTIVE operator conversations a single user may hold at
    /// once — counted from durable run status (<see cref="RunStatus.InProgress"/> operator runs), not
    /// from any one API replica's in-memory cache. A new start is rejected with 429 once the user is
    /// at this bound; merely rehydrating, reading, or listing an existing conversation never consumes
    /// a slot.</summary>
    public int MaxConcurrentRunsPerUser { get; set; } = 5;

    /// <summary>How long an operator run may sit without a new message before it is auto-closed
    /// (transitioned to Completed and its stream completed), releasing the concurrency slot.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a conversation's AgentHost pod is HELD after its last turn before being released.
    /// Deliberately much shorter than <see cref="IdleTimeout"/>: holding the pod is what removes the
    /// 15-20s per-turn cold start (claim bind + one-shot <c>/configure</c> + Copilot client startup),
    /// but each AgentHost pod reserves real cluster capacity, so a conversation the human has walked
    /// away from must give its pod back long before the conversation itself goes dormant. The next
    /// message simply pays one cold start again.
    /// </summary>
    public TimeSpan PodIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the idle sweeper runs.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a durable <c>InProgress</c> assistant run may go without a single persisted event
    /// before the concurrency check stops counting it against its owner and parks it.
    ///
    /// <para>
    /// Only the owning API pod's in-memory sweep ever parks a conversation, so a pod that restarts
    /// before it gets there strands the durable row as <c>InProgress</c> forever — nothing else
    /// transitions it, and the AgentHost reaper reclaims the pod without touching run status. Each
    /// such restart permanently burns one of that user's slots. Comfortably longer than
    /// <see cref="IdleTimeout"/> so a genuinely live-but-quiet conversation is never mistaken for a
    /// stranded one: by the time this elapses a healthy owner would already have parked it.
    /// </para>
    /// </summary>
    public TimeSpan StaleActiveRunThreshold { get; set; } = TimeSpan.FromMinutes(90);

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
        string? firstMessage,
        string? projectId,
        string? contextRunId,
        string? modelId,
        CancellationToken ct,
        string? resumeFromRunId = null);

    /// <summary>Runs the next conversational turn on an existing operator run owned by the caller.</summary>
    Task<OperatorAssistantResponse> SendMessageAsync(
        CallerContext caller,
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
/// It wires <see cref="IOperatorAssistantAgent"/> to run one turn at a time using renewable,
/// five-minute MCP broker tokens bound to the authenticated caller. No browser credential or broker
/// token is cached or shared across users. A durable, replica-independent
/// per-user concurrency bound (derived from run status, so the API's replicas agree) and an idle
/// sweep keep the number of live Copilot/MCP sessions and held AgentHost pods bounded.
///
/// This is additive: it does not touch the existing <c>/api/console/turn</c> facade path.
/// </summary>
public sealed class AssistantRunService : IAssistantRunService, IDisposable
{
    /// <summary>Sentinel AgentName that marks a run as an operator chat (mirrors "Coordinator").</summary>
    public const string OperatorAgentName = "Operator";

    private const int MaxHistoryMessages = 24;

    /// <summary>How many of the caller's newest operator runs are read to evaluate the concurrency
    /// bound. Generous enough that every genuinely-active conversation is seen in practice; because
    /// the query is newest-first, a pathological overflow can only UNDER-count, which fails open (a
    /// start is allowed) rather than falsely rejecting a legitimate one.</summary>
    private const int ConcurrencyScanLimit = 50;

    /// <summary>How many of those newest runs the duplicate-start guard considers (unchanged from
    /// when it issued its own query).</summary>
    private const int DuplicateScanLimit = 5;

    private readonly IRunStore _runStore;
    private readonly IRunEventStream _eventStream;
    private readonly IOperatorAssistantAgent _assistant;
    private readonly IToolApprovalGate _approvalGate;
    private readonly AssistantRunOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOperatorAssistantBrokerTokenIssuer _brokerTokenIssuer;
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

    /// <summary>Per-user count of starts that have passed the concurrency bound but whose durable run
    /// row does not exist yet. Guarded by <see cref="_startLock"/>.</summary>
    private readonly Dictionary<string, int> _pendingStarts = new(StringComparer.Ordinal);
    private readonly object _startLock = new();
    private readonly Timer _idleSweeper;

    public AssistantRunService(
        IRunStore runStore,
        IRunEventStream eventStream,
        IOperatorAssistantAgent assistant,
        IToolApprovalGate approvalGate,
        IOptions<AssistantRunOptions> options,
        IServiceScopeFactory scopeFactory,
        IOperatorAssistantBrokerTokenIssuer brokerTokenIssuer,
        IConfiguration configuration,
        ILogger<AssistantRunService> logger)
    {
        _runStore = runStore;
        _eventStream = eventStream;
        _assistant = assistant;
        _approvalGate = approvalGate;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _brokerTokenIssuer = brokerTokenIssuer;
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
        //
        // This one query also feeds the concurrency bound below, so the caller's conversations are
        // read from durable storage exactly once per start.
        var recentRuns = await _runStore
            .GetRunsBySubmittingUserAsync(caller.User, OperatorAgentName, ConcurrencyScanLimit, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(firstMessage))
        {
            var duplicate = recentRuns.Take(DuplicateScanLimit).FirstOrDefault(r =>
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

        // Concurrency bound, counted from DURABLE run status rather than this replica's _runs map.
        //
        // The map conflates "resident in this process" with "actively running": RehydrateRunAsync
        // inserts into it too, so merely opening or replying to an existing conversation used to
        // occupy a slot for the next IdleTimeout — and with two API replicas and no session affinity
        // the SAME conversation could occupy a slot on BOTH, so the two processes disagreed on the
        // count and a user with a handful of open conversations was falsely told they had "too many
        // active assistant conversations". Counting InProgress runs in the shared store fixes both:
        // a dormant (Idle) or finished conversation frees its slot the moment it is parked, and one
        // conversation is one row no matter how many replicas have it resident.
        var durableActive = recentRuns.Count(r => r.Status == RunStatus.InProgress);

        // ... but a durable InProgress row only frees its slot when SOMEONE parks it, and the only
        // thing that parks an assistant conversation is the owning API pod's idle sweep. If that pod
        // restarts (deploy, OOM, node drain) before it gets there, the row stays InProgress forever:
        // nothing durable ever transitions it, and AgentHostReaperService reclaims the abandoned pod
        // and claim without touching run status. Those rows are indistinguishable from live
        // conversations here, so each restart permanently burns one of the user's slots until they
        // are all gone and every new conversation is refused.
        //
        // So before refusing, re-examine the counted rows against the one replica-independent
        // activity signal there is (the run's last DURABLE event) and discount any that have been
        // silent past the staleness threshold, CAS-parking them so the repair is shared cluster-wide
        // instead of re-derived on every start. Done only on the about-to-refuse path: the happy
        // path stays exactly as cheap as before, and no new background job is needed.
        if (durableActive >= _options.MaxConcurrentRunsPerUser)
        {
            durableActive = await DiscountStaleActiveRunsAsync(
                recentRuns.Where(r => r.Status == RunStatus.InProgress), now, ct).ConfigureAwait(false);
        }

        // In-flight starts have no durable row yet (InsertAsync happens below), so the store count
        // alone would let concurrent starts on THIS replica all slip past the bound. Reserve under
        // the same lock the original code used so that race stays closed within a process; across
        // replicas the bound stays advisory, as any store-read-then-write check must be.
        lock (_startLock)
        {
            _pendingStarts.TryGetValue(caller.User, out var pending);
            if (durableActive + pending >= _options.MaxConcurrentRunsPerUser)
                throw new AssistantConcurrencyLimitException(_options.MaxConcurrentRunsPerUser);

            _pendingStarts[caller.User] = pending + 1;
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
        finally
        {
            // The durable row now exists (or the start failed and freed its own slot), so the
            // reservation must not keep counting against the user.
            ReleasePendingStart(caller.User);
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
            firstTurn = await RunTurnAsync(caller, key, firstMessage!, contextRunId, ct)
                .ConfigureAwait(false);

        return new StartAssistantRunResult(runId, RunStatus.InProgress, firstTurn);
    }

    /// <summary>Drops a start reservation taken under <see cref="_startLock"/>.</summary>
    private void ReleasePendingStart(string user)
    {
        lock (_startLock)
        {
            if (!_pendingStarts.TryGetValue(user, out var pending))
                return;
            if (pending <= 1)
                _pendingStarts.Remove(user);
            else
                _pendingStarts[user] = pending - 1;
        }
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
    private async Task<ModelSource> ResolveAssistantModelSourceAsync(CancellationToken ct) =>
        ToModelSource(await ResolveAssistantProviderAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Returns how many of <paramref name="activeRuns"/> should still count against the caller's
    /// concurrency bound, excluding any that have been durably silent past
    /// <see cref="AssistantRunOptions.StaleActiveRunThreshold"/> and CAS-parking those so the repair
    /// is durable and shared by every replica rather than re-derived on each start.
    /// </summary>
    /// <remarks>
    /// A run whose last activity cannot be determined (no persisted events yet, or an event store
    /// that cannot answer) is counted, so an unanswerable question can never reclaim a live
    /// conversation's slot; the failure mode stays "refuses a start it could have allowed", never
    /// "kills a conversation that was running".
    /// </remarks>
    private async Task<int> DiscountStaleActiveRunsAsync(
        IEnumerable<Run> activeRuns, DateTimeOffset now, CancellationToken ct)
    {
        var counted = 0;
        foreach (var run in activeRuns)
        {
            var runId = run.Id.ToString();
            DateTimeOffset? lastActivity;
            try
            {
                lastActivity = await _eventStream.GetLastEventTimestampAsync(runId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "Could not read the durable last-activity timestamp for operator run {RunId}; counting it as active.",
                    runId);
                counted++;
                continue;
            }

            // Fall back to StartedAt: a run that never managed to persist an event is still bounded
            // by when it began, so a row stranded before its first event self-heals too.
            var reference = lastActivity ?? run.StartedAt;
            if (now - reference < _options.StaleActiveRunThreshold)
            {
                counted++;
                continue;
            }

            // Never reclaim a conversation that is resident and live on THIS replica; its in-memory
            // activity clock is authoritative and beats any inference from the event log.
            if (_runs.ContainsKey(runId))
            {
                counted++;
                continue;
            }

            try
            {
                var parked = await _runStore.TryTransitionToIdleAsync(run.Id, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Operator run {RunId} has been durably silent for {QuietMinutes:F0} minutes; " +
                    "parking it as Idle so its concurrency slot is not stranded (parked: {Parked}). " +
                    "The conversation stays resumable.",
                    runId, (now - reference).TotalMinutes, parked);

                // A lost CAS means another replica changed the row concurrently; be conservative and
                // keep counting it this time round.
                if (!parked)
                    counted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Failed to park durably-stale operator run {RunId}; counting it as active.", runId);
                counted++;
            }
        }

        return counted;
    }

    /// <summary>Resolves the full effective provider (kind AND binding/configuration identity) for an
    /// Assistant session at platform scope. See <see cref="ResolveAssistantModelSourceAsync"/> for why
    /// the scope is deliberately <c>projectId: null</c>.</summary>
    private async Task<EffectiveModelProviderResult> ResolveAssistantProviderAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var modelProviderResolver = scope.ServiceProvider.GetRequiredService<EffectiveModelProviderResolver>();
        return await modelProviderResolver.ResolveAsync(projectId: null, ct).ConfigureAwait(false);
    }

    private static ModelSource ToModelSource(EffectiveModelProviderResult provider) =>
        provider is EffectiveModelProviderResult.Byok ? ModelSource.Byok : ModelSource.GitHubCopilot;

    /// <summary>
    /// Re-resolves the effective model provider at the START OF EVERY TURN, repoints the persisted
    /// run at it when the coarse <see cref="ModelSource"/> changed, and — critically for a
    /// conversation that HOLDS its AgentHost pod between turns — gives that pod back whenever the
    /// provider IDENTITY changed, so the next turn rebuilds it against the provider that is actually
    /// in effect now.
    ///
    /// <para>
    /// Without the re-resolution the provider was pinned at session-creation time forever:
    /// <c>ModelSource</c> was computed once in <see cref="StartRunAsync"/>, and neither
    /// <see cref="SendMessageAsync"/> nor <see cref="RehydrateRunAsync"/> (which rebuilds state from
    /// the stored row) ever revisited it — so switching the deployment to BYOK mid-conversation had
    /// no visible effect until the user started a brand-new session.
    /// </para>
    ///
    /// <para>
    /// Two things make comparing only the two-value <c>ModelSource</c> insufficient here.
    /// </para>
    ///
    /// <para>
    /// First, <c>ModelSource</c> cannot see a same-kind provider change at all: switching the active
    /// BYOK provider from one configuration to another, or rebinding the platform GitHub Copilot
    /// connection to a different account, leaves it identical, so the run kept being served by the
    /// stale provider. The comparison is therefore made on
    /// <see cref="EffectiveModelProviderResult.ProviderIdentity"/> (provider kind + binding /
    /// configuration id), recorded per conversation on the previous turn.
    /// </para>
    ///
    /// <para>
    /// Second, a held pod resolves its provider EXACTLY ONCE. <c>CopilotAIAgent.SetupAsync</c> — which
    /// decides BYOK vs Copilot and builds the SDK client — runs only at the pod's one-shot
    /// <c>/configure</c>, and the per-turn refresh (<c>CopilotAIAgent.ApplyPerTurnContext</c>) rebuilds
    /// only the tool set and system message, never the client. So once the pod is held across turns,
    /// repointing the DB row alone changes nothing about which provider actually serves the AI calls.
    /// Releasing the held pod here is what makes the switch real: the next turn re-launches, and
    /// <c>KubernetesSandboxExecutor</c> resolves the provider and configures the fresh pod from
    /// scratch. The cost is exactly one cold start on the turn after an admin changed the provider.
    /// </para>
    ///
    /// <para>
    /// A change is otherwise applied transparently: the conversation keeps its history (which is
    /// replayed as plain text on every turn and is not provider-specific), and the persisted
    /// <c>ModelSource</c> is what the downstream per-turn credential fence reads — including
    /// <c>RemoteOperatorAssistantAgent.EnsureAgentHostCapabilityAsync</c>, which loads the run row
    /// fresh each turn — so selection and validation stay in agreement after the switch too. When the
    /// new provider is GitHub Copilot the capability gate is re-run immediately so an unusable
    /// platform connection fails fast with the "Connect GitHub" CTA rather than deep inside the turn.
    /// </para>
    /// </summary>
    private async Task ReresolveModelSourceForTurnAsync(OperatorRunState state, string runId, CancellationToken ct)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
            return;

        var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
        if (run is null)
            return;

        var provider = await ResolveAssistantProviderAsync(ct).ConfigureAwait(false);
        var modelSource = ToModelSource(provider);

        // Null on the conversation's very first turn on this replica (a fresh start, or a rehydration
        // that has no pod and no cached provider state yet) — there is nothing to compare against and
        // nothing provider-bound to invalidate, so the identity is simply recorded.
        var previousIdentity = state.ExchangeModelProviderIdentity(provider.ProviderIdentity);
        var identityChanged = previousIdentity is not null &&
            !string.Equals(previousIdentity, provider.ProviderIdentity, StringComparison.Ordinal);
        var modelSourceChanged = modelSource != run.ModelSource;

        if (!identityChanged && !modelSourceChanged)
            return;

        _logger.LogInformation(
            "Operator run {RunId}: effective model provider changed ({PreviousSource}/{PreviousIdentity} -> " +
            "{CurrentSource}/{CurrentIdentity}) since the last turn; the new provider takes effect for this turn.",
            runId,
            run.ModelSource.ToApiString(),
            previousIdentity ?? "(none)",
            modelSource.ToApiString(),
            provider.ProviderIdentity);

        if (modelSourceChanged)
        {
            var repointed = run with { ModelSource = modelSource };
            await PrepareAgentHostCapabilityAsync(repointed, ct).ConfigureAwait(false);
            await _runStore.UpdateModelSourceAsync(parsedRunId, modelSource, ct).ConfigureAwait(false);
        }

        // Give the held, provider-bound pod back so this turn cold-starts one configured for the
        // provider now in effect. The CAS keeps this to exactly one release even if the pod-idle
        // sweeper fires concurrently.
        if (identityChanged && state.TryMarkAgentHostPodReleasing())
            await ReleaseAgentHostPodAsync(state, runId, "model_provider_changed").ConfigureAwait(false);
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
        string runId,
        string message,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (string.IsNullOrWhiteSpace(message))
            throw new AssistantRunHttpException(StatusCodes.Status400BadRequest, "message_required", "message is required.");

        return RunTurnAsync(caller, runId, message, contextRunId: null, ct);
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

            // Re-resolve the effective provider for this turn so a platform provider switch takes
            // effect on the next message, releasing any held pod configured for the prior provider.
            await ReresolveModelSourceForTurnAsync(state, runId, ct).ConfigureAwait(false);

            Task<string> IssueBrokerTokenAsync(CancellationToken token) =>
                _brokerTokenIssuer.IssueAsync(caller, runId, state.ProjectId, token);

            var brokerToken = await IssueBrokerTokenAsync(ct).ConfigureAwait(false);

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
                McpBrokerToken: brokerToken,
                History: state.HistorySnapshot(),
                RenewMcpBrokerTokenAsync: IssueBrokerTokenAsync,
                // Fencing token for this conversation's AgentHost claim (see
                // OperatorRunState.AgentHostPodHolderToken): stamped on the claim when this replica
                // creates it, and checked before any later release from this replica deletes it.
                PodHolderToken: state.AgentHostPodHolderToken);

            var assistantMessageId = Guid.NewGuid().ToString("N");
            var sink = new RunEventSink(this, runId, assistantMessageId, ct);

            OperatorAssistantResponse response;
            try
            {
                // In agent-host mode the turn claims (or re-binds) this conversation's AgentHost pod
                // and — unlike the original per-turn claim/release — leaves it HELD for the next
                // message. Record that from the moment the turn starts, not once it succeeds, so a
                // turn that dies without unwinding still leaves a pod this service knows to release.
                if (_agentHostEnabled)
                    state.MarkAgentHostPodHeld();

                response = await _assistant.RunTurnAsync(request, sink, ct).ConfigureAwait(false);
            }
            catch (AgentProviderException ex)
            {
                // A failed turn releases its AgentHost pod (RemoteOperatorAssistantAgent does it on
                // its failure paths), so this conversation no longer holds one.
                state.MarkAgentHostPodReleased();
                await AppendAsync(runId, EventTypes.RunError, new
                {
                    error = ex.ErrorCode,
                    message = ex.UserMessage,
                    kind = ex.FailureKind.ToString(),
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                // A cancelled turn ALSO releases the real pod: RemoteOperatorAssistantAgent gives it
                // back on its OperationCanceledException path too (rethrowing the cancellation as-is
                // rather than wrapping it, so it never reaches the handler above). Without mirroring
                // that here the local flag stayed stuck at "held" for a pod that no longer exists,
                // and the pod-idle/dormancy sweeps later issued a redundant release for it.
                state.MarkAgentHostPodReleased();
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

        // Concurrency accounting: rehydration does NOT count against MaxConcurrentRunsPerUser, and
        // since the bound is now derived from DURABLE run status (StartRunAsync counts the caller's
        // InProgress operator runs in the store) rather than from this dictionary, inserting here
        // cannot consume a slot at all — being resident in one replica's cache is no longer mistaken
        // for being actively running. The limit bounds how many conversations a user may have ACTIVE
        // at once (enforced in StartRunAsync when a brand-new run is created); it is not meant to
        // make an existing conversation unresumable just because the user has since started others.
        // Applying the same check here would let a run rehydrating from a cache-miss be blocked by
        // its own resumption, and (unlike StartRunAsync) there is no "start a different one instead"
        // escape hatch — the caller is asking for THIS conversation specifically. We still use the
        // same _startLock used by StartRunAsync so a concurrent StartRunAsync's reservation and this
        // insert cannot race each other.
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

    /// <summary>Closes runs that have been idle beyond the configured timeout, and releases the
    /// AgentHost pod of any conversation that has been quiet beyond the (much shorter)
    /// <see cref="AssistantRunOptions.PodIdleTimeout"/>. Exposed for tests so the sweep can be driven
    /// deterministically without waiting on the timer.</summary>
    internal void SweepIdleRuns(DateTimeOffset now)
    {
        foreach (var (key, state) in _runs.ToArray())
        {
            var quietFor = now - state.LastActivityUtc;

            // Pod-idle release (independent of, and much earlier than, conversation dormancy): a
            // conversation HOLDS its AgentHost pod between turns so the next message skips the
            // ~15-20s claim/configure cold start, but a human who walked away must not keep that
            // cluster capacity reserved for the full IdleTimeout. The conversation itself stays
            // fully alive and resumable — the next message just pays one cold start again.
            if (state.AgentHostPodHeld && quietFor >= _options.PodIdleTimeout)
            {
                if (_approvalGate.HasArmedApproval(key))
                {
                    // An armed approval means the pod is mid-turn, blocked on the operator's own
                    // decision — releasing it would destroy the in-flight tool call.
                    _logger.LogDebug(
                        "Skipping AgentHost pod release for operator run {RunId}: an armed tool-approval is awaiting the operator.",
                        key);
                }
                else if (state.TryMarkAgentHostPodReleasing())
                {
                    _ = ReleaseAgentHostPodAsync(state, key, "pod_idle_timeout");
                }
            }

            if (quietFor < _options.IdleTimeout)
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

            // Parking the conversation as dormant also gives its pod back, if the pod-idle sweep
            // above has not already done so.
            if (state.TryMarkAgentHostPodReleasing())
                _ = ReleaseAgentHostPodAsync(state, key, "conversation_idle_timeout");

            _ = CloseIdleRunAsync(key);
        }
    }

    /// <summary>
    /// Best-effort release of a held AgentHost pod. Resolved through the scope factory rather than
    /// injected because <see cref="IAgentHostPodLifecycle"/> is only registered in-cluster (the same
    /// optional-dependency convention <see cref="RemoteOperatorAssistantAgent"/> uses), so outside
    /// Kubernetes — local dev and the test host — this is a no-op. Never throws: a failed release is
    /// picked up by <c>AgentHostReaperService</c>, which reaps any claim whose run is no longer
    /// active.
    ///
    /// <para>
    /// FENCED. The claim is a shared cluster object with a deterministic, run-derived name, while
    /// everything that decides to release it here (the hold flag, the activity clock, the sweep
    /// timer) is process-local. With two API replicas and no session affinity, a conversation whose
    /// next turn lands on the other replica gets a brand-new pod under that same name there — and
    /// this replica, still believing it holds one, would otherwise delete the OTHER replica's live
    /// claim mid-turn. The release is therefore gated on
    /// <see cref="OperatorRunState.AgentHostPodHolderToken"/> still matching the token stamped on the
    /// claim, so it is a no-op once a newer launch has taken it over.
    /// </para>
    /// </summary>
    private async Task ReleaseAgentHostPodAsync(OperatorRunState state, string runId, string reason)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var lifecycle = scope.ServiceProvider.GetService<IAgentHostPodLifecycle>();
            if (lifecycle is null)
                return;

            var released = await lifecycle
                .TryReleaseHeldAgentHostPodAsync(runId, state.AgentHostPodHolderToken, CancellationToken.None)
                .ConfigureAwait(false);
            if (!released)
            {
                _logger.LogInformation(
                    "Skipped releasing the AgentHost pod for operator run {RunId} ({Reason}): the claim is no " +
                    "longer the one this replica holds — another replica has taken the conversation over.",
                    runId, reason);
                return;
            }

            _logger.LogInformation(
                "Released the held AgentHost pod for operator run {RunId} ({Reason}); the conversation stays resumable.",
                runId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release the held AgentHost pod for operator run {RunId} ({Reason}); the AgentHost reaper will reclaim it.",
                runId, reason);
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

        // 0 = no pod held, 1 = an AgentHost pod is held for this conversation between turns.
        private int _podHeld;

        /// <summary>
        /// Fencing token for this conversation's AgentHost claim, stamped on the claim when THIS
        /// replica creates it and re-checked before this replica deletes it.
        ///
        /// <para>
        /// The hold flag above, <see cref="LastActivityUtc"/>, and the whole pod-idle sweep are
        /// process-local, but the claim they act on is a shared cluster object addressed by a
        /// DETERMINISTIC name derived from the run id. With two API replicas and no session affinity
        /// a conversation's next turn can land on the other replica, which correctly cold-starts its
        /// own pod under that same name — while this replica still believes it holds one. Its idle
        /// sweep would then delete a claim the OTHER replica is actively using, mid-turn. The token
        /// makes the release a compare-and-swap: it proceeds only while the claim on the cluster is
        /// still the one this replica stamped.
        /// </para>
        ///
        /// <para>
        /// Deliberately NOT a full distributed lease. It is a single generation stamp on the object
        /// being deleted, which is all "do not delete someone else's newer claim" requires; the
        /// cross-replica reaper remains the backstop for genuinely orphaned claims.
        /// </para>
        /// </summary>
        public string AgentHostPodHolderToken { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// The <see cref="EffectiveModelProviderResult.ProviderIdentity"/> observed on this
        /// conversation's previous turn, so a turn can tell a genuine provider change from a no-op.
        /// Null until the first turn records one.
        /// </summary>
        private string? _modelProviderIdentity;

        /// <summary>Records the identity observed for this turn and returns the previous one.</summary>
        public string? ExchangeModelProviderIdentity(string identity) =>
            Interlocked.Exchange(ref _modelProviderIdentity, identity);

        /// <summary>Whether this conversation currently holds an AgentHost pod between turns.</summary>
        public bool AgentHostPodHeld => Volatile.Read(ref _podHeld) == 1;

        public void MarkAgentHostPodHeld() => Volatile.Write(ref _podHeld, 1);

        public void MarkAgentHostPodReleased() => Volatile.Write(ref _podHeld, 0);

        /// <summary>Claims the right to release the held pod, returning <c>true</c> for exactly one
        /// caller so overlapping sweeps cannot issue duplicate releases.</summary>
        public bool TryMarkAgentHostPodReleasing() => Interlocked.CompareExchange(ref _podHeld, 0, 1) == 1;

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

        public ValueTask OnRunFailedAsync(AgentProviderException providerFailure, CancellationToken _) =>
            // Assistant conversations remain resumable after a failed turn. Persisting run.failed on
            // the public run stream would mark the whole conversation terminal and stop future SSE
            // replay/tailing; the outer AgentProviderException handler emits the non-terminal run.error.
            ValueTask.CompletedTask;

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

        public ValueTask OnMcpBrokerTokenRefreshRequiredAsync(CancellationToken _) =>
            ValueTask.FromException(new InvalidOperationException(
                "In-process operator MCP token renewal is unavailable; the assistant must run through AgentHost."));
    }
}
