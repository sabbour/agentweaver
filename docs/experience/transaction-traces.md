---
title: Transaction traces
---

# Transaction traces

The Observability area includes a hierarchical transaction trace panel for recent coordinator runs.
Open a project, go to **Observability** then **Traces**, and choose **Preview trace** on a run.

The trace detail opens on the **Timeline** tab. Its summary row shows the agent, run ID,
measured trace-window duration, recorded input/output tokens when they exist, and the
aggregate trace status. Agentweaver does not currently return a session identifier in its
trace DTO, so the UI deliberately shows the run ID rather than presenting invented session
telemetry.

The timeline reconstructs hierarchy from parent/child relationships, shows each span against
the measured trace window, and uses distinct agent, model, and tool visuals. Failed spans use
error styling. Select a span to inspect its status, timing, correlation IDs, operation, model
usage, and tool-call context.

The trace tree is organized by span relationships:

1. **Invoke Agent** nodes represent agent turns.
2. **Execute Tool** nodes represent tool calls beneath the agent span that triggered them.
3. **LLM** nodes represent model calls. When the backend span carries model or token usage directly
   on an agent span, the UI synthesizes an LLM leaf so the hierarchy still shows agent to model.

Expand or collapse rows to follow the transaction. Select a span to inspect event time, duration,
status, operation name, model, token usage, or tool name. If Application Insights has not produced
trace data for the run yet, the panel shows an empty state.

The **Attributes** tab lists the typed, allow-listed dimensions returned by the run-traces API for
the selected span: session/run/project identity; agent and workflow-run identity; operation,
model, provider, and routing; tool and policy/authorization decisions; sandbox/runtime; token
usage; and status/error type. Every field is explicit: **Not recorded** means the span predates the
dimension or Agentweaver did not truthfully have that fact. The API never returns arbitrary
OpenTelemetry custom dimensions, prompt text, credentials, raw tokens, or raw tool input/output.

The **Events** tab lists persisted run events using their actual sequence number and type. Every
newly persisted event has a server-side UTC append timestamp. The API projects that as
`timestamp_utc` at the event level (and reports event status and an existing duration when
available). For legacy rows with no trustworthy timestamp, the fields remain absent instead of
being synthesized at read time. Historical `agent.system_prompt` and `agent.task` payloads are
also withheld while their event sequence/type/time remain visible. Expand a payload only when its
recorded fields are needed.

For an **Execute Tool** span, the detail panel also shows the tool's **Input** and **Output**. These
come from the persisted `tool.call` / `tool.result` / `tool.error` run events (matched to the span
by `callId`), not from Application Insights. Objects and JSON-string output are formatted as
readable JSON. A failed tool call appears as an error-formatted output. If data is missing, the pane
says **No input** or **No output**; if a value is redacted, it is explicitly marked **Redacted**.
The UI applies a second, bounded redaction pass before displaying legacy event data, so credentials
and oversized or deeply nested payloads cannot leak through the inspector.

Each span, and the panel header, also shows an **AIC** (AI Credit) cost chip. An LLM span shows the
cost of that one model turn; an Invoke Agent span shows the summed cost of every turn and tool call
nested beneath it; the panel header shows the total cost across the whole run. The underlying value
is `agentweaver.aiu.nano` (nano-AIU), sourced from the model turn's `agent.turn.usage` event and
formatted with the same `AIC` unit used elsewhere in the app (see `formatAic`/`CostChip`).

## Troubleshooting empty traces

An empty `spans` collection can mean that Application Insights has not produced trace data yet.
If the trace query itself fails, the run-traces API response instead includes a non-null
`queryError` value. The API also writes an Error-level log containing the query context and the
truncated failing KQL, so operators can distinguish a query failure from a genuinely empty trace.

![Observability Traces page listing recent coordinator runs](/screenshots/observability-traces.png)

> 📸 **Screenshot — `observability-traces.png`**
> *Shows:* the **Observability** Traces tab listing recent coordinator runs with status badges, **Open run**, **Preview trace**, and **Refresh**.
> *Path:* open a project → click **Observability** → **Traces** → `/projects/:projectId/observability/traces`.

![Expanded transaction trace preview with span details](/screenshots/observability-trace-preview.png)

> 📸 **Screenshot — `observability-trace-preview.png`**
> *Shows:* the expanded **Preview trace** panel with the hierarchical transaction trace, span rows, and selected span details when AppInsights has data.
> *Path:* `/projects/:projectId/observability/traces` → click **Preview trace**.

## Source

| Concern | Source |
| --- | --- |
| Traces page route and preview action | `apps/web/src/pages/observability/ObservabilityTracesPage.tsx:69` |
| Hierarchical trace panel | `apps/web/src/components/runs/TransactionTracePanel.tsx:294` |
| Parent/child reconstruction and synthetic LLM leaf | `apps/web/src/components/runs/traceTree.ts:22` |
| Tool call argument/output correlation by `callId` | `apps/web/src/components/runs/traceTree.ts:1` (`buildToolCallIndex`) |
| AIC cost aggregation per span/agent invocation/run | `apps/web/src/components/runs/traceTree.ts` (`aggregateNanoAiu`, `totalNanoAiu`) |
| Trace DTO | `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:133` |
| Trace endpoint | `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:130` |
| AppInsights trace query and span classification | `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs:522` |
| Safe trace-dimension names | `packages/Agentweaver.Domain/TraceTelemetry.cs` |
| Trace-query error response and Error-level logging | `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs:585`, `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs:662` |
| Persisted run event log (source of `tool.call`/`tool.result`/`tool.error`) | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:503` |

## See also

- [Events & observability](../deep-dive/events-observability.md)
- [Token usage monitoring](./token-usage-monitoring.md)
