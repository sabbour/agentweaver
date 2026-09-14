# From workflow definition to execution - pass-01

Binding, checkpointed execution and observation are distinct responsibilities.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Selected definition: Concrete authored workflow; no policy overlay | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1495-1517` |
| n1 | Node classification: Types and gate contracts; not fragile node IDs | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-105` |
| n2 | Factory + binder: Executors and typed edges; fail closed if unsupported | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| n3 | Executable MAF graph: Run the bound workflow; not always default chain | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n4 | Checkpoint store: Provider-aware persistence; not universally files | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n5 | Default example: Merge -> push-pr -> Scribe; authored default only | `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:42-161` |
| n6 | Watch loop: Consume execution updates; supervised observer | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-335` |
| n7 | Pending + run state: Durable request/status state; typed terminal projection | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:363-377` |
| n8 | Workflow-step events: Progress for observers; not runtime control | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-409` |

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
  "baseline_meaningful_xml": 1531,
  "result_meaningful_xml": 22922,
  "baseline_visible_structures": 6,
  "result_visible_structures": 80,
  "growth_ratio": 14.971914,
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
| e0 | n0 -> n1 | Classify authored node types and gates | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-105` |
| e1 | n1 -> n2 | Bind classified definitions to runtime contracts | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| e2 | n2 -> n3 | Run the resulting executable graph | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| e3 | n3 -> n4 | Execution uses provider-aware checkpoint persistence | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| e4 | n0 -> n5 | Default definition includes push-pr between merge and Scribe | `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:42-161` |
| e5 | n3 -> n6 | Runtime updates are consumed by watcher | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-335` |
| e6 | n6 -> n7 | Watcher persists pending requests and status projection | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:363-377` |
| e7 | n6 -> n8 | Watcher exposes workflow-step events | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-409` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
