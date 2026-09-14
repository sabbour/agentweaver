# One heartbeat tick, two scopes - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
