# Guarded standalone merge - pitch

Repository locking precedes CAS; reviewed-tree mismatch and conflicts are not success.

This v2 cycle replaces the earlier strategy without altering its saved evidence or artifacts.
A5 landscape, one uncompressed editable page, pinned draw.io Desktop 31.4.5.
Exactly three existing independent bounded Astra research reports were reconciled; no new researcher was launched.

- `docs/diagrams/reviews/coordinator-internals-fig4/research-coordinator.md`
- `docs/diagrams/reviews/canonical-workflow-invocation/research-workflows.md`
- `docs/diagrams/reviews/team-casting-fig1/research-supporting.md`

## Content and evidence

| Node | Meaning | Evidence |
| --- | --- | --- |
| n0 | Reviewed input: Canonicalize repository path; reviewed source + tree | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:34-44` |
| n1 | Repository lock: Bounded acquisition wait; 5-second wait | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-48` |
| n2 | Repository busy: No acquired lock; LockFailed | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:85-93` |
| n3 | TryStartMerging CAS: Reload if CAS loses; already Merging may proceed | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:50-58` |
| n4 | Guarded Git operation: Reviewed tree into origin; while holding lock | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:98-102` |
| n5 | Merged: Persist commit and status; best-effort cleanup | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:106-136` |
| n6 | Blocked / conflict: Blocked: restore review; conflict: MergeFailed | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:138-171` |
| n7 | Internal error: Filtered exception handling; revert / internal error | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:178-185` |
| n8 | Release acquired lock: Every acquired-lock exit; finally | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:187-190` |

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
