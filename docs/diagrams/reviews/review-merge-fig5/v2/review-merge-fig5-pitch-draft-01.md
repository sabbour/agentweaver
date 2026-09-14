# Review API decision paths - pitch

Authorize first. Deliver through the right path. Lock before any merge CAS.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Caller + access: Project contributor check; legacy: pending owner | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:862-877` |
| n1 | Reviewable state?: Inspect status + pending; awaiting_review | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:879-891` |
| n2 | Replay or conflict: Matching terminal: reuse; otherwise: 409 | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:879-891` |
| n3 | Live pending: Changes / decline use CAS; approve: no merge CAS | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:963-984` |
| n4 | Deferred pending: Persist the decision first; then status transition | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:933-957` |
| n5 | No live / no pending: Validate direct approval; changes: 409 | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:985-1001` |
| n6 | Consume + deliver: Send workflow response; live continuation | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:983-1053` |
| n7 | Repository lock: Only on reaching merge; lock before CAS | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-62` |
| n8 | Merge CAS + Git: Guard reviewed tree input; release lock on exit | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:82-190` |

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

Pending actual PNG inspection; not certified by generation/export alone.
