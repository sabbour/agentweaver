# Review authorizes; merge still guards - pitch

A review-bearing standalone workflow declares its gates; approval alone does not edit Git.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Selected definition: Bind the authored graph; no injected project policy | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:1495-1517` |
| n1 | Producer output: Capture tree and diff; reviewable candidate | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:927-931` |
| n2 | Declared review gate: Only when workflow includes it; not universal to all graphs | `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:822-835` |
| n3 | Request changes: Return feedback to execution; revision path | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:969-975` |
| n4 | Approve: Allow workflow continuation; not direct file mutation | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:963-967` |
| n5 | Decline: Persist declined terminal; no merge authorization | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:977-980` |
| n6 | Continuation: Deliver workflow response; remaining authored nodes | `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:1049-1053` |
| n7 | Merge coordinator: Lock and reviewed-tree guard; CAS before Git operation | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-102` |
| n8 | Actual merge result: Merged, blocked or conflict; internal errors distinct | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:106-185` |

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
