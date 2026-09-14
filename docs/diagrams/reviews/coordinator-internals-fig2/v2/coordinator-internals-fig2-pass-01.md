# Draft, decide and finalize - pass-01

DefineOutcome confirms intent before decomposition; decline finalizes without work.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Submitted intent: DefineOutcome mode; goal + context | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:103-117` |
| n1 | Draft executor: Prepare OutcomeSpec; coordinator-draft | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:103-117` |
| n2 | Confirmation port: Request human decision; confirmation-gate | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:158-168` |
| n3 | Revise: Update draft input; same draft loop | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:129-138` |
| n4 | Confirm: Record affirmative intent; confirmed-by user | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:612-631` |
| n5 | Decline: No decomposition; declined status | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:148-150` |
| n6 | Finalize spec: Persist decision status; confirm or decline | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:612-631` |
| n7 | Confirmed status?: Guard orchestration; not status-blind | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:142-170` |
| n8 | Plan or pass through: Confirmed: decompose; declined: return | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:142-170` |

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
  "baseline_meaningful_xml": 1515,
  "result_meaningful_xml": 23657,
  "baseline_visible_structures": 6,
  "result_visible_structures": 82,
  "growth_ratio": 15.615182,
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
| e0 | n0 -> n1 | Build a draft from submitted intent | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:103-117` |
| e1 | n1 -> n2 | Draft reaches confirmation request port | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:158-160` |
| e2 | n2 -> n3 | Revision decision supplies revised draft input | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:129-138` |
| e3 | n3 -> n1 | Revision returns to draft | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:160-164` |
| e4 | n2 -> n4 | Confirmation selects finalization | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:164-168` |
| e5 | n2 -> n5 | Decline also selects finalization | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:164-168` |
| e6 | n4 -> n6 | Persist confirmed spec and attribution | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:612-631` |
| e7 | n5 -> n6 | Persist declined spec without confirmation attribution | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:612-631` |
| e8 | n6 -> n7 | Orchestrator checks confirmed status | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:142-170` |
| e9 | n7 -> n8 | Only confirmed intent is decomposed | `apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:142-170` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
