# Events

Scaffolders treats the event log as the durable record of a run. The API writes every event before it publishes that event to live subscribers, so the stream and the persisted history stay aligned.

## Append-only storage

The `run_events` table is keyed by `(run_id, sequence)`. SQLite triggers reject `UPDATE` and `DELETE`, which makes the log append-only by policy as well as by convention.

The server allocates `sequence` numbers inside the SQLite write transaction. That means `sequence` is the only ordering key you need for a run, and no client can race the server or invent its own cursor.

## Live fan-out

`RunEventBroadcaster` keeps an in-memory stream per run and fans events out through `Channel<RunEvent>`. Each new subscriber attaches to the live channel first, then backfills events from SQLite after its cursor, then continues with live events.

That subscribe-then-backfill design avoids gaps during the handoff from history to live traffic. If replay overlaps with live delivery, the client can deduplicate by `sequence`.

## SSE resume cursor

The SSE endpoint exposes each event's `sequence` as the SSE `id`. Clients reconnect with `Last-Event-ID`, which the API treats as `afterSequence`.

On reconnect, the API:

1. Reads durable events after the cursor.
2. Streams them in ascending `sequence` order.
3. Continues with live events from the broadcaster.

Delivery is at least once. Clients should store the latest `sequence` they rendered and ignore any duplicate sequence on replay.

## Restart recovery

Because the event log is durable, replay works across process restarts. On startup, the API initializes the database, then runs restart recovery.

Restart recovery looks for runs still marked `in_progress` from the previous process and finalizes them as failed with the reason `process-restart`. That leaves no run stranded in a non-terminal state and preserves a complete event trail for operators and users.

## Review and merge events

The event log does not stop at `run.completed`. It continues through `review.requested`, the review decision, and the merge outcome, so the same stream covers the full lifecycle from submission to merge or decline.
