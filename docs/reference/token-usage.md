# Token usage and metrics reference

Agentweaver returns Application Insights metrics when telemetry is available. Project and run endpoints use stored data as a fallback for model and agent usage.

## Endpoints

| Method | Path | Access | Description |
| --- | --- | --- | --- |
| `GET` | `/api/projects/{id}/metrics?from=...&to=...` | Project viewer | Project performance, model, agent, and AI-credit metrics. |
| `GET` | `/api/metrics/runs/{runId}/traces` | Run viewer | Agent and LLM spans for a coordinator run and its children. |
| `GET` | `/api/runs/{id}/token-breakdown` | Run viewer | Per-agent token and AI-credit data for a run. |

`from` and `to` are optional for project metrics. The server accepts parseable timestamps. Missing values use the last 30 days.

## Project metrics

`GET /api/projects/{id}/metrics` returns `ProjectMetricsDto`.

| Field | Description |
| --- | --- |
| `throughput` | Created and completed run counts by day. |
| `leaderboard` | Per-agent activity, success, duration, and AI-credit data. |
| `invocationTrend` | Run-creation counts by day. |
| `modelUsage` | Invocation count and nano-AIU totals by model. |
| `responseDuration` | P50 and P95 duration by model. |
| `timeToFirstToken` | P50 and P95 first-token time when telemetry provides it. |
| `agentBreakdown` | Invocation count, token total, and nano-AIU total by agent. |
| `aiCreditUsageTrend` | Daily nano-AIU totals. |

`GET /api/projects/{id}/dashboard` returns dashboard counters with compatibility throughput and leaderboard fields. `GET /api/overview` returns global operational counters and activity. Neither route returns token-usage fields.

## Run metrics

`GET /api/runs/{id}/token-breakdown` returns `RunAgentTokenBreakdownDto`. It includes `runId`, `source`, `hasAgentData`, `totalTokens`, `totalNanoAiu`, and `breakdown`.

`GET /api/metrics/runs/{runId}/traces` returns `RunTraceDto`. Each span can include its parent ID, agent, tool, model, token counts, AI-credit total, duration, success result, and operation name.

## AI-credit unit

```text
1 AIC (AI Credit) = 1,000,000,000 nano-AIU
display value = total_nano_aiu / 1_000_000_000
```

## Status codes

| Code | Meaning |
| --- | --- |
| `200 OK` | Metrics data was returned. |
| `400 Bad Request` | The run or project ID is invalid. |
| `403 Forbidden` | The caller cannot view the project or run. |
| `404 Not Found` | The project or run does not exist. |

## Source

- `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs`
- `apps/Agentweaver.Api/Metrics/MetricsDtos.cs`
