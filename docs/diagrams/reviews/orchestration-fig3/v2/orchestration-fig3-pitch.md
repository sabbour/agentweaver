# Child execution and dependency progress - pitch

Children publish typed results and branch content; collective review belongs to the parent.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Pending subtasks: Persisted dependency map; not automatically ready | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:54-85` |
| n1 | Ready frontier: All predecessors satisfied; not any terminal result | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-85` |
| n2 | Isolated child: Launch agent execution; separate checkout | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:906-929` |
| n3 | Agent result: Typed conditional output; no child RAI executor | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| n4 | Assemble-ready: Successful child content; satisfies dependents | `apps/Agentweaver.Api/Coordinator/SubtaskFrontier.cs:34-45` |
| n5 | Typed turn failure: Does not satisfy dependents; no per-child review | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:788-814` |
| n6 | Published branch: Authoritative committed tip; not shared mutable files | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:2715-2731` |
| n7 | Dependency base: Rebuild prerequisite content; new isolated dependent | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1327-1435` |
| n8 | Parent assembly check: Quiescence plus eligibility; collective gates later | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:873-908` |

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
