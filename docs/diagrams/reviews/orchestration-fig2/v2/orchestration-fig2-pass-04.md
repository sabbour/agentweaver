# A dependency DAG, not agent chat - pass-04

Illustrative tasks A-D show readiness; only assemble-ready/completed prerequisites count.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Confirmed OutcomeSpec: Intent before decomposition; confirmed status | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:119-150` |
| n1 | Persisted WorkPlan: Tasks + dependency edges; selected workflow | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:230-249` |
| n2 | Readiness rule: Every predecessor satisfied; assemble_ready / done | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| n3 | Example root A: No prerequisites; illustrative, not fixed | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n4 | Example root B: No prerequisites; parallel with A | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n5 | Satisfied roots: Not merely terminal; failure does not unlock | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-45` |
| n6 | Example dependent C: Depends on A; illustrative edge | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n7 | Example dependent D: Depends on A and B; illustrative join | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n8 | Collective handoff: Recheck aggregate eligibility; quiescence != success | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:94-104` |

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
| e0 | n0 -> n1 | Persist outcome-complete plan and dependencies | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:230-249` |
| e1 | n1 -> n2 | Evaluate durable prerequisite satisfaction | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:63-85` |
| e2 | n2 -> n3 | Illustrative root without prerequisites is ready | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:63-85` |
| e3 | n2 -> n4 | Another illustrative root can be ready concurrently | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:63-85` |
| e4 | n3 -> n6 | Illustrative C depends on satisfied A | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| e5 | n3 -> n5 | A contributes to illustrative all-predecessor join | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| e6 | n4 -> n5 | B contributes to illustrative all-predecessor join | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| e7 | n5 -> n7 | Illustrative D requires A and B satisfied | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| e8 | n6 -> n8 | Settled plan reaches aggregate handoff check | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:94-104` |
| e9 | n7 -> n8 | Dependent completion contributes to quiescent handoff | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:94-104` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
