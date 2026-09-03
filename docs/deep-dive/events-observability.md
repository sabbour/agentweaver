# Events and observability

## Event stream

Agentweaver records each run as an ordered, durable stream of facts. The stream lets clients reconstruct timelines, graphs, approvals, status, and diagnostics after a restart or reconnection.

Each event has a run-local sequence number. Producers write an event before subscribers receive it. Subscribers resume from their last observed sequence.

Production uses `EfRunEventStream` to write events to PostgreSQL and poll by cursor across replicas. Local development uses `SqliteRunEventStream`. Process-local channels provide local delivery only; they are not the production cross-replica transport.

## Telemetry

When `APPLICATIONINSIGHTS_CONNECTION_STRING` is configured, the API calls `.UseAzureMonitor()` to export OpenTelemetry data to Azure Monitor. The runtime also records explicit Agentweaver counters and histograms for operational metrics.

Model turns emit activities and `agentweaver.token.usage` metrics. Project metrics and run traces query this telemetry when it is available.

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
