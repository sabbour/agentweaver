# Token usage monitoring

## Flow

After a model turn, the runtime emits a durable `agent.turn.usage` run event. The payload includes input tokens, output tokens, total tokens, nano-AIU, and the response model when available.

The runtime also creates an `Agentweaver` activity for the model turn and records the `agentweaver.token.usage` metric. It adds run, project, agent, model, token, nano-AIU, duration, and first-token data when the provider supplies it.

In production, run events use the EF/Postgres durable event stream. Local development can use the SQLite event stream. Subscribers replay events by cursor, so a web replica can show work performed by another replica.

## Queries

Application Insights provides project metrics and run traces when telemetry is available. Stored data supplies fallback model and agent usage where supported.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/projects/{id}/metrics` | Project throughput, model, agent, duration, first-token, and AI-credit metrics. |
| `GET /api/runs/{id}/token-breakdown` | Per-agent token and AI-credit data for a run. |
| `GET /api/metrics/runs/{runId}/traces` | Agent and LLM spans for a run. |

The retired `/usage` routes, token-usage projection store, and `token_usage_records` table are not part of the current architecture.

## AI-credit unit

```text
1 AIC (AI Credit) = 1,000,000,000 nano-AIU
display value = total_nano_aiu / 1_000_000_000
```

## Source

- `packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs`
- `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs`
- `apps/Agentweaver.Api/Metrics/MetricsDtos.cs`
- `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs`

## Related reading

- [Token usage and metrics reference](../reference/token-usage.md)
- [Events and observability](./events-observability.md)
