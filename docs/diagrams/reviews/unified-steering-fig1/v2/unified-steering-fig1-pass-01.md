# One signal, four explicit effects - pass-01

Durable decisions choose resume, fresh dispatch, human escalation or advisory continuation.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Gate feedback: Implemented assembly sources; structured scope | `apps/Agentweaver.Api/Coordinator/SteeringSignal.cs:93-109` |
| n1 | SteeringSignal: Normalize reason and targets; one decision contract | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2289-2319` |
| n2 | Persist directive: Received event is visible; queued / durable | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:201-259` |
| n3 | Bounded decider: Budget + attempt resumability; human-only budget reset | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:31-52` |
| n4 | Persist decision: Decision event follows commit; explicit direction | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:201-259` |
| n5 | In-place steer: Keep author and session; attempt-specific proof | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2333-2350` |
| n6 | Fresh dispatch: Conscious fresh execution; bounded same-author fallback | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2352-2370` |
| n7 | Durable human park: Proceed / exhausted budget; not a failure terminal | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| n8 | Advisory continuation: No child-state reset; separate from in-place | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2392-2397` |

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
  "baseline_meaningful_xml": 1554,
  "result_meaningful_xml": 23029,
  "baseline_visible_structures": 6,
  "result_visible_structures": 80,
  "growth_ratio": 14.819176,
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
| e0 | n0 -> n1 | Convert scoped gate feedback to SteeringSignal | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2289-2319` |
| e1 | n1 -> n2 | Persist directive and received event | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:201-259` |
| e2 | n2 -> n3 | Apply deterministic bounded steering policy | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:31-52` |
| e3 | n3 -> n4 | Persist decision and publish decision event | `apps/Agentweaver.Api/Coordinator/CoordinatorSteeringDecider.cs:201-259` |
| e4 | n4 -> n5 | In-place effect preserves author/session without reset | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2333-2350` |
| e5 | n4 -> n6 | Fresh effect chooses a new execution with context | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2352-2370` |
| e6 | n4 -> n7 | Proceed applies durable human escalation | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| e7 | n4 -> n8 | Advisory continuation performs no child reset | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2392-2397` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below.
