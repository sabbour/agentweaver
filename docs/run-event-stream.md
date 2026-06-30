# Run Event Stream (`IRunEventStream`)

> Feature: `016-run-event-stream` · Foundation wave `016-US4`

A durable, pub/sub event log for a run. It replaces the ad-hoc, memory-only
`RunStreamStore` ring buffer as the **system of record** for a run's `RunEvent`
timeline, while preserving the existing SSE wire protocol byte-for-byte.

## Why

The legacy `RunStreamStore` / `RunStreamEntry` held each run's events in an
in-memory list and only snapshotted them to SQLite at terminal time
(fire-and-forget). That design lost mid-run events on a crash, capped runs at
10k events (silent drop), and forced the coordinator to poll child runs. The
`IRunEventStream` abstraction makes every append durable per-append and gives
consumers a push-based `await foreach` over an `IAsyncEnumerable<RunEvent>`.

## Architecture — two layers

```
 Producer (agent runtime / workflow / endpoints)
        │  AppendAsync(runId, evt)
        ▼
 ┌─────────────────────────────────────────────────────────────┐
 │ SqliteRunEventStream                                          │
 │                                                              │
 │  Layer 1 — SQLite write-through (DURABILITY)                 │
 │    synchronous INSERT into RunEvents (memory.db, WAL)        │
 │    ── returns only after the row is committed ──            │
 │                                                              │
 │  Layer 2 — in-process Channel<RunEvent> per run (FAN-OUT)   │
 │    bounded (capacity 1000); TryWrite publishes to live      │
 │    subscribers; surplus live copies are dropped (durable    │
 │    copy remains in Layer 1)                                 │
 └─────────────────────────────────────────────────────────────┘
        ▲                              │
        │ SubscribeAsync(runId, from)  │ replay (Layer 1) then tail (Layer 2)
        │                              ▼
 Consumer (SSE endpoint / MCP run_watch / coordinator ObserveChild)
```

- **Layer 1** is the source of truth. The `RunEvents` table shape is frozen by
  migration `20260616063937_AddRunEvents`
  (`RunId`, `Sequence`, `EventType`, `PayloadJson`, `CreatedAt`, unique index on
  `(RunId, Sequence)`) and lives in `memory.db`, the EF Core `MemoryDbContext`
  file — separate from the main `agentweaver.db`. No new migration is added.
- **Layer 2** is an in-process `Channel<RunEvent>` per active run for
  low-latency tailing. It is *not* a system of record: a slow or absent consumer
  that fills the bounded channel causes surplus *live* copies to be dropped; the
  durable copy in Layer 1 is unaffected and is recovered by a reconnecting
  subscriber via replay.

## Interface contract

```csharp
public interface IRunEventStream
{
    // Durable, synchronous SQLite write BEFORE return, then publish to the channel.
    // Honors evt.Sequence when > 0 (idempotent on the unique (RunId, Sequence)
    // index); otherwise assigns the next monotonic sequence.
    ValueTask AppendAsync(string runId, RunEvent evt, CancellationToken ct = default);

    // Replay persisted events from fromSequence, then tail the live channel.
    // Completes on a terminal event, channel completion, or cancellation.
    IAsyncEnumerable<RunEvent> SubscribeAsync(string runId, int fromSequence = 0, CancellationToken ct = default);

    // Close the live channel so subscribers drain and complete normally.
    ValueTask CompleteAsync(string runId, CancellationToken ct = default);
}
```

`RunEvent` (`packages/Agentweaver.Domain/RunEvent.cs`) is unchanged:
`record RunEvent(int Sequence, string Type, object Payload)`.

## `SubscribeAsync` — replay-then-tail

```
Subscriber                         Stream                         SQLite        Channel
   │  SubscribeAsync(run, K)         │                              │             │
   │────────────────────────────────▶ GetOrAdd channel ───────────────────────────▶ (created)
   │                                 │  SELECT … WHERE Sequence > K  │             │
   │                                 │◀──────────────────────────────│             │
   │  ◀── yield persisted events ────│  (track lastReplayed)         │             │
   │                                 │   terminal? → yield break     │             │
   │                                 │  await ReadAllAsync ──────────────────────▶ tail
   │  ◀── yield live events ─────────│   skip Sequence ≤ lastReplayed             │
   │                                 │   terminal? / channel closed → complete    │
```

Boundary guarantees:

- **No gap.** The channel is created *before* the DB read, so any append that
  lands during replay is captured by the channel and delivered while tailing.
- **No duplicate.** While tailing, events with `Sequence <= lastReplayed`
  (already delivered by replay) are skipped.
- **Clean termination.** Replay stops at a terminal event
  (`run.completed` / `run.failed` / `run.cancelled`), so subscribing to a
  finished run replays its full history and then completes. Tailing ends when
  `CompleteAsync` closes the channel or `ct` is cancelled.

> Edge case: subscribing to a run that has *no* terminal event and *no* live
> producer (e.g. an orphan) tails until `ct` cancels. In production the SSE
> handler passes the HTTP request-abort token, and `WorkflowRestartService`
> terminalizes orphans at startup, so this is bounded in practice.

## SSE wire protocol (frozen)

The `/api/runs/{id}/stream` handler in `RunEndpoints.cs` emits exactly:

```
id: <Sequence>
event: <EventType>
data: <camelCase JSON payload>

```

terminated by:

```
event: done
data: {}

```

The `done` frame closes the current SSE connection. For most runs this is permanent — it signals the run has reached a true terminal state (`run.completed`, `run.failed`, `run.cancelled`). However, for coordinator runs, `done` can also close the stream at an intermediate gate. When the coordinator pauses at the outcome-spec `awaiting_confirmation` gate (autopilot off, or before the unattended confirm fires), the server closes the SSE connection with `done`. After the user confirms the spec, the frontend calls `onReconnect()` to reopen the stream from the last received sequence, and the run continues. The `done` frame at a spec gate is therefore not a permanent terminal — the stream can be reconnected.

True permanent terminals are the run-level events `run.completed`, `run.failed`, and `run.cancelled`. Once any of these is replayed, `SubscribeAsync` completes and further reconnects return only the history.

Reconnects send `Last-Event-ID: <Sequence>`, which maps to
`SubscribeAsync(runId, fromSequence: <Sequence>)`. The frame layout, the
`Last-Event-ID` semantics, and the `[DONE]` terminator are unchanged; the
frontend `useRunStream` reader and the MCP `run_watch` client require no changes.

## Migration guide — producers/consumers `RunStreamStore` → `IRunEventStream`

This is staged across waves so the wire format stays frozen at every step.

| Concern | Legacy (`RunStreamStore`) | `IRunEventStream` |
| --- | --- | --- |
| Produce an event | `entry.Record(...)` / `entry.RecordNext(...)` | `AppendAsync(runId, evt)` |
| Durability | terminal-only `PersistRunEventsAsync` (fire-and-forget) | synchronous per-append write-through |
| Consume | `GetSnapshotSince(lastSeen)` + `WaitForChangeAsync` poll loop | `await foreach (… in SubscribeAsync(runId, from))` |
| Completion | `streamStore.Complete(runId)` | `CompleteAsync(runId)` |
| Cap / eviction | `MaxEventsPerRun`, stale-sweep, generation bumps | none (durable, unbounded) |

### Wave status

- **`016-US4` (this wave) — foundation.**
  - `IRunEventStream` + `SqliteRunEventStream` shipped and registered in DI
    (`AddSingleton<IRunEventStream, SqliteRunEventStream>()`).
  - **Producers**: the agent event path (`RecordingChannelWriter`, via
    `RunWorkflowFactory.GetRecordingWriter` / `CreateSubStreamWriter`) now writes
    *through* `IRunEventStream.AppendAsync` per append, using the sequence the
    in-memory entry assigned. `PersistRunEventsAsync` re-appends the full history
    idempotently as a terminal **backfill safety net** and then calls
    `CompleteAsync`.
  - `RunStreamStore` is **retained** as the live fan-out path so all current
    consumers (the SSE live loop, review/merge endpoints, sandbox/outcome reads,
    coordinator `ObserveChildAsync`) behave exactly as before.
- **`016-US2`** migrates coordinator child observation
  (`CoordinatorDispatchService.ObserveChildAsync`) to `SubscribeAsync` and
  retires the `Task.Delay(200)` polling fallback.
- **`016-US3`** removes the cap / eviction / generation machinery and deletes
  `RunStreamStore` once every consumer subscribes via `IRunEventStream`.

### Adding a new producer or consumer

Depend only on `IRunEventStream`:

```csharp
// produce
await runEventStream.AppendAsync(runId, new RunEvent(seq, EventTypes.AgentMessage, payload));

// consume
await foreach (var evt in runEventStream.SubscribeAsync(runId, fromSequence, ct))
{
    // …
}
```

A future multi-instance backend (e.g. Redis Streams, Postgres `LISTEN`/`NOTIFY`)
is a one-class swap of `SqliteRunEventStream` with no producer/consumer changes.
