# One heartbeat tick, two scopes - pass-04

Pickup runs per project; reconciliation, deferred-spec drain and optional reaping run afterward.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Heartbeat tick: Enumerate projects; failure-isolated sweep | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:96-147` |
| n1 | Active + available?: Skip unavailable projects; per-project admission | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:96-147` |
| n2 | Capped Ready list: Deterministic candidates; per-project limit | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:96-147` |
| n3 | Atomic claim: Reserve coordinator run; competing claim may lose | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:187-240` |
| n4 | Start reserved run: Carry backlog origin; confirmation policy applies | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:240` |
| n5 | End project loop: Record tick result; not an inner-loop sweep | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:151-166` |
| n6 | Reconcile once: Repair durable supervision; after all projects | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:151-166` |
| n7 | Drain spec decisions: Recover orphaned decisions; durable OutcomeSpec | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:169-187` |
| n8 | Optional pod reaper: Every N ticks when enabled; throttled cleanup | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:190-210` |

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
| e0 | n0 -> n1 | Evaluate active/available projects in project loop | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:96-147` |
| e1 | n1 -> n2 | Read bounded deterministic Ready candidates | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:96-147` |
| e2 | n2 -> n3 | Attempt atomic claim and run reservation | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:187-240` |
| e3 | n3 -> n4 | Start only successfully reserved coordinator run | `apps/Agentweaver.Api/Coordinator/CoordinatorPickupService.cs:240` |
| e4 | n4 -> n5 | Finish all project pickup before tick-level work | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:151-166` |
| e5 | n5 -> n6 | Run one reconciliation sweep after project loop | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:151-166` |
| e6 | n6 -> n7 | Drain orphaned OutcomeSpec decisions next | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:169-187` |
| e7 | n7 -> n8 | Run optional throttled pod reaper | `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs:190-210` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
