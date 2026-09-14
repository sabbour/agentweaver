# Dispatch frontier and observation - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
