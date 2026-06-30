# Token usage monitoring

Agentweaver tracks GitHub Copilot token consumption at four levels — individual run, workflow orchestration, project, and app — and surfaces the data live during execution and in post-run dashboards. This guide covers where the token counter appears in the product, what it shows, and how to read the numbers.

Scope: this page describes the token usage surfaces in the web UI. For the API and MCP tool contracts see the [reference](../reference/token-usage.md); for the architecture see the [deep dive](../deep-dive/token-usage-monitoring.md).

## Live run view (Watch page)

While an agent is running, a live token counter appears below the execution timeline on the Watch page. It updates automatically each time the agent completes a turn and the `agent.turn.usage` SSE event arrives — no manual refresh needed.

The counter shows:

- **Input tokens** — prompt tokens sent to the model this run.
- **Output tokens** — completion tokens returned by the model.
- **Total tokens** — sum of input and output.
- **Total AIC** — total AI Credits consumed to 4 decimal places.
- **Per-model breakdown** — a table listing each model that was called, with its individual token counts and AIC contribution.

The per-model table is especially useful for coordinator orchestrations where different specialist agents run on different models: you can see which model is responsible for the most consumption at a glance.

![Token counter on the Watch page](/screenshots/watch-token-counter.png)

📸 **Screenshot** — Watch page showing the live token counter and per-model breakdown.

The counter starts at zero and accumulates as turns complete. If the run has no model activity yet (e.g. it is still connecting or only coordinator lifecycle events have arrived), the panel shows zero. It will populate as soon as the first model response lands.

## Project dashboard

The project dashboard includes a **Token usage** section with a time-range selector. Navigate to a project and open its **Dashboard** tab to find it.

The section shows:

- **Total tokens** and **Total AIC** for the selected time window.
- **Per-model breakdown** — same table format as the Watch page, aggregated across all runs in the period.

The time-range selector offers **7 days**, **30 days**, and **90 days** presets. The default view is 30 days. Switching ranges reloads the section from `GET /api/projects/{id}/usage?from=...&to=...`.

![Token usage section on the project dashboard](/screenshots/dashboard-token-usage.png)

📸 **Screenshot** — Project dashboard showing the token usage section with model breakdown.

### Reading AICs on the dashboard

AICs (AI Credits) are displayed to four decimal places. Very small runs will show fractional credits such as `0.0023 AIC`. Larger projects with many parallel coordinator runs will accumulate whole credits or more. The conversion is:

```
1 AIC = 1,000,000,000 nano-AIU
displayed AIC = total_nano_aiu ÷ 1,000,000,000
```

This is a product-level display convention; the raw `total_nano_aiu` value is available in the API response for precise comparisons.

## App overview (admin)

The **Overview** page includes an app-level token usage section visible to admin users. It shows total tokens and AICs across **all projects** in the selected time window, plus a per-project breakdown table so you can see which projects are contributing most to model spend.

- **Admin-only**: non-admin users see the Overview page but the token usage section is hidden (the underlying `/api/usage` endpoint returns `403 Forbidden` for non-admins, and the UI degrades gracefully without showing an error).
- **Per-project breakdown**: each row shows the project name, total tokens, total AIC, and a collapsed per-model table.
- **Time range**: the same `from` and `to` query parameters apply; the default is the last 30 days.

![App-level usage on the Overview page](/screenshots/overview-token-usage.png)

📸 **Screenshot** — Overview page showing app-level token usage and per-project breakdown.

## Understanding the numbers

### Token definitions

| Term | What it counts |
|---|---|
| **Input tokens** | The prompt sent to the model — system prompt, conversation history, tool definitions, and tool results |
| **Output tokens** | The completion returned by the model — agent messages and structured tool calls |
| **Total tokens** | Input + output; this is the primary billing unit |

Long-running coordinator orchestrations accumulate tokens across many child agents. Each agent's turns are recorded separately and aggregated when you query at workflow-run or project level.

### AIC conversion

The Agentweaver backend stores cost as `totalNanoAiu` (nano-AIU). The display conversion is:

```
1 AIC = 1,000,000,000 nano-AIU
```

The product always displays AICs rounded to 4 decimal places. If you need the raw integer for budget comparisons or exports, use the `total_nano_aiu` field in the API response.

### Why per-model matters

Different Copilot models have different token costs. If your project uses a mix — for example a lighter model for triage agents and a heavier model for implementation agents — the per-model breakdown lets you understand which model is driving spend and adjust your agent definitions accordingly.

## See also

- [Token usage — Reference](../reference/token-usage.md) — endpoints, DTOs, status codes, MCP tools.
- [Token usage monitoring — Deep Dive](../deep-dive/token-usage-monitoring.md) — concept, event flow, source table.
- [Runs, board, and live watch](./runs-board-watch.md) — the Watch page and timeline.
- [Operations](./operations.md) — project health and observability surfaces.
