---
title: Transaction traces
---

# Transaction traces

The Observability area includes a hierarchical transaction trace panel for recent coordinator runs.
Open a project, go to **Observability** then **Traces**, and choose **Preview trace** on a run.

The trace tree is organized by span relationships:

1. **Invoke Agent** nodes represent agent turns.
2. **Execute Tool** nodes represent tool calls beneath the agent span that triggered them.
3. **LLM** nodes represent model calls. When the backend span carries model or token usage directly
   on an agent span, the UI synthesizes an LLM leaf so the hierarchy still shows agent to model.

Expand or collapse rows to follow the transaction. Select a span to inspect event time, duration,
status, operation name, model, token usage, or tool name. If Application Insights has not produced
trace data for the run yet, the panel shows an empty state.

## Source

| Concern | Source |
| --- | --- |
| Traces page route and preview action | `apps/web/src/pages/observability/ObservabilityTracesPage.tsx:69` |
| Hierarchical trace panel | `apps/web/src/components/runs/TransactionTracePanel.tsx:294` |
| Parent/child reconstruction and synthetic LLM leaf | `apps/web/src/components/runs/traceTree.ts:22` |
| Trace DTO | `apps/Agentweaver.Api/Metrics/MetricsDtos.cs:133` |
| Trace endpoint | `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs:130` |
| AppInsights trace query and span classification | `apps/Agentweaver.Api/Metrics/AppInsightsMetricsService.cs:522` |

## See also

- [Events & observability](../deep-dive/events-observability.md)
- [Token usage monitoring](./token-usage-monitoring.md)
