# From workflow definition to execution - pitch

Binding, checkpointed execution and observation are distinct responsibilities.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Selected definition: Concrete authored workflow; no policy overlay | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1495-1517` |
| n1 | Node classification: Types and gate contracts; not fragile node IDs | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-105` |
| n2 | Factory + binder: Executors and typed edges; fail closed if unsupported | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| n3 | Executable MAF graph: Run the bound workflow; not always default chain | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n4 | Checkpoint store: Provider-aware persistence; not universally files | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1386-1439` |
| n5 | Default example: Merge -> push-pr -> Scribe; authored default only | `apps/Agentweaver.Api/Workflows/DefaultWorkflowTemplate.cs:42-161` |
| n6 | Watch loop: Consume execution updates; supervised observer | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-335` |
| n7 | Pending + run state: Durable request/status state; typed terminal projection | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:363-377` |
| n8 | Workflow-step events: Progress for observers; not runtime control | `apps/Agentweaver.Api/Runs/RunWatchLoopService.cs:313-409` |

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
