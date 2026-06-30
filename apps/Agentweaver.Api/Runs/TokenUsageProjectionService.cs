using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

/// <summary>
/// Background service that subscribes to run event streams and persists
/// <c>agent.turn.usage</c> events as <see cref="TokenUsageRecord"/> rows.
///
/// <para>On startup it subscribes to every active (non-terminal) run. A periodic poll then
/// discovers newly started runs and subscribes them. Each subscription runs in its own
/// fire-and-forget task; failures are caught and logged — an individual run error never
/// crashes the service.</para>
/// </summary>
public sealed class TokenUsageProjectionService : BackgroundService
{
    private static readonly RunStatus[] ActiveStatuses =
    [
        RunStatus.Pending,
        RunStatus.InProgress,
        RunStatus.AwaitingReview,
        RunStatus.Committing,
        RunStatus.Merging,
    ];

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IRunStore _runStore;
    private readonly IRunEventStream _eventStream;
    private readonly ITokenUsageStore _usageStore;
    private readonly ILogger<TokenUsageProjectionService> _logger;
    private readonly ConcurrentDictionary<string, byte> _subscribedRunIds = new(StringComparer.Ordinal);

    public TokenUsageProjectionService(
        IRunStore runStore,
        IRunEventStream eventStream,
        ITokenUsageStore usageStore,
        ILogger<TokenUsageProjectionService> logger)
    {
        _runStore = runStore;
        _eventStream = eventStream;
        _usageStore = usageStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial subscription pass: subscribe to all currently active runs.
        await SubscribeToActiveRunsAsync(stoppingToken).ConfigureAwait(false);

        // Periodic discovery of newly started runs.
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SubscribeToActiveRunsAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SubscribeToActiveRunsAsync(CancellationToken ct)
    {
        try
        {
            foreach (var status in ActiveStatuses)
            {
                var runs = await _runStore.GetByStatusAsync(status, ct).ConfigureAwait(false);
                foreach (var run in runs)
                {
                    var runIdStr = run.Id.ToString();
                    if (_subscribedRunIds.TryAdd(runIdStr, 0))
                    {
                        _ = Task.Run(() => ProcessRunEventsAsync(runIdStr, ct), CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — exit cleanly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TokenUsageProjectionService: error discovering active runs.");
        }
    }

    private async Task ProcessRunEventsAsync(string runId, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var evt in _eventStream.SubscribeAsync(runId, fromSequence: 0, stoppingToken).ConfigureAwait(false))
            {
                if (!string.Equals(evt.Type, EventTypes.AgentTurnUsage, StringComparison.Ordinal))
                    continue;

                try
                {
                    await ProcessUsageEventAsync(runId, evt, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "TokenUsageProjectionService: error processing usage event for run {RunId}.", runId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — exit cleanly.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TokenUsageProjectionService: subscription error for run {RunId}.", runId);
        }
        finally
        {
            _subscribedRunIds.TryRemove(runId, out _);
        }
    }

    private async Task ProcessUsageEventAsync(string runId, RunEvent evt, CancellationToken ct)
    {
        if (evt.Payload is not JsonElement json)
        {
            _logger.LogWarning(
                "TokenUsageProjectionService: agent.turn.usage payload for run {RunId} is not a JsonElement.", runId);
            return;
        }

        if (!TryExtractUsage(json, out var modelId, out var inputTokens, out var outputTokens, out var totalNanoAiu))
        {
            _logger.LogWarning(
                "TokenUsageProjectionService: could not extract usage fields from event payload for run {RunId}.", runId);
            return;
        }

        // Resolve WorkflowRunId and ProjectId from the run store.
        string? workflowRunId = null;
        string? projectId = null;
        try
        {
            if (RunId.TryParse(runId, out var parsedRunId))
            {
                var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
                if (run is not null)
                {
                    workflowRunId = run.WorkflowRunId;
                    projectId = run.ProjectId?.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "TokenUsageProjectionService: could not resolve run metadata for run {RunId}; recording without project context.", runId);
        }

        var record = new TokenUsageRecord
        {
            Id = $"{runId}:{evt.Sequence}",
            RunId = runId,
            WorkflowRunId = workflowRunId,
            ProjectId = projectId,
            ModelId = modelId,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalNanoAiu = totalNanoAiu,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        await _usageStore.RecordAsync(record, ct).ConfigureAwait(false);
    }

    private static bool TryExtractUsage(
        JsonElement json,
        out string modelId,
        out long inputTokens,
        out long outputTokens,
        out long totalNanoAiu)
    {
        modelId = "";
        inputTokens = 0;
        outputTokens = 0;
        totalNanoAiu = 0;

        if (!json.TryGetProperty("modelId", out var modelProp)
            && !json.TryGetProperty("model_id", out modelProp))
            return false;
        modelId = modelProp.GetString() ?? "";
        if (string.IsNullOrEmpty(modelId)) return false;

        if (json.TryGetProperty("inputTokens", out var inputProp)
            || json.TryGetProperty("input_tokens", out inputProp))
        {
            inputTokens = inputProp.ValueKind == JsonValueKind.Number
                ? inputProp.GetInt64() : 0;
        }

        if (json.TryGetProperty("outputTokens", out var outputProp)
            || json.TryGetProperty("output_tokens", out outputProp))
        {
            outputTokens = outputProp.ValueKind == JsonValueKind.Number
                ? outputProp.GetInt64() : 0;
        }

        if (json.TryGetProperty("totalNanoAiu", out var nanoProp)
            || json.TryGetProperty("total_nano_aiu", out nanoProp))
        {
            totalNanoAiu = nanoProp.ValueKind == JsonValueKind.Number
                ? nanoProp.GetInt64() : 0;
        }

        return true;
    }
}
