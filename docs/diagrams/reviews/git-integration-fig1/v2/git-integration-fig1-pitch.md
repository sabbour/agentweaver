# Isolated candidates, guarded merge - pitch

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

## Skeleton contract

The three macro states form a complete answer at the selected scope, not a partial excerpt.
Conditional outcomes are named inside the macro state or edge. Pass 1 expands those states into their
source-backed actors, decisions, durable states and routes; it does not add unrelated content.
The full evidence model was established before the skeleton. Its measured baseline is frozen after inspection.

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. All three macro states and both directed connectors were inspected in the seven A5 contact sheets and fourteen lossless original-pixel pairs. Labels wrap within cards; the selected scope and conditional outcomes are complete. Freeze this skeleton before structural expansion.
