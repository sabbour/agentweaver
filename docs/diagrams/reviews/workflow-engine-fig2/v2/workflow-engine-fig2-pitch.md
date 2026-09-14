# Declarative data is not an executor - pitch

A parsed workflow still needs structural and runtime-bindability validation.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Workflow YAML: Authored graph document; start + nodes + edges | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| n1 | Start + typed nodes: Stable node identity; type / gate contracts | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:58-103` |
| n2 | Edges + metadata: Transitions and render fields; role / kind: visual | `apps/Agentweaver.Api/Workflows/WorkflowDefinition.cs:66-103` |
| n3 | Structural checks: References and graph shape; loader-valid definition | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-156` |
| n4 | Runtime classifier: Known node execution kinds; publish -> agent kind | `apps/Agentweaver.Api/Workflows/NodeClassifier.cs:66-100` |
| n5 | Executor bindings: Concrete execution contracts; not metadata inference | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-139` |
| n6 | Start/edge compatibility: Typed transitions must fit; dry-run bindability | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:156-205` |
| n7 | Executable graph: Valid concrete MAF graph; ready to execute | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:95-151` |
| n8 | Binding error: Unsupported node/start/edge; WorkflowBindException | `apps/Agentweaver.Api/Workflows/RunWorkflowGraphBinder.cs:122-205` |

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
