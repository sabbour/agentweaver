# Persist the plan before dispatch - pitch

Confirmation, selection and decomposition precede durable child dispatch.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Submitted request: Goal + caller context; manual or pickup | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| n1 | Confirmation boundary: Manual or unattended policy; autopilot-dependent | `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs:370-408` |
| n2 | Existing plan?: Reuse persisted plan; avoid decomposing twice | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:133-150` |
| n3 | Select workflow: Available definitions; explicit choices honored | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-160` |
| n4 | Decompose + validate: Outcome-complete work; compatibility check | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:157-182` |
| n5 | Persist WorkPlan: Subtasks and dependencies; workflow identity | `apps/Agentweaver.Api/Coordinator/CoordinatorOrchestratorExecutor.cs:230-249` |
| n6 | Ready frontier: Dependency satisfaction; pending -> ready work | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-427` |
| n7 | Dispatch children: Observe classified outcomes; isolated child runs | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:387-537` |
| n8 | Collective handoff: After child supervision; assembly eligibility | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:798-799` |

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
