using System.Text.Json;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Persists run-id → execution pod bindings in the shared RunEvents log. In AKS/Postgres this is the
/// same cross-replica store used by the SSE relay, so graph snapshots served by any API replica can
/// resolve a child run's pod even though the worker registered it in a different process.
/// </summary>
public sealed class RunEventExecutionPodNameStore : IExecutionPodNameStore
{
    public const string EventType = "sandbox.execution_pod.bound";

    private readonly IRunEventStream _eventStream;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunEventExecutionPodNameStore> _logger;

    public RunEventExecutionPodNameStore(
        IRunEventStream eventStream,
        IServiceScopeFactory scopeFactory,
        ILogger<RunEventExecutionPodNameStore> logger)
    {
        _eventStream = eventStream;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Register(string runId, string podName)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(podName))
            return;

        try
        {
            _eventStream.AppendAsync(runId, new Agentweaver.Domain.RunEvent(0, EventType, new
            {
                podName,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            })).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist execution pod binding for run {RunId}", runId);
        }
    }

    public string? TryGet(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var payloadJson = db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId && e.EventType == EventType)
                .OrderByDescending(e => e.Sequence)
                .Select(e => e.PayloadJson)
                .FirstOrDefault();

            return ReadPodName(payloadJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read execution pod binding for run {RunId}", runId);
            return null;
        }
    }

    public static string? ReadPodName(object payload)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(payload);
            return ReadPodName(element);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadPodName(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return ReadPodName(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadPodName(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("podName", out var pod)
        && pod.ValueKind == JsonValueKind.String
            ? pod.GetString()
            : null;
}
