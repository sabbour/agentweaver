using System.Collections.Concurrent;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
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

public interface IAssistantRunService
{
    /// <summary>Creates a new operator run for the caller and, when <paramref name="firstMessage"/> is
    /// supplied, runs the opening turn. Enforces the per-user concurrency bound.</summary>
    Task<StartAssistantRunResult> StartRunAsync(
        CallerContext caller,
        string callerBearerToken,
        string? firstMessage,
        string? projectId,
        string? contextRunId,
        string? modelId,
        CancellationToken ct);

    /// <summary>Runs the next conversational turn on an existing operator run owned by the caller.</summary>
    Task<OperatorAssistantResponse> SendMessageAsync(
        CallerContext caller,
        string callerBearerToken,
        string runId,
        string message,
        CancellationToken ct);
}

/// <summary>
/// Backend service (#346) that models an MCP-driven operator chat as a lightweight "operator run":
/// a persisted <see cref="Run"/> with <c>AgentName == "Operator"</c>, no work plan and no children,
/// whose turns stream onto the existing <see cref="IRunEventStream"/> so the unchanged
/// <c>GET /api/runs/{id}/stream</c> and <c>/events</c> endpoints serve the transcript.
///
/// It wires <see cref="IOperatorAssistantAgent"/> (the in-API Copilot loop sourcing its tools from the
/// AgentweaverMCP server) to run one turn at a time using the caller's GitHub bearer token, threaded
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
    private readonly AssistantRunOptions _options;
    private readonly ILogger<AssistantRunService> _logger;

    private readonly ConcurrentDictionary<string, OperatorRunState> _runs = new(StringComparer.Ordinal);
    private readonly object _startLock = new();
    private readonly Timer _idleSweeper;

    public AssistantRunService(
        IRunStore runStore,
        IRunEventStream eventStream,
        IOperatorAssistantAgent assistant,
        IOptions<AssistantRunOptions> options,
        ILogger<AssistantRunService> logger)
    {
        _runStore = runStore;
        _eventStream = eventStream;
        _assistant = assistant;
        _options = options.Value;
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var runId = RunId.New();
        var key = runId.ToString();
        var now = DateTimeOffset.UtcNow;

        // Reserve the concurrency slot atomically before any IO so two parallel starts cannot both
        // slip past the bound.
        lock (_startLock)
        {
            var active = _runs.Values.Count(s => string.Equals(s.User, caller.User, StringComparison.Ordinal));
            if (active >= _options.MaxConcurrentRunsPerUser)
                throw new AssistantConcurrencyLimitException(_options.MaxConcurrentRunsPerUser);

            _runs[key] = new OperatorRunState(caller.User, projectId, modelId, now);
        }

        try
        {
            ProjectId? project = ProjectId.TryParse(projectId, out var pid) ? pid : null;
            var run = new Run
            {
                Id = runId,
                // Operator runs drive the platform via MCP tools; they have no worktree/repo. The
                // empty placeholders satisfy the required Run fields without implying a workspace.
                RepositoryPath = string.Empty,
                OriginatingBranch = string.Empty,
                ModelSource = ModelSource.GitHubCopilot,
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
        }, ct).ConfigureAwait(false);

        OperatorAssistantResponse? firstTurn = null;
        if (!string.IsNullOrWhiteSpace(firstMessage))
            firstTurn = await RunTurnAsync(caller, callerBearerToken, key, firstMessage!, contextRunId, ct)
                .ConfigureAwait(false);

        return new StartAssistantRunResult(runId, RunStatus.InProgress, firstTurn);
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

    private async Task<OperatorAssistantResponse> RunTurnAsync(
        CallerContext caller,
        string callerBearerToken,
        string runId,
        string message,
        string? contextRunId,
        CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var state))
            throw new AssistantRunHttpException(StatusCodes.Status404NotFound, "run_not_found",
                "No active operator run with that id. It may have been closed after an idle timeout.");

        if (!caller.Owns(state.User))
            throw new AssistantRunHttpException(StatusCodes.Status403Forbidden, "forbidden",
                "You do not own this operator run.");

        // Serialize turns within a single conversation so history and streamed events stay ordered.
        await state.Turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.Touch();

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

    /// <summary>Closes runs that have been idle beyond the configured timeout. Exposed for tests so
    /// the sweep can be driven deterministically without waiting on the timer.</summary>
    internal void SweepIdleRuns(DateTimeOffset now)
    {
        foreach (var (key, state) in _runs.ToArray())
        {
            if (now - state.LastActivityUtc < _options.IdleTimeout)
                continue;
            if (!_runs.TryRemove(key, out _))
                continue;

            _ = CloseIdleRunAsync(key);
        }
    }

    private async Task CloseIdleRunAsync(string key)
    {
        try
        {
            if (RunId.TryParse(key, out var runId))
                await _runStore.UpdateStatusAsync(runId, RunStatus.Completed, DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);

            await AppendAsync(key, EventTypes.RunCompleted, new
            {
                runId = key,
                reason = "idle_timeout",
            }, CancellationToken.None).ConfigureAwait(false);

            await _eventStream.CompleteAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close idle operator run {RunId}", key);
        }
    }

    private void SweepIdleRunsSafe()
    {
        try { SweepIdleRuns(DateTimeOffset.UtcNow); }
        catch (Exception ex) { _logger.LogWarning(ex, "Operator run idle sweep failed."); }
    }

    private ValueTask AppendAsync(string runId, string type, object payload, CancellationToken ct) =>
        _eventStream.AppendAsync(runId, new RunEvent(0, type, payload), ct);

    public void Dispose() => _idleSweeper.Dispose();

    /// <summary>Per-conversation in-memory state: owner, context, activity timestamp, bounded history,
    /// and a per-run gate that serializes turns.</summary>
    private sealed class OperatorRunState(string user, string? projectId, string? modelId, DateTimeOffset startedAt)
    {
        private readonly List<ConsoleFacadeHistoryMessage> _history = [];
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
    }
}
