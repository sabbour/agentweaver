# Rejected work keeps useful context - pass-01

A steering decision chooses the effect; rejection does not always rotate the author.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Gate request-changes: Structured target-file hints; not prose-inferred blame | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2246-2302` |
| n1 | Implicated + dependent: Rebuild closure without blame; structured TARGET_FILES | `apps/Agentweaver.Api/Coordinator/AssemblyPlanning.cs:257-324` |
| n2 | Signal + decision: Persist explicit direction; accumulated context | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2290-2321` |
| n3 | In-place revision: Same author and session; no reset-to-pending | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2334-2350` |
| n4 | Fresh dispatch: Scoped author selection; handoff with context | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2352-2370` |
| n5 | No alternate author: Context permits same author; bounded conscious fallback | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2549-2585` |
| n6 | Human escalation: No context or budget left; durable review request | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| n7 | Human decision: Approve, change or decline; no wall-clock timeout | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1388-1485` |
| n8 | Fresh autonomous budget: Only human changes reset it; no human-round-trip cap | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2264-2286` |

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
  "baseline_meaningful_xml": 1549,
  "result_meaningful_xml": 24377,
  "baseline_visible_structures": 6,
  "result_visible_structures": 83,
  "growth_ratio": 15.73725,
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
| e0 | n0 -> n1 | Compute implicated contributors and dependent closure | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2246-2302` |
| e1 | n1 -> n2 | Submit scoped signal and persist steering decision | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2290-2321` |
| e2 | n2 -> n3 | Resumable direction keeps author/session in place | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2334-2350` |
| e3 | n2 -> n4 | Fresh direction selects a scoped dispatch effect | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2352-2370` |
| e4 | n4 -> n5 | No eligible alternate triggers bounded fallback decision | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2549-2585` |
| e5 | n5 -> n4 | Useful context permits conscious same-author fresh dispatch | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2549-2560` |
| e6 | n5 -> n6 | Without actionable context open durable human review | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2563-2585` |
| e7 | n2 -> n6 | Budget exhaustion or Proceed escalates durably | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2372-2388` |
| e8 | n6 -> n7 | Durable escalation waits for human action | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| e9 | n7 -> n8 | Human request-changes resets autonomous budgets | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2264-2286` |
| e10 | n8 -> n2 | Human feedback permits a fresh bounded decision cycle | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2271-2321` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below. Fresh autonomous budget wraps into the subtitle; fit the existing title.
