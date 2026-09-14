---
title: Transaction traces
---

# Transaction traces

The Observability area includes a hierarchical transaction trace panel for recent coordinator runs.
Open a project, go to **Observability** then **Traces**, and choose **Preview trace** on a run.

The trace detail opens on the **Timeline** tab. Its summary row shows the agent, run ID,
measured trace-window duration, recorded input/output tokens when they exist, and the
aggregate trace status. The typed trace attributes include an optional `sessionId`; the
panel shows a recorded session identity when available. Missing session telemetry remains
**Not recorded** in span detail rather than being invented from the run ID.

The timeline reconstructs hierarchy from parent/child relationships, shows each span against
the measured trace window, and uses distinct agent, model, and tool visuals. Failed spans use
error styling. The summary status follows the root agent/coordinator spans, so a failed tool
attempt that a later retry recovers from remains visible without marking the whole trace failed.
Select a span to inspect its status, timing, correlation IDs, operation, model
usage, and tool-call context. Long timelines scroll inside their own bounded region, keeping the
selected-span inspector visible beside the rows on wide screens. On narrower screens the layout
stacks so both the timeline and inspector remain usable.

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
For `run_command`, these command details start collapsed and require an explicit expansion. The UI
applies a second, bounded redaction pass before displaying legacy event data, so credentials
and oversized or deeply nested payloads cannot leak through the inspector.

While a sandboxed `run_command` is still executing, the same run stream carries
`tool.execution_pending` heartbeats. The trace row and inspector use those correlated,
output-free events to show **Running** elapsed time for the matching command span until a
`tool.result` or `tool.error` arrives. The heartbeat deliberately contains only run id, tool-call id,
tool name, start/deadline timestamps, and elapsed seconds — never command text or command output.

When a tool attempt fails, its inspector shows a bounded, redacted error detail and explains the
outcome in the context of the run: **Recovered** means the run later completed, **Run active**
means the final outcome is not yet recorded, and **Run failed** means a terminal failure was
recorded. The summary keeps failed-tool-attempt count separate from run state, so a recovered
attempt is never presented as a failed run. The events API also redacts legacy error payloads and
limits an individual error detail to 2,048 characters before the UI receives it.

Command progress is not inferred from the absence of a terminal result. The current sandbox tool
uses the executor's buffered `ExecuteAsync` contract, while `StreamAsync` carries output-bearing
chunks. Replacing one with the other merely to make a timer appear active could alter buffering or
expose output prematurely. A future live-progress implementation must emit a correlated,
output-free server-side heartbeat only while the exact command invocation is active, stop it on
every terminal path, and deliver it over the existing run stream rather than adding browser polling.
It must not use activity/span telemetry for command text or output.

Each span, and the panel header, also shows an **AIC** (AI Credit) cost chip. An LLM span shows the
cost of that one model turn; an Invoke Agent span shows the summed cost of every turn and tool call
nested beneath it; the panel header sums model cost across the currently loaded trace tree,
not an independently verified full-run billing total. Agent/tool nodes do not double-count
cost already attributed to their model leaves. The underlying value
is `agentweaver.aiu.nano` (nano-AIU), sourced from the model turn's `agent.turn.usage` event and
formatted with the same `AIC` unit used elsewhere in the app (see `formatAic`/`CostChip`).

## Troubleshooting empty traces

An empty `spans` collection can mean that Application Insights has not produced trace data yet.
If the trace query itself fails, the run-traces API response instead includes a non-null
`queryError` value. The API also writes an Error-level log containing the query context and the
truncated failing KQL, so operators can distinguish a query failure from a genuinely empty trace.

On `/projects/:projectId/observability/traces`, **Open run** opens the orchestration;
**Preview trace** expands the trace panel in place. Inspect the real loaded spans and
query state before drawing conclusions. The two former screenshot embeds were placeholders,
not evidence of a populated trace or selected-span inspector, and are omitted.

## Source

| Concern | Source |
| --- | --- |
| Traces page preview action | `apps/web/src/pages/observability/ObservabilityTracesPage.tsx:76`, `:327–333` |
| Hierarchical trace panel and recorded session identity | `apps/web/src/components/runs/TransactionTracePanel.tsx:511`, `:743`, `:1081–1084` |
| Parent/child reconstruction and synthetic LLM leaf | `apps/web/src/components/runs/traceTree.ts:171`, `:271` |
| Tool call argument/output correlation by `callId` | `apps/web/src/components/runs/traceTree.ts:102` (`buildToolCallIndex`) |
| AIC cost aggregation per span/agent invocation/run | `apps/web/src/components/runs/traceTree.ts` (`aggregateNanoAiu`, `totalNanoAiu`) |
| Trace DTO, query error, and optional session ID | `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:133`, `:137`, `:175` |
| Trace endpoint | `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:161` |
| AppInsights trace query and span classification | `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs` |
| Safe trace-dimension names | `packages/Agentweaver.Domain/TraceTelemetry.cs` |
| Truthful query-error display | `apps/web/src/components/runs/TransactionTracePanel.tsx:1153–1167` |
| Recovered failed tool versus terminal failure | `apps/web/src/components/runs/TransactionTracePanel.tsx:768–771`; `apps/web/src/__tests__/TransactionTraceDetail.test.tsx:233`, `:325`, `:355` |
| Persisted run event log (source of `tool.call`/`tool.result`/`tool.error`) | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs` |

## See also

- [Events & observability](../deep-dive/events-observability.md)
- [Token usage monitoring](./token-usage-monitoring.md)
