# Token usage and cost visibility

Agentweaver exposes recorded token consumption and AI Credit (AIC) cost through run inspection, graph cost chips, dashboards, and Observability. Read the **scope, time range, and data source** before comparing totals. A selected-range telemetry chart is not the same measurement as a durable run aggregate. For API contracts see the [reference](../reference/token-usage.md); for event/projection internals see the [deep dive](../deep-dive/token-usage-monitoring.md).

## Run and graph cost surfaces

Run usage is projected from `agent.turn.usage` events. `CostChip` delegates its label to `costChipLabel`: positive `totalNanoAiu` displays AIC, positive tokens without cost display a compact token count, and absent/zero values produce no chip. The richer `AiCredits` control shares this formatting for run/graph contexts. The absence of a chip is not evidence that execution was free.

Coordinator inspection exposes AI-credit context and per-agent usage where available. Graph usage is associated with the relevant coordinator or child context, not inferred from a neighboring node. Open an orchestration and inspect its usage controls alongside the selected task's messages and artifacts.

The current board `RunCard` focuses on status, stage, agent, approvals, retry, and archive; it does **not** render `CostChip` or fetch supplemental run usage. Use run inspection and telemetry surfaces rather than expecting a cost beside every board status badge.

Sources: `apps/web/src/components/CostChip.tsx:13`, `apps/web/src/components/costChipFormat.ts:16`, `apps/web/src/components/AiCredits.tsx:109`, `apps/web/src/components/board/RunCard.tsx:146`.

## Scope and surface mapping

| Surface | Scope and source | How to read it |
|---|---|---|
| Run usage API / run inspection | Persisted usage aggregates for that run; project usage APIs have their own endpoint-defined scope. | Treat these as run aggregates, not automatically as telemetry for the dashboard's selected range. |
| Project **Dashboard** | Dashboard state plus `GET /api/projects/{id}/metrics?from=...&to=...`. The **Activity** selector offers 7/30/90-day ranges. | The current **Model metrics** and agent leaderboard **Cost** use the selected-range metrics response, not a client-side sum of `/runs/{id}/usage` calls. |
| Fleet **Overview** | **AI usage & performance** aggregates project observability metrics for the **recent projects shown on that page**. | Its 7/30/90-day selector scopes that rollup; do not call it an exhaustive all-project billing total. |
| Project **Observability → Overview** | Selected-range project metrics. | Inspect model mix, credit trends, response time, and first-token timing with the range visible. |
| Project **Observability → Agents** | Metrics grouped by agent over the selected range. | **Agent token breakdown** is a dimensioned telemetry view, not proof that every run reported usage. |
| Project **Observability → Traces** | Recorded spans for a selected run, with persisted run events for tool detail. | Cost sums the model spans loaded into the trace tree; incomplete telemetry or pagination can differ from a durable full-run aggregate. |

Sources: `apps/web/src/pages/DashboardPage.tsx:675`, `:881`, `:989`, `:1018`; `apps/web/src/pages/OverviewPage.tsx:299`, `:382`; `apps/web/src/pages/observability/ObservabilityOverviewPage.tsx:95`; `apps/web/src/pages/observability/ObservabilityAgentsPage.tsx:95`, `:190`; `apps/web/src/components/runs/traceTree.ts:308`.

## Empty and partial telemetry

Charts use explicit empty states such as **No data for AI credits in this range**, **No response-time data for this range**, and **No first-token data for this range**. Missing or delayed telemetry is not a healthy zero-cost result. Check the selected project/range and query availability before interpreting an empty trend or comparing it with per-model rows.

The previous AI-credit popover, dashboard, and Overview usage images were 1×1 placeholders; the Agents image was also a placeholder. They are omitted. The earlier Observability Overview capture had zero total AIC alongside nonzero model rows and empty trends; it is not used as an illustration of coherent totals. No replacement captures or visual verification are claimed.

Source: `apps/web/src/components/dashboard/ModelPerformancePanels.tsx:332`, `:354`, `:386`, `:390`.

## Understanding the numbers

| Term | What it counts |
|---|---|
| **Input tokens** | Recorded prompt tokens sent to the model. |
| **Output tokens** | Recorded completion tokens returned by the model. |
| **Total tokens** | Reported aggregate for the specified run or telemetry scope; compare like scopes and ranges. |
| **AIC** | Nano-AIU divided by **1,000,000,000**. `formatAic` renders values below 1 AIC with four decimal places. This is a credit unit, not a currency amount. |
| **Per-model / per-agent breakdown** | Usage grouped by the dimension supplied by the endpoint. Missing attribution or incomplete telemetry must not be filled with invented rows. |
| **Trace invocation cost** | Sum of descendant model-call costs; agent and tool nodes do not add a second copy of their own model totals. |

Sources: `apps/web/src/components/costChipFormat.ts:1`; `apps/web/src/components/runs/traceTree.ts:308–327`; `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:159`, `:203`.

## Graph layout

Cost chips, pod indicators, and status metadata contribute to card height. Shared DAG layout uses rendered-height hints so graphs can accommodate that metadata; this is presentation behavior, not another source of usage totals. See `apps/web/src/utils/dagLayout.ts` and `apps/web/src/pages/CoordinatorRunPage.tsx`.

## See also

- [Token usage — Reference](../reference/token-usage.md)
- [Token usage monitoring — Deep Dive](../deep-dive/token-usage-monitoring.md)
- [Runs, board, and live inspection](./runs-board-watch.md)
- [Transaction traces](./transaction-traces.md)
- [Distributed execution & scaling](../deep-dive/distributed-execution-scaling.md)
