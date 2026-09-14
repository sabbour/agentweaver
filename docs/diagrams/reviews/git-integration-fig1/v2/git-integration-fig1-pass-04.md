# Isolated candidates, guarded merge - pass-04

Runs edit isolated candidates; approval names a tree, not permission to bypass Git guards.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Originating branch: Resolve starting commit; branch tip | `apps/Agentweaver.Api/Git/WorktreeManager.cs:130-165` |
| n1 | Run branch: Deterministic branch name; agentweaver/{runId} | `apps/Agentweaver.Api/Git/WorktreeManager.cs:77-191` |
| n2 | Isolated worktree: Agent edits candidate files; not origin checkout | `apps/Agentweaver.Api/Git/WorktreeManager.cs:145-191` |
| n3 | Capture changes: Stage non-ignored changes; no empty commit | `apps/Agentweaver.Api/Git/WorktreeManager.cs:676-730` |
| n4 | Candidate tree: Committed content identity; tree SHA | `apps/Agentweaver.Api/Git/WorktreeManager.cs:685-701` |
| n5 | Full diff: Compare branch-tip trees; origin vs candidate | `apps/Agentweaver.Api/Git/WorktreeManager.cs:800-810` |
| n6 | Approved identity: Expected tree must match; expectedTreeHash | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1902-1919` |
| n7 | Guarded merge: Containment + origin safety; checked-out or ref-only | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1922-1953` |
| n8 | Merge outcome: Advance, block or conflict; dirty origin protected | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1991-2039` |

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
| e0 | n0 -> n1 | Create a run branch from resolved origin tip | `apps/Agentweaver.Api/Git/WorktreeManager.cs:130-191` |
| e1 | n1 -> n2 | Provision isolated worktree for the run branch | `apps/Agentweaver.Api/Git/WorktreeManager.cs:145-191` |
| e2 | n2 -> n3 | Capture changed non-ignored files | `apps/Agentweaver.Api/Git/WorktreeManager.cs:676-730` |
| e3 | n3 -> n4 | Capture returns candidate tree identity | `apps/Agentweaver.Api/Git/WorktreeManager.cs:685-701` |
| e4 | n4 -> n5 | Compute full candidate-versus-origin tree diff | `apps/Agentweaver.Api/Git/WorktreeManager.cs:800-810` |
| e5 | n5 -> n6 | Review the candidate content whose tree is guarded | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1902-1919` |
| e6 | n6 -> n7 | Merge requires matching reviewed tree | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1902-1953` |
| e7 | n7 -> n8 | Guarded Git operation produces success, block or conflict | `apps/Agentweaver.Api/Git/WorktreeManager.cs:1991-2039` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
