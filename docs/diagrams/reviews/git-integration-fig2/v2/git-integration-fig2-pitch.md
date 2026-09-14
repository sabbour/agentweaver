# Child content and integration bases - pitch

Published branch content crosses child boundaries; a shared mutable checkout does not.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Origin tip: Reset integration branch; authoritative repository | `apps/Agentweaver.Api/Git/WorktreeManager.cs:850-878` |
| n1 | Published children: Isolated execution checkouts; committed branches | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:2715-2731` |
| n2 | Ordered accumulator: Skip empty or contained tips; caller supplies order | `apps/Agentweaver.Api/Git/WorktreeManager.cs:881-905` |
| n3 | Merge conflict?: A merge base is required; not always terminal | `apps/Agentweaver.Api/Git/WorktreeManager.cs:906-954` |
| n4 | Later-child overlay: Apply delta from merge base; record auto-resolution | `apps/Agentweaver.Api/Git/WorktreeManager.cs:906-958` |
| n5 | Unresolved conflict: No merge base: fail assembly; no partial success | `apps/Agentweaver.Api/Git/WorktreeManager.cs:918-924` |
| n6 | Integration snapshot: Ref, tree, diff, resolutions; content contract | `apps/Agentweaver.Api/Git/WorktreeManager.cs:971-986` |
| n7 | Dependent-child base: Verify prerequisite reachability; isolated new checkout | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1327-1435` |
| n8 | Final aggregate review: Authored checks, reviewed tree; then guarded merge | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:981-1194` |

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
