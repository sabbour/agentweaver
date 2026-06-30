# Token usage and cost visibility

Agentweaver tracks GitHub Copilot token consumption and AI Credit (AIC) cost at run, workflow/coordinator, project, and app scope. The UI now surfaces that cost in compact run cards, graph nodes, the project dashboard leaderboard, and the fleet Overview page. For API contracts see the [reference](../reference/token-usage.md); for event/projection internals see the [deep dive](../deep-dive/token-usage-monitoring.md).

## Live run view (Watch page)

While an agent is running, a live token counter appears below the execution timeline on the Watch page. It updates as `agent.turn.usage` events arrive through the run stream and renders total tokens, input tokens, output tokens, total AICs, and a per-model table. Source: `apps/web/src/components/TokenUsagePanel.tsx:80`, `apps/web/src/components/TokenUsagePanel.tsx:87`, `apps/web/src/components/TokenUsagePanel.tsx:101`, `apps/web/src/components/TokenUsagePanel.tsx:106`.

![Token counter on the Watch page](/screenshots/watch-token-counter.png)

📸 **Screenshot** — Watch page showing the live token counter and per-model breakdown.

## Run cards and DAG nodes

Run cards in the board show a compact cost chip beside the status badge. If `total_nano_aiu` is positive, the chip displays AICs; otherwise it falls back to compact token count when only tokens are available. Run cards use embedded card fields when present and fetch `GET /api/runs/{id}/usage` as supplementary data when the card did not include totals. Source: `apps/web/src/components/CostChip.tsx:18`, `apps/web/src/components/CostChip.tsx:24`, `apps/web/src/components/board/RunCard.tsx:78`, `apps/web/src/components/board/RunCard.tsx:90`, `apps/web/src/components/board/RunCard.tsx:160`.

Workflow and coordinator graphs use the same `CostChip`. Workflow run pages attach run usage to the agent node; coordinator run pages attach usage to the coordinator node and child-run usage to subtask nodes. Source: `apps/web/src/components/WorkflowGraphPanel.tsx:108`, `apps/web/src/components/WorkflowGraphPanel.tsx:594`, `apps/web/src/pages/WorkflowRunPage.tsx:700`, `apps/web/src/pages/CoordinatorRunPage.tsx:1527`, `apps/web/src/pages/CoordinatorRunPage.tsx:1604`.

## Project dashboard

The project dashboard's **Agent and usage metrics** range selector scopes both the **Token and AIC usage** panel and the leaderboard's **Cost** column. The selector offers **Last 7 days**, **Last 30 days**, and **Last 90 days**; changing it calls `GET /api/projects/{id}/usage?from=...&to=...`, then fetches scoped run usage to aggregate costs per leaderboard agent. Source: `apps/web/src/pages/DashboardPage.tsx:40`, `apps/web/src/pages/DashboardPage.tsx:44`, `apps/web/src/pages/DashboardPage.tsx:289`, `apps/web/src/pages/DashboardPage.tsx:293`, `apps/web/src/pages/DashboardPage.tsx:299`, `apps/web/src/pages/DashboardPage.tsx:304`, `apps/web/src/pages/DashboardPage.tsx:425`, `apps/web/src/pages/DashboardPage.tsx:461`, `apps/web/src/pages/DashboardPage.tsx:494`, `apps/web/src/pages/DashboardPage.tsx:504`.

![Token usage section on the project dashboard](/screenshots/dashboard-token-usage.png)

📸 **Screenshot** — Project dashboard showing the shared range filter, leaderboard Cost column, and Token/AIC usage panel.

## App overview (admin)

The **Overview** page reads the embedded `token_usage` from `GET /api/overview` when available and separately fetches `GET /api/usage`. A `403` from the app-wide endpoint is treated as admin-only and hidden without a visible error. Admins see a **Cost overview** tile with total AICs, total tokens, top-project bars, a per-model `TokenUsagePanel`, and a **Usage by project** table. Source: `apps/web/src/pages/OverviewPage.tsx:222`, `apps/web/src/pages/OverviewPage.tsx:225`, `apps/web/src/pages/OverviewPage.tsx:239`, `apps/web/src/pages/OverviewPage.tsx:245`, `apps/web/src/pages/OverviewPage.tsx:272`, `apps/web/src/pages/OverviewPage.tsx:439`, `apps/web/src/pages/OverviewPage.tsx:442`, `apps/web/src/pages/OverviewPage.tsx:445`, `apps/web/src/pages/OverviewPage.tsx:450`, `apps/web/src/pages/OverviewPage.tsx:465`, `apps/web/src/pages/OverviewPage.tsx:475`.

![App-level usage on the Overview page](/screenshots/overview-token-usage.png)

📸 **Screenshot** — Overview page showing the Cost overview tile, top project usage bars, and project usage table.

## Understanding the numbers

| Term | What it counts | Source |
|---|---|---|
| **Input tokens** | Prompt tokens sent to the model. | `apps/web/src/api/types.ts:1187` |
| **Output tokens** | Completion tokens returned by the model. | `apps/web/src/api/types.ts:1189` |
| **Total tokens** | Aggregate token count for the selected run/project/app scope. | `apps/web/src/api/types.ts:1190` |
| **Total AICs** | `total_nano_aiu` converted by `formatAic`. Values below `1` AIC show four decimal places. | `apps/web/src/api/types.ts:1191`, `apps/web/src/components/CostChip.tsx:3` |
| **Per-model breakdown** | Model rows in `by_model`, used by `TokenUsagePanel`. | `apps/web/src/api/types.ts:1179`, `apps/web/src/components/TokenUsagePanel.tsx:118` |

## DAG layout note

The added cost chips and pod/status badges made DAG cards taller, so graph layout now shares `DAG_NODE_SEP = 96` and per-node rendered-height hints. Workflow runs, coordinator topology, shared workflow graph panels, and the visual workflow editor all pass those hints into `layoutDag`, preventing node overlap as cards gain metadata. Source: `apps/web/src/utils/dagLayout.ts:6`, `apps/web/src/utils/dagLayout.ts:24`, `apps/web/src/utils/dagLayout.ts:48`, `apps/web/src/pages/WorkflowRunPage.tsx:706`, `apps/web/src/components/CoordinatorTopologyGraph.tsx:522`, `apps/web/src/components/WorkflowGraphPanel.tsx:931`, `apps/web/src/components/VisualWorkflowEditor.tsx:236`.

## See also

- [Token usage — Reference](../reference/token-usage.md) — endpoints, DTOs, and status codes.
- [Token usage monitoring — Deep Dive](../deep-dive/token-usage-monitoring.md) — event flow, projections, and source table.
- [Runs, board, and live watch](./runs-board-watch.md) — run cards, the board, and Watch page flow.
- [Distributed execution & scaling](../deep-dive/distributed-execution-scaling.md) — shared event-store streaming under multiple replicas.
