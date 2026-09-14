# Review API decision paths - pass-04

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


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Fetch and authorize before arbitration | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:862-891` |
| e1 | n1 -> n2 | Terminal replay or invalid/live-without-pending response | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:879-891` |
| e2 | n1 -> n3 | Local pending review path | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:963-984` |
| e3 | n1 -> n4 | Persist a decision when local workflow is absent | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:933-957` |
| e4 | n1 -> n5 | No local workflow and no pending uses direct fallback | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:985-1001` |
| e5 | n3 -> n6 | CAS where required, consume pending, send response | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:969-1053` |
| e6 | n6 -> n7 | Workflow continuation eventually reaches guarded merge | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-101` |
| e7 | n5 -> n7 | Validated direct approval uses shared merge coordinator | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:2988-3022` |
| e8 | n7 -> n8 | Repository lock precedes merge CAS and Git operation | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-101` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
