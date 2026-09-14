# Child execution and dependency progress - pass-01

Children publish typed results and branch content; collective review belongs to the parent.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Pending subtasks: Persisted dependency map; not automatically ready | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n1 | Ready frontier: All predecessors satisfied; not any terminal result | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| n2 | Isolated child: Launch agent execution; separate checkout | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:906-929` |
| n3 | Agent result: Typed conditional output; no child RAI executor | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| n4 | Assemble-ready: Successful child content; satisfies dependents | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-45` |
| n5 | Typed turn failure: Does not satisfy dependents; no per-child review | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| n6 | Published branch: Authoritative committed tip; not shared mutable files | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:2715-2731` |
| n7 | Dependency base: Rebuild prerequisite content; new isolated dependent | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1327-1435` |
| n8 | Parent assembly check: Quiescence plus eligibility; collective gates later | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-908` |

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
  "baseline_meaningful_xml": 1551,
  "result_meaningful_xml": 23833,
  "baseline_visible_structures": 6,
  "result_visible_structures": 82,
  "growth_ratio": 15.366215,
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
| e0 | n0 -> n1 | Compute dependency-satisfied frontier | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:63-85` |
| e1 | n1 -> n2 | Ready subtask launches isolated child | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:906-929` |
| e2 | n2 -> n3 | Trimmed child graph consumes typed agent result | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| e3 | n3 -> n4 | Successful output is assemble-ready | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| e4 | n3 -> n5 | Typed failure follows child failure terminal | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| e5 | n4 -> n6 | Successful child publishes authoritative branch content | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:2715-2731` |
| e6 | n6 -> n7 | Prerequisite branches form dependent starting content | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1327-1435` |
| e7 | n7 -> n1 | Satisfied prerequisites unlock additional frontier work | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| e8 | n4 -> n8 | Successful children contribute to aggregate eligibility | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-908` |
| e9 | n5 -> n8 | Terminal failed children can block aggregate eligibility | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-908` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
