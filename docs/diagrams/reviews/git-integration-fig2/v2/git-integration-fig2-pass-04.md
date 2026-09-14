# Child content and integration bases - pass-04

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


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n2 | Initialize accumulator from origin | `apps/Agentweaver.Api/Git/WorktreeManager.cs:850-878` |
| e1 | n1 -> n2 | Accumulate caller-ordered branch inputs | `apps/Agentweaver.Api/Git/WorktreeManager.cs:881-905` |
| e2 | n2 -> n3 | Inspect integration conflicts | `apps/Agentweaver.Api/Git/WorktreeManager.cs:906-954` |
| e3 | n3 -> n4 | Merge-base conflict can overlay later-child delta | `apps/Agentweaver.Api/Git/WorktreeManager.cs:906-958` |
| e4 | n3 -> n5 | No merge base yields conflict result | `apps/Agentweaver.Api/Git/WorktreeManager.cs:918-924` |
| e5 | n4 -> n6 | Return aggregate with resolution records | `apps/Agentweaver.Api/Git/WorktreeManager.cs:949-986` |
| e6 | n2 -> n6 | Successful accumulation returns aggregate snapshot | `apps/Agentweaver.Api/Git/WorktreeManager.cs:971-986` |
| e7 | n6 -> n7 | Use integrated prerequisites for a dependent child | `apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:1327-1435` |
| e8 | n6 -> n8 | Final aggregate enters authored collective review | `apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:981-1194` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
