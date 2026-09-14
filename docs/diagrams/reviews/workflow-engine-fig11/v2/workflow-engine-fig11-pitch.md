# Generate a draft, not a saved workflow - pitch

One correction attempt reuses loader and binder validation; saving is a separate action.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | User description: Request workflow generation; not a save command | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n1 | Server prompt + model: Constrained generation request; authored draft response | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n2 | Clean draft + ID: Normalize returned content; built-in edit: new ID | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n3 | Loader validation: Parse workflow definition; same validation pipeline | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n4 | Binder validation: Check runtime bindability; not syntax alone | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:88-112` |
| n5 | Valid draft response: Return YAML to caller; not persisted/applied | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n6 | Error + one retry: Ask model to correct error; exactly one correction | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |
| n7 | Validate correction: Same cleanup/loader/binder; second pass only | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-112` |
| n8 | Generation exception: Correction still invalid; explicit failure | `apps/Agentweaver.Api/Workflows/CopilotWorkflowGenerator.cs:54-85` |

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
