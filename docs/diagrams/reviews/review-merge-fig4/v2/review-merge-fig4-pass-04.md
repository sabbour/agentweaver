# Guarded standalone merge - pass-04

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


## Connector evidence

| ID | Source -> target | Relationship | Evidence |
| --- | --- | --- | --- |
| e0 | n0 -> n1 | Validate/canonicalize path before lock acquisition | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:34-48` |
| e1 | n1 -> n2 | Failed lock acquisition returns repository busy | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:85-93` |
| e2 | n1 -> n3 | Acquire lock before merge-status CAS | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:46-50` |
| e3 | n3 -> n4 | CAS success or already-Merging reload may continue while locked | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:50-102` |
| e4 | n4 -> n5 | Successful Git operation persists merged result | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:106-136` |
| e5 | n4 -> n6 | Blocked/conflict cases retain their distinct recovery states | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:138-171` |
| e6 | n4 -> n7 | Handled non-InvalidOperationException maps to internal error | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:178-185` |
| e7 | n5 -> n8 | Release lock after successful merge | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:187-190` |
| e8 | n6 -> n8 | Release lock after block or conflict | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:187-190` |
| e9 | n7 -> n8 | Release lock after handled internal error | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:187-190` |
| e10 | n3 -> n8 | Losing CAS without already-Merging state releases lock | `apps/Agentweaver.Api/Runs/MergeCoordinator.cs:51-58` |

## Inspection

Actual exported PNG inspected at A5 screen approximation and enlarged detail. Individually opened every final diagram PNG and all seven final A5-approximation sheets. Traced each evidenced edge against the final PNG and XML: actor/state endpoints, direction, arrowhead, label association, orthogonal path, native crossing bridges and semantic return rails. No remaining defects; no pass-4 edits were necessary. Independently re-exported all final images with verified official Desktop 31.4.5.

Correction-only pass: no added content or reopened composition. No permitted defect required an edit.

Every listed connector was traced individually: source, target, direction, endpoint, label, route, crossings and junction meaning.
