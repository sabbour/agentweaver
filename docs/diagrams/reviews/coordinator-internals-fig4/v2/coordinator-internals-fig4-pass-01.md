# Collective assembly and review - pass-01

RED parks durably for a human. REVISE enters explicit steering, not RaiBlocked.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Claim + eligibility: No partial failed plan; awaiting -> assembling | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:851-913` |
| n1 | Integration snapshot: Ordered child branches; branch / tree / diff | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:910-981` |
| n2 | Applicable gates: Workflow-defined ordering; non-code: omit build | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1586-1676` |
| n3 | Gate outcomes: Pass: next; REVISE: steer; RAI RED: human park | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1109-1194` |
| n4 | Normal human gate: Persist request, then wait; approve: next gates | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1198-1464` |
| n5 | Safety / budget park: Durable human escalation; in_review / awaiting | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2845-2958` |
| n6 | Explicit steering: In-place, fresh or advisory; Proceed: human park | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2232-2397` |
| n7 | Recovered review: Use saved branch and tree; no routine rebuild | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1321-1409` |
| n8 | Approved completion: Lock, merge, then Scribe; Scribe error: nonfatal | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1678-1852` |

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
  "baseline_meaningful_xml": 1519,
  "result_meaningful_xml": 25566,
  "baseline_visible_structures": 6,
  "result_visible_structures": 86,
  "growth_ratio": 16.83081,
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
| e0 | n0 -> n1 | Eligible children provide ordered integration inputs | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-981` |
| e1 | n1 -> n2 | Persist aggregate artifacts before resolving checks | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:981-997` |
| e2 | n2 -> n3 | Run applicable authored gate executors | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1023-1194` |
| e3 | n3 -> n2 | Successful check continues the resolved gate list | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1148-1194` |
| e4 | n2 -> n4 | An authored human gate persists its request | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1198-1234` |
| e5 | n4 -> n2 | Normal human-gate approval continues remaining gates | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1443-1464` |
| e6 | n3 -> n5 | Collective RED parks at durable human review | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:3757-3795` |
| e7 | n3 -> n6 | Autonomous request-changes enters steering | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1139-1192` |
| e8 | n4 -> n6 | Human request-changes enters scoped steering | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1467-1482` |
| e9 | n6 -> n5 | Exhaustion or Proceed opens durable human review | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2372-2388` |
| e10 | n6 -> n1 | Explicit revision effects precede subsequent assembly | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:2334-2370` |
| e11 | n5 -> n7 | Persisted human-review metadata supports recovery | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1321-1357` |
| e12 | n7 -> n8 | Recovered persisted approval completes using saved artifacts | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1388-1409` |
| e13 | n2 -> n8 | Finished authored gates reach approved completion | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:1678-1698` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven A5-approximation sheets and all fourteen lossless original-pixel pairs. Checked every diagram for reading order, wrapping, endpoints, crossings and evidence associations; specific remaining defects are noted below. Dense central connector detours make pass/RED association difficult; preserve explicit segment waypoints.
