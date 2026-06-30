# Token usage — Reference

API endpoints, response types, status codes, and MCP tools for GitHub Copilot token consumption and AI Credit monitoring across all four aggregation levels.

## Endpoints

| Method | Path | Auth | Description |
|---|---|---|---|
| `GET` | `/api/runs/{id}/usage` | API key (run owner) | Token usage summary for a single run |
| `GET` | `/api/workflow-runs/{id}/usage` | API key (project owner) | Token usage summary for a workflow-run envelope |
| `GET` | `/api/projects/{id}/usage` | API key (project owner) | Project-level usage, time-ranged (default: last 30 days) |
| `GET` | `/api/usage` | Admin only | App-wide usage, time-ranged (default: last 30 days) |

The following existing endpoints also include a `token_usage` field in their responses when usage data is available:

| Endpoint | Added field | Type |
|---|---|---|
| `GET /api/projects/{id}/dashboard` | `token_usage` | `TokenUsageSummaryDto` (nullable) |
| `GET /api/overview` | `token_usage` | `AppUsageDto` (nullable) |

Source: `apps/Agentweaver.Api/Endpoints/UsageEndpoints.cs`, `apps/Agentweaver.Api/Metrics/MetricsDtos.cs`.

## Response types

### TokenUsageSummaryDto

Returned by the run, workflow-run, and project usage endpoints.

| Field | Type | Description |
|---|---|---|
| `input_tokens` | `long` | Total prompt tokens across all turns in scope |
| `output_tokens` | `long` | Total completion tokens across all turns in scope |
| `total_tokens` | `long` | Sum of `input_tokens` + `output_tokens` |
| `total_nano_aiu` | `long` | Total cost in nano-AIU units |
| `by_model` | `TokenUsageByModelDto[]` | Per-model breakdown |

### TokenUsageByModelDto

| Field | Type | Description |
|---|---|---|
| `model_id` | `string` | Copilot model identifier as returned by the SDK (e.g. `gpt-4o`) |
| `input_tokens` | `long` | Prompt tokens attributed to this model |
| `output_tokens` | `long` | Completion tokens attributed to this model |
| `total_nano_aiu` | `long` | Cost in nano-AIU attributed to this model |

### AppUsageDto

Returned by `GET /api/usage` and embedded in the overview response.

| Field | Type | Description |
|---|---|---|
| `generated_utc` | `string` (ISO-8601) | Timestamp when this response was generated |
| `from_utc` | `string` (ISO-8601) | Start of the query time range |
| `to_utc` | `string` (ISO-8601) | End of the query time range |
| `total_tokens` | `long` | Total tokens across all projects in range |
| `total_nano_aiu` | `long` | Total nano-AIU across all projects in range |
| `by_project` | `ProjectUsageDto[]` | Per-project breakdown |
| `by_model` | `TokenUsageByModelDto[]` | Per-model breakdown across all projects |

### ProjectUsageDto

Appears in the `by_project` array of `AppUsageDto`.

| Field | Type | Description |
|---|---|---|
| `project_id` | `string` | Project identifier |
| `project_name` | `string` | Project display name |
| `total_tokens` | `long` | Total tokens attributed to this project |
| `total_nano_aiu` | `long` | Total nano-AIU attributed to this project |
| `by_model` | `TokenUsageByModelDto[]` | Per-model breakdown for this project |

### AIC conversion

```
1 AIC (AI Credit) = 1,000,000,000 nano-AIU
display value = total_nano_aiu / 1_000_000_000  (4 decimal places)
```

## Status codes

| Code | Meaning |
|---|---|
| `200 OK` | Usage data returned (may be zero totals if no turns recorded yet) |
| `400 Bad Request` | Invalid query parameter (e.g. unparseable date) |
| `401 Unauthorized` | Missing or unrecognized API key |
| `403 Forbidden` | Key is valid but caller does not own the resource, or non-admin caller on `/api/usage` |
| `404 Not Found` | Run, workflow-run, or project id does not exist |

## Time range defaults

The project and app-level endpoints accept optional `from` and `to` query parameters:

```
GET /api/projects/{id}/usage?from=2026-05-01T00:00:00Z&to=2026-06-01T00:00:00Z
GET /api/usage?from=2026-05-01T00:00:00Z&to=2026-06-01T00:00:00Z
```

- Both parameters are **ISO-8601 UTC** timestamps.
- When omitted, the server defaults to the **last 30 days** ending at the time of the request.
- The run and workflow-run endpoints do not accept time range parameters — they return all usage for that specific run scope.


## UI data feeds

| UI surface | Data source | Notes |
|---|---|---|
| Board run card cost chip | `RunCardDto.total_nano_aiu` / `total_tokens`, with supplementary `GET /api/runs/{id}/usage` when missing | `apps/web/src/api/types.ts:767`, `apps/web/src/api/types.ts:783`, `apps/web/src/components/board/RunCard.tsx:90`, `apps/web/src/components/board/RunCard.tsx:160` |
| Workflow run DAG agent node | `GET /api/runs/{id}/usage` through `runUsage` | `apps/web/src/api/client.ts:758`, `apps/web/src/pages/WorkflowRunPage.tsx:700` |
| Coordinator DAG coordinator/subtask nodes | Coordinator run usage plus child `GET /api/runs/{childId}/usage` summaries | `apps/web/src/pages/CoordinatorRunPage.tsx:1527`, `apps/web/src/pages/CoordinatorRunPage.tsx:1604` |
| Project dashboard usage panel | `GET /api/projects/{id}/usage?from=...&to=...` | `apps/web/src/api/client.ts:766`, `apps/web/src/pages/DashboardPage.tsx:289`, `apps/web/src/pages/DashboardPage.tsx:504` |
| Project dashboard leaderboard Cost column | Scoped run usage aggregated by agent over the same dashboard range selector | `apps/web/src/pages/DashboardPage.tsx:299`, `apps/web/src/pages/DashboardPage.tsx:304`, `apps/web/src/pages/DashboardPage.tsx:461`, `apps/web/src/pages/DashboardPage.tsx:494` |
| Overview Cost overview and top-project bars | `GET /api/usage` or embedded `OverviewDto.token_usage` | `apps/web/src/api/client.ts:774`, `apps/web/src/api/types.ts:1280`, `apps/web/src/pages/OverviewPage.tsx:225`, `apps/web/src/pages/OverviewPage.tsx:439` |

## MCP tools

### `get_run_usage`

Returns token usage for a single run.

Source: `apps/Agentweaver.Mcp/Tools/RunTools.cs`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `run_id` | `string` | Yes | The run id to query |

Returns a `TokenUsageSummaryDto`-shaped result with `input_tokens`, `output_tokens`, `total_tokens`, `total_nano_aiu`, and `by_model`.

### `get_project_usage`

Returns token usage for a project, optionally time-ranged.

Source: `apps/Agentweaver.Mcp/Tools/ProjectTools.cs`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `project_id` | `string` | Yes | The project id to query |
| `from` | `string` | No | ISO-8601 UTC start (default: 30 days ago) |
| `to` | `string` | No | ISO-8601 UTC end (default: now) |

Returns a `TokenUsageSummaryDto`-shaped result.

## See also

- [Token usage monitoring — Deep Dive](../deep-dive/token-usage-monitoring.md) — concept, flow diagram, source table.
- [Token usage monitoring — User Guide](../experience/token-usage-monitoring.md) — watch counter, dashboard section, overview.
- [API reference](./api.md) — complete endpoint listing.
- [MCP tools reference](./mcp-tools.md) — all available MCP tools.
