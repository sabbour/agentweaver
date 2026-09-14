# Dispatch frontier and observation - pass-02

Quiescence triggers an eligibility check; it does not prove every child succeeded.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Status + dependency map: Use durable subtask state; pending is not ready | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n1 | Ready frontier: All prerequisites satisfied; assemble_ready / done | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| n2 | Retry + scope checks: Retry time and conflicts; defer if ineligible | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| n3 | Dispatch child: Start isolated execution; observe launched run | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:422-427` |
| n4 | Observe result: Nonterminal: keep watching; waits are not failure | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1827-1880` |
| n5 | Apply completion: Persist result, recompute; terminal classification | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:459-537` |
| n6 | Bounded recovery: Stalled child: fresh retry; not immediate failure | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:463-474` |
| n7 | Quiescent frontier: No work or eligible retry; not success proof | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:430-455` |
| n8 | Assembly admission: Check terminal eligibility; failed plan may block | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-908` |

## Symbols and credits

Repository Fluent template/library/design-system and current React theme are visual references, not runtime evidence.
Process, decision, event and document icons are native:flowchart; cylinders native:database;
people native:uml; component symbols native:uml. Product-specific chrome is custom:agentweaver.
Built-in diagrams.net symbols use the existing Desktop Apache-2.0 distribution. No external logos or image payloads.
Warm canvas/cards, 16px rounding, 5px accents, restrained shadows, Segoe UI, Cascadia Code metadata,
fixed pill tones, tiered named groups and orthogonal rounded labeled connectors are retained.


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Compute dependency-satisfied pending frontier | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:63-85` |
| e1 | n1 -> n2 | Check retry eligibility and conflicting scopes | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| e2 | n2 -> n3 | Admitted subtask starts child execution | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:422-427` |
| e3 | n3 -> n4 | Dispatcher observes launched child | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1827-1880` |
| e4 | n4 -> n5 | Apply classified terminal outcome | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:459-537` |
| e5 | n4 -> n6 | Recover a stalled child within bounds | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:463-474` |
| e6 | n6 -> n1 | Fresh recovery re-enters frontier processing | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:463-474` |
| e7 | n5 -> n1 | Recompute readiness after applying a result | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:536-537` |
| e8 | n1 -> n7 | No in-flight or eligible work becomes quiescent | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:430-455` |
| e9 | n7 -> n8 | Handoff still checks assembly eligibility | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:736-761` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Opened all seven print sheets and fourteen original-pixel pairs. The Review API heading collision and resilient budget title wrap are corrected. Existing content and Fluent styling remain intact; residual close-lane issues are recorded per diagram.

Correction-only pass: no added content or reopened composition. Existing-label fitting, native segment-routing correction and any heading repair are recorded in corrections-pass-02.json.
