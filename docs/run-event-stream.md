# Run Event Stream (`IRunEventStream`)

> Feature: `016-run-event-stream` · updated for cross-replica streaming

Agentweaver treats a run's event stream as a durable, ordered log first and a live SSE feed second. The current implementation mirrors every `RunStreamEntry` append into the shared `RunEvents` table, and the EF/Postgres stream reads from that table by cursor so any replica can stream any run. The pod-local `RunStreamStore` remains a same-replica compatibility and low-latency path; it is no longer the only place an SSE client can see a run's live history. Source: `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:98`, `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs:115`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:77`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:416`.

For the scaling story, see [Distributed execution & scaling](./deep-dive/distributed-execution-scaling.md#run-event-fan-out-under-multiple-replicas). For the event taxonomy, see [Events reference](./reference/events.md).

## Architecture — shared store, cursor stream

![Architecture — shared store, cursor stream: Run producer, RunStreamEntry, RunEvents, Replica A, Replica B, Browser / MCP watcher](diagrams/canonical-durable-event-stream.png)

<!-- Editable source: diagrams/src/canonical-durable-event-stream.drawio.
     Export with pinned draw.io Desktop 31.4.5 using --spec canonical-durable-event-stream.
     Review records: diagrams/reviews/canonical-durable-event-stream/. -->

The horizontal-scale invariant is simple: **the database log is the source of truth, and the cursor is the replay boundary**. `EfRunEventStream.AppendAsync` writes through before acknowledging (`WriteThroughAsync`), and `SubscribeAsync` repeatedly loads rows whose sequence is greater than the caller's last seen cursor, yielding them in sequence order until a terminal event appears. It drains the full replay batch before stopping, so a diagnostic row persisted immediately after a terminal row is still delivered before the SSE subscription closes. Source: `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:63`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:71`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:77`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:84`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:111`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:180`.

## Delivery sequence — durable-first, then cursor polling

![EF/Postgres sequence: append and commit, acknowledge assigned sequence, read ordered rows after the cursor, poll on an empty batch, and drain the terminal-containing batch before closing](diagrams/canonical-durable-event-stream-sequence.png)

<!-- Editable source: diagrams/src/canonical-durable-event-stream-sequence.drawio.
     Export with pinned draw.io Desktop 31.4.5 using --spec canonical-durable-event-stream-sequence.
     Review records: diagrams/reviews/canonical-durable-event-stream-sequence/. -->

The EF/Postgres path does not switch to an in-process live channel after replay:
each replica continues reading the shared log. SQLite's compatibility implementation
registers a local channel before replay and then tails it, dropping already replayed
sequences. These are distinct delivery implementations, not a shared cross-replica bus.

## What changed from the old in-memory-only stream

| Concern | Current behavior | Source |
|---|---|---|
| Event production | `RunStreamEntry` obtains the authoritative sequence from the durable append before exposing the event to local history/waiters. Local notification is not the cross-replica relay. | `RunStreamStore.cs:183-212` |
| Cross-replica reads | `EfRunEventStream.SubscribeAsync` polls the shared `RunEvents` table every `250 ms` when no new rows were emitted, so a subscriber on a different replica catches up without sticky sessions. | `EfRunEventStream.cs:33`, `EfRunEventStream.cs:77`, `EfRunEventStream.cs:96`, `EfRunEventStream.cs:180` |
| Late terminal diagnostics | Diagnostics can remain durable after terminalization. EF's late message-delta suppression is process-local; it is not a database-enforced cross-replica terminal fence. A row outside the drained terminal-containing batch may require a later replay. | `EfRunEventStream.cs:59-86,144-189` |
| Replay terminal semantics | Replay yields all rows in the loaded batch, then stops if the batch contained a terminal event. `coordinator.assembly_failed` is terminal; retryable `coordinator.assembly_blocked` is not, so subscribers can stay attached across recovery. | `EfRunEventStream.cs:35`, `EfRunEventStream.cs:84`, `EfRunEventStream.cs:111`, `SqliteRunEventStream.cs:34`, `SqliteRunEventStream.cs:153` |
| Sequence safety | PostgreSQL allocates per-run `MAX(Sequence) + 1` under an advisory transaction lock before commit. An explicit duplicate sequence is idempotent only when its contents match; a conflicting payload is rejected. | `EfRunEventStream.cs:223-284,316-321` |
| Terminal safety net | Terminal persistence re-appends the full in-memory history through `IRunEventStream`; duplicate `(RunId, Sequence)` rows are skipped, so missed mirrors are reconciled without duplication. | `RunWorkflowFactory.cs:287`, `RunWorkflowFactory.cs:296`, `RunWorkflowFactory.cs:298`, `RunWorkflowFactory.cs:301` |
| Execution pod badge | AgentHost pod bindings append `sandbox.execution_pod.bound` to the same shared log, so graph pod badges resolve after refresh and across replicas. | `RunEventExecutionPodNameStore.cs:15`, `RunEventExecutionPodNameStore.cs:38`, `RunEventExecutionPodNameStore.cs:59`, `KubernetesSandboxExecutor.cs:342` |
| SSE fallback | If the current replica has no local stream entry, `/api/runs/{id}/stream` subscribes to `IRunEventStream` from the `Last-Event-ID` cursor and writes those events as SSE frames. | `RunEndpoints.cs:416`, `RunEndpoints.cs:423`, `RunEndpoints.cs:429`, `RunEndpoints.cs:431`, `RunEndpoints.cs:443` |

`EfRunEventStreamTests` proves the cross-replica behavior by creating two stream instances over the same database: one appends events and the other receives them through `SubscribeAsync`. The tests also prove that `RunStreamStore.RecordNext` mirrors into the shared stream. Source: `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:27`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:30`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:37`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:42`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:50`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:56`, `tests/Agentweaver.Tests/EfRunEventStreamTests.cs:63`.

## SSE wire protocol

The `/api/runs/{id}/stream` endpoint still emits the same Server-Sent Event shape: an `id` line with the run-event sequence, an `event` line with the event type, and a JSON `data` line, followed by a `done` frame when the server closes the stream. Source: `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:315`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:448`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:468`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:489`.

Reconnects use the browser's `Last-Event-ID` header as the `fromSequence` cursor. The endpoint parses that header on the replay path and on the local path, so refreshes resume from the last emitted sequence instead of starting over. Source: `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:423`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:424`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:429`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:452`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:453`.

## Read-through coordinator gates

Browser refreshes around coordinator gates no longer surface transient `404` or `409` responses just because the spec or plan row is being created. `GET /api/runs/{id}/outcome-spec` waits up to three seconds for the persisted `OutcomeSpec`, and `GET /api/runs/{coordinatorRunId}/work-plan` waits up to five seconds for the persisted work plan before returning not found. Confirming an already-confirmed outcome spec also attempts to return the confirmed spec instead of a conflict. Source: `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:53`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:88`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:90`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:167`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:571`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:574`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:580`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:591`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:594`, `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs:600`.

## Source

| Concern | File |
|---|---|
| Local stream entry and mirror to shared stream | `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs` |
| EF/Postgres shared event stream and cursor polling | `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs` |
| SSE endpoint, `Last-Event-ID`, replay fallback | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs` |
| Coordinator outcome/work-plan read-through waits | `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs` |
| Terminal backfill and recording writer integration | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs` |
| Cross-instance tests | `tests/Agentweaver.Tests/EfRunEventStreamTests.cs` |

## See also

- [Distributed execution & scaling](./deep-dive/distributed-execution-scaling.md#run-event-fan-out-under-multiple-replicas) — why the shared event store is required for multi-replica deployments.
- [Events & observability](./deep-dive/events-observability.md) — event taxonomy and observability model.
- [Token usage monitoring](./experience/token-usage-monitoring.md) — one UI surface that consumes the same live stream and usage projections.

<!-- diagram-context:canonical-durable-event-stream:start -->
<details id="diagram-context-canonical-durable-event-stream">
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Postgres is the event relay</td></tr>
<tr><td>subtitle</td><td>Any API replica can serve a cursor over durable RunEvents—no sticky session required.</td></tr>
<tr><td>group-title0</td><td>Write path · replica A</td></tr>
<tr><td>group-title1</td><td>Read path · replica B</td></tr>
<tr><td>Run producer</td><td>Run producer</td></tr>
<tr><td>Run producer</td><td>Append a structured event</td></tr>
<tr><td>Run producer</td><td>runId + type + payload</td></tr>
<tr><td>EF event stream</td><td>EF event stream</td></tr>
<tr><td>EF event stream</td><td>Serialize writes per run</td></tr>
<tr><td>EF event stream</td><td>pg_advisory_xact_lock</td></tr>
<tr><td>RunEvents</td><td>RunEvents</td></tr>
<tr><td>RunEvents</td><td>Shared PostgreSQL table</td></tr>
<tr><td>RunEvents</td><td>(RunId, Sequence)</td></tr>
<tr><td>Web / MCP watcher</td><td>Web / MCP watcher</td></tr>
<tr><td>Web / MCP watcher</td><td>Consume ordered events</td></tr>
<tr><td>Web / MCP watcher</td><td>last delivered cursor</td></tr>
<tr><td>SSE endpoint</td><td>SSE endpoint</td></tr>
<tr><td>SSE endpoint</td><td>Emit id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>ordered response frames</td></tr>
<tr><td>EF subscriber</td><td>EF subscriber</td></tr>
<tr><td>EF subscriber</td><td>Read Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>idle poll: 250 ms</td></tr>
<tr><td>e1</td><td>append</td></tr>
<tr><td>e2</td><td>commit</td></tr>
<tr><td>e3</td><td>ordered batch</td></tr>
<tr><td>e4</td><td>yield</td></tr>
<tr><td>e5</td><td>SSE frames</td></tr>
<tr><td>assurance-title</td><td>POSTGRES LANE ONLY</td></tr>
<tr><td>assurance-line1</td><td>SQLite register-channel / replay / tail is a separate implementation—not this architecture.</td></tr>
<tr><td>assurance-line2</td><td>Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>Run producer</td><td>Input</td></tr>
<tr><td>Run producer</td><td>RunStreamEntry</td></tr>
<tr><td>Run producer</td><td>Identity</td></tr>
<tr><td>Run producer</td><td>runId + event type</td></tr>
<tr><td>Run producer</td><td>Body</td></tr>
<tr><td>Run producer</td><td>Structured payload</td></tr>
<tr><td>Run producer</td><td>Ack</td></tr>
<tr><td>Run producer</td><td>After durable commit</td></tr>
<tr><td>EF event stream</td><td>Lock</td></tr>
<tr><td>EF event stream</td><td>Per-run advisory lock</td></tr>
<tr><td>EF event stream</td><td>Next</td></tr>
<tr><td>EF event stream</td><td>MAX(Sequence) + 1</td></tr>
<tr><td>EF event stream</td><td>Write</td></tr>
<tr><td>EF event stream</td><td>Save transaction</td></tr>
<tr><td>EF event stream</td><td>Commit</td></tr>
<tr><td>EF event stream</td><td>Before acknowledgement</td></tr>
<tr><td>RunEvents</td><td>Table</td></tr>
<tr><td>RunEvents</td><td>Key</td></tr>
<tr><td>RunEvents</td><td>RunId + Sequence</td></tr>
<tr><td>RunEvents</td><td>Order</td></tr>
<tr><td>RunEvents</td><td>Ascending sequence</td></tr>
<tr><td>RunEvents</td><td>Reuse</td></tr>
<tr><td>RunEvents</td><td>Same type / payload</td></tr>
<tr><td>Web / MCP watcher</td><td>Client</td></tr>
<tr><td>Web / MCP watcher</td><td>Web or MCP</td></tr>
<tr><td>Web / MCP watcher</td><td>Resume</td></tr>
<tr><td>Web / MCP watcher</td><td>Last delivered cursor</td></tr>
<tr><td>Web / MCP watcher</td><td>Replica</td></tr>
<tr><td>Web / MCP watcher</td><td>No sticky requirement</td></tr>
<tr><td>Web / MCP watcher</td><td>History</td></tr>
<tr><td>Web / MCP watcher</td><td>Durable ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Frame</td></tr>
<tr><td>SSE endpoint</td><td>id + event + data</td></tr>
<tr><td>SSE endpoint</td><td>Cursor</td></tr>
<tr><td>SSE endpoint</td><td>Last-Event-ID</td></tr>
<tr><td>SSE endpoint</td><td>Delivery</td></tr>
<tr><td>SSE endpoint</td><td>Yield ordered events</td></tr>
<tr><td>SSE endpoint</td><td>Close</td></tr>
<tr><td>SSE endpoint</td><td>After batch is drained</td></tr>
<tr><td>EF subscriber</td><td>Query</td></tr>
<tr><td>EF subscriber</td><td>Sequence &gt; cursor</td></tr>
<tr><td>EF subscriber</td><td>Idle</td></tr>
<tr><td>EF subscriber</td><td>Poll after 250 ms</td></tr>
<tr><td>EF subscriber</td><td>State</td></tr>
<tr><td>EF subscriber</td><td>Shared durable table</td></tr>
<tr><td>EF subscriber</td><td>Blocked</td></tr>
<tr><td>EF subscriber</td><td>Retryable: keep open</td></tr>
<tr><td>producer</td><td>Coordinator or run execution; Acknowledgement follows commit</td></tr>
<tr><td>append</td><td>Allocate MAX(Sequence) + 1; Save and commit transaction</td></tr>
<tr><td>store</td><td>Cross-replica ordered history; Explicit duplicates must match payload</td></tr>
<tr><td>client</td><td>Reconnect from the cursor; No local channel dependency</td></tr>
<tr><td>sse</td><td>Cursor advances after delivery; Drain batch before terminal close</td></tr>
<tr><td>reader</td><td>Query the shared durable table; Retryable assembly_blocked stays open</td></tr>
<tr><td>notes</td><td>POSTGRES LANE ONLY; SQLite register-channel / replay / tail is a separate implementation—not this architecture.; Late-delta suppression is process-local; do not read it as a database-wide terminal fence.</td></tr>
<tr><td>groups</td><td>Write path · replica A; Read path · replica B</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-durable-event-stream:end -->

<!-- diagram-context:canonical-durable-event-stream-sequence:start -->
<details id="diagram-context-canonical-durable-event-stream-sequence" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>notes</td><td>LOOP · repeat durable reads; idle wait = 250 ms; Drain the whole batch before terminal close. Retryable assembly_blocked is not terminal.; Explicit-sequence reuse is idempotent only for matching type/payload. SQLite live channels are a separate lane.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-durable-event-stream-sequence:end -->
