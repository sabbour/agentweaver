# Draft, decide and finalize - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
