# One signal, four explicit effects - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
