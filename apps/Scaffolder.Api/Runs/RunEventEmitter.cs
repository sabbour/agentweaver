using System.Text.Json;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Streaming;
using Scaffolder.Domain;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Writes a run event to the durable, append-only log and then publishes it to
/// live subscribers. Append happens first so the assigned sequence is known
/// before the event is broadcast; this keeps the SSE id and the persisted cursor
/// identical (FR-019, FR-021).
/// </summary>
public sealed class RunEventEmitter
{
    private readonly SqliteEventStore _store;
    private readonly RunEventBroadcaster _broadcaster;

    public RunEventEmitter(SqliteEventStore store, RunEventBroadcaster broadcaster)
    {
        _store = store;
        _broadcaster = broadcaster;
    }

    public async Task<RunEvent> EmitAsync<TPayload>(
        RunId runId,
        string type,
        TPayload payload,
        string? callId = null,
        CancellationToken ct = default)
        where TPayload : notnull
    {
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var evt = await _store.AppendNewAsync(runId, type, json, callId, ct).ConfigureAwait(false);
        _broadcaster.Publish(evt);
        return evt;
    }
}
