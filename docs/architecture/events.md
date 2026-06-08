# Events

Run events are the primary mechanism for communicating progress from the agent to clients. The API records events in memory and delivers them to SSE subscribers in real time.

## In-memory event store

`RunStreamStore` holds a `RunStreamEntry` per run. Each entry records events into a bounded list (capped at 10,000 events per run). The server allocates `sequence` numbers at write time, giving each run a monotonic total order. Events are written by the orchestrator through a `RecordingChannelWriter` that calls `Record()` on the entry directly.

Completed entries are retained for up to 256 finished runs. In-progress entries older than two hours are evicted as likely leaked. Once an entry is evicted or the process restarts, the live event sequence for that run is lost.

## Live fan-out

When a client connects to `GET /api/runs/{id}/stream`, the endpoint reads the entry from `RunStreamStore` and enters a poll loop:

1. Call `GetSnapshotSince(lastSeen)` which returns new events and the completion flag atomically under a single lock.
2. Write each event as an SSE frame.
3. If the run is not yet complete, call `WaitForChangeAsync` which blocks until the next event is recorded, completion is signaled, or a one-second timeout elapses.
4. Repeat until the run completes or the client disconnects.

Each `Record()` call wakes all blocked waiters immediately, so event delivery is prompt — not polling-interval-limited.

## SSE resume cursor

The SSE endpoint exposes each event's `sequence` as the SSE `id`. Clients can reconnect with `Last-Event-ID` set to the last sequence they received. The endpoint resumes from that point in the in-memory history.

Reconnection works as long as the run's entry still exists in memory. Delivery is at least once; clients should deduplicate by `sequence`.

## Process restart behavior

The in-memory event store does not survive a process restart. On startup, the API marks any run still recorded as `in_progress` in SQLite as `failed`. If a client connects to the stream for such a run after restart, the endpoint falls back to replaying the run's persisted `result` field (the final concatenated agent output) as a single `agent.message` event, then sends a `done` frame.

This means the granular event-by-event replay is only available while the process that ran the agent is still alive and the entry has not been evicted. Durable per-event persistence across restarts is specified (FR-022) but not yet implemented.

## Persistence (run record)

The `runs` SQLite table stores the run's metadata, status, and the final `result` text. This is the only durable artifact that survives a restart. There is no `run_events` table in the current schema — the append-only event log described in the spec is planned but not yet built.

## Review and merge events

The event log is designed to span the full lifecycle from submission through merge or decline. Review and merge events (`review.requested`, `review.approved`, `review.declined`, `merge.completed`, `merge.failed`) will be recorded on the same per-run stream once the review workflow is implemented.
