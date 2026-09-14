# Visual role versus runtime context - pass-01

Node fields feed different executors; role/kind never becomes an executing catalog identity.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Authored node: Fields are not interchangeable; node declaration | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| n1 | role / kind: Rendering metadata; not executing identity | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-70` |
| n2 | prompt / charter: Generic worker context; prompt or publish node | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:935-955` |
| n3 | Diagram presentation: Visual role and grouping; no agent assignment | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-70` |
| n4 | AgentTurnExecutor: Receives prompt + charter; not node.Agent | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:935-955` |
| n5 | Peer-review fields: agent + charter; reviewer-specific contract | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:987-1006` |
| n6 | Rubberduck executor: Receives agent + charter; peer-review context | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:987-1006` |
| n7 | Build & Test field: agent; platform executor context | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:961-980` |
| n8 | Build & Test executor: Receives agent context; not role/kind mapping | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:961-980` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.

## Meaningful expansion

```json
{
  "growth_metric": "visible-semantic-canonical-xml-v1",
  "baseline_meaningful_xml": 1540,
  "result_meaningful_xml": 22887,
  "baseline_visible_structures": 6,
  "result_visible_structures": 80,
  "growth_ratio": 14.861688,
  "minimum_ratio": 9.0,
  "invisible_cells": [],
  "off_page_cells": [],
  "duplicate_cells": [],
  "metadata_padding": [],
  "passed": true
}
```

## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | role/kind are render metadata fields | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-70` |
| e1 | n0 -> n2 | Prompt/publish nodes supply worker context fields | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:935-955` |
| e2 | n1 -> n3 | Visual metadata affects presentation, not executing identity | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-70` |
| e3 | n2 -> n4 | Generic worker receives prompt and charter | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:935-955` |
| e4 | n0 -> n5 | Peer-review binding uses agent and charter fields | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:987-1006` |
| e5 | n5 -> n6 | Bind peer-review context to Rubberduck executor | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:987-1006` |
| e6 | n0 -> n7 | Build & Test binding reads agent context | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:961-980` |
| e7 | n7 -> n8 | Bind agent context to platform Build & Test executor | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:961-980` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
