# Events and observability

## Event stream

Agentweaver records each run as an ordered, durable stream of facts. The stream lets clients reconstruct timelines, graphs, approvals, status, and diagnostics after a restart or reconnection.

Each event has a run-local cursor. The normal append path obtains its sequence
from durable storage before local delivery; subscribers resume from their last
observed sequence. See the [durable write-through and replay sequence](../diagrams/distributed-execution-scaling-fig4.png).

Production uses `EfRunEventStream` to write events to PostgreSQL and poll by cursor across replicas. Local development uses `SqliteRunEventStream`. Process-local channels provide local delivery only; they are not the production cross-replica transport.

## Telemetry

When `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured, the API calls `.UseAzureMonitor()` to export OpenTelemetry data to Azure Monitor. The runtime also records explicit Agentweaver counters and histograms for operational metrics.

Model turns emit activities and `agentweaver.token.usage` metrics. Despite its
name, that counter measures nano-AIU cost, not token count. Project metrics and
run traces query telemetry when available; Azure Monitor export is separate from
durable operational-event persistence.

## Operational use

Use the run stream to explain a specific run. Use `GET /api/projects/{id}/metrics` for project performance and `GET /api/metrics/runs/{runId}/traces` for trace details. Use cluster diagnostics for runtime dependencies and sandbox inventory.

## Source

- `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs`
- `apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs`
- `apps/Agentweaver.Api/Infrastructure/AzureMonitorBootstrap.cs`
- `apps/Agentweaver.Api/Infrastructure/AgentWeaverMetrics.cs`
- `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs`

## Related reading

- [Token usage monitoring](./token-usage-monitoring.md)
- [Cluster diagnostics reference](../reference/cluster-diagnostics.md)

<!-- diagram-context:distributed-execution-scaling-fig4:start -->
<details id="diagram-context-distributed-execution-scaling-fig4" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Durable allocation, then cursor replay</td></tr>
<tr><td>takeaway</td><td>Cross-replica delivery polls shared rows; local notifications are not a distributed bus.</td></tr>
<tr><td>RecordNext</td><td>RecordNext</td></tr>
<tr><td>RecordNext</td><td>Request sequence allocation</td></tr>
<tr><td>RecordNext</td><td>Append with Sequence = 0</td></tr>
<tr><td>EF append</td><td>EF append</td></tr>
<tr><td>EF append</td><td>ReadCommitted + advisory lock</td></tr>
<tr><td>EF append</td><td>Serialize allocation per run</td></tr>
<tr><td>RunEvents</td><td>RunEvents</td></tr>
<tr><td>RunEvents</td><td>MAX+1 / insert / commit</td></tr>
<tr><td>RunEvents</td><td>Return assigned sequence</td></tr>
<tr><td>Local history</td><td>Local history</td></tr>
<tr><td>Local history</td><td>Updated after durable ack</td></tr>
<tr><td>Local history</td><td>Notify only local waiters</td></tr>
<tr><td>Web replica A</td><td>Web replica A</td></tr>
<tr><td>Web replica A</td><td>Read Sequence &gt; lastSeen</td></tr>
<tr><td>Web replica A</td><td>250 ms delay only when empty</td></tr>
<tr><td>Browser watcher</td><td>Browser watcher</td></tr>
<tr><td>Browser watcher</td><td>SSE sequence IDs</td></tr>
<tr><td>Browser watcher</td><td>Remember last event cursor</td></tr>
<tr><td>Reconnect cursor</td><td>Reconnect cursor</td></tr>
<tr><td>Reconnect cursor</td><td>Last-Event-ID</td></tr>
<tr><td>Reconnect cursor</td><td>Not tied to original web pod</td></tr>
<tr><td>Web replica B</td><td>Web replica B</td></tr>
<tr><td>Web replica B</td><td>Ordered replay and live tail</td></tr>
<tr><td>Web replica B</td><td>Read same shared RunEvents</td></tr>
<tr><td>Resumed watcher</td><td>Resumed watcher</td></tr>
<tr><td>Resumed watcher</td><td>Receive rows after cursor</td></tr>
<tr><td>Resumed watcher</td><td>No PostgreSQL NOTIFY required</td></tr>
<tr><td>arrow-1</td><td>append</td></tr>
<tr><td>arrow-2</td><td>commit</td></tr>
<tr><td>arrow-3</td><td>ack</td></tr>
<tr><td>arrow-4</td><td>poll</td></tr>
<tr><td>arrow-5</td><td>SSE</td></tr>
<tr><td>arrow-6</td><td>resume</td></tr>
<tr><td>note-0</td><td>Rows: write-through / live delivery / reconnect on another replica.</td></tr>
<tr><td>note-1</td><td>Explicit historic sequence: identical content is idempotent; conflicts fail.</td></tr>
<tr><td>note-2</td><td>SQL commits before local history update; polling reads the shared table.</td></tr>
<tr><td>notes</td><td>Rows: write-through / live delivery / reconnect on another replica.; Explicit historic sequence: identical content is idempotent; conflicts fail.; SQL commits before local history update; polling reads the shared table.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:distributed-execution-scaling-fig4:end -->
