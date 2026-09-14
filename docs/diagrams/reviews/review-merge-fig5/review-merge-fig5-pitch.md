# Review API arbitration pitch

Status: inspected pitch, not a final or a completed iteration.

Produced while continuing the orchestration shard after sending the first representative
collective-review sample path. No published image/source/hash was changed.

## Contract and research

Audience: implementers distinguishing HTTP review arbitration, durable delivery and Git
merge authorization. Takeaway: pending consumption, run-status CAS and repository locking
belong to different paths and cannot be drawn as one unconditional happy-path sequence.

One editable, uncompressed A5 portrait page: 559 by 794 draw.io pixels, page scale 1.
Stable future publication path: `docs/diagrams/review-merge-fig5.png`.
Consumer pages: `review-merge.md` and `orchestration.md`.

Uses the existing three independently launched bounded GPT-6 Astra research reports;
no additional research agent was launched:

- `../coordinator-internals-fig4/research-coordinator.md`: exact endpoint/CAS/merge paths.
- `../canonical-workflow-invocation/research-workflows.md`: execution/binding boundaries.
- `../team-casting-fig1/research-supporting.md`: reviewed tree identity and Git authority.

These are implementation/test research, not old-diagram evidence. The pitch is a
proposed visual organization of verified facts. Execution combines the workflow and
merge coordinator in one abbreviated lane; it does not assert that they are one class.
The run-state lane represents pending/status operations, not one atomic database
transaction or ownership of the repository lock.

## Node and connector evidence

E = `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs`;
M = `apps/Agentweaver.Api/Runs/MergeCoordinator.cs`.
All source spans are expanded in the coordinator research report.

| ID | Participant or message | Evidence |
| --- | --- | --- |
| caller | Caller supplies the review action | E:862-877,927-931 |
| api | Endpoint performs access/status/pending arbitration | E:862-891,933-984 |
| state | Durable run status and pending review state | E:879-891,933-984 |
| runtime | Live workflow response and later merge coordination | E:1049-1053; M:46-62,82-101 |
| request | Caller -> API: decision | E:862-877,927-931 |
| read | API -> state: run lookup | E:862-877 |
| state-result | State -> API: status/pending lookup results | E:879-891 |
| persist | API -> state: deferred decision persistence first | E:933-957 |
| cas | API -> state: local changes/decline transition | E:969-984 |
| consume | API -> state: consume pending request | E:963-984 |
| respond | API -> execution: workflow response | E:1049-1053 |
| merge-cas | Merge execution -> state: merge CAS after repository lock | M:46-62,82-101 |

ALT A and ALT B are alternatives, not sequential phases every request executes.
The CAS message in ALT B is conditional on request-changes/decline; approval skips
that message. Matching terminal replay can return success (E:879-886).
Project contributor access is checked before proceeding; projectless pending-owner
defense is additional (E:875,1008-1011). Missing pending with a live workflow conflicts.
No local workflow and no pending request reaches the direct fallback (E:985-1001):
direct request-changes returns 409 (E:2966-2979), while direct approval checks artifacts
and reviewed tree before shared merge coordination (E:2988-3022).

## Visual language, native notation and credits

Started from the repository Fluent template envelope and consulted the committed
Fluent library/design-system assets already inspected for this shard.
Warm canvas, near-white rounded participant cards, 5px semantic accents, warm shadows,
semantic badge tones, Segoe UI, and tiered fragment/note backgrounds are retained.
Current product theme reference remains `apps/web/src/theme.ts:1-104`.

Native symbols: caller `native:uml` actor; API/execution `native:flowchart` process;
run state `native:database` cylinder. Dashed lifelines and horizontal open-head
messages use native draw.io sequence primitives (`native:uml`).
Product-specific participant chrome is `custom:agentweaver`. The dashed return message
is a UML reply, not a marigold revision rail. No crossings imply split/merge junctions.
No external image/logo or embedded raster was added. Native primitives are supplied
by pinned draw.io Desktop 31.4.5, using the existing Apache-2.0 distribution.

## Actual PNG inspection and iteration handoff

Exported with official Desktop 31.4.5, border 16, scale 2.
Opened both `review-merge-fig5-pitch.png` and its
`review-merge-fig5-pitch-print.png` 559px-wide A5 screen approximation.
The participant labels, distinct alternatives, arrow directions and merge-lock
ordering are visible at both scales. No raw parser IDs or dangling messages appear.
No physical print inspection is claimed.

Pass-1 issues: dashed lifelines remain visible through several explanatory text
labels; use proper note backgrounds. Make conditional local CAS visually stronger
so approval cannot be read as traversing that CAS. Complete metadata hierarchy and
review the smallest labels for print. Native UML message endpoint identities should
be made durable editor connections rather than relying only on aligned coordinates.

The pitch has no post-pitch pass yet. No 9x growth, four-pass completion or final
every-arrow trace is claimed. The existing published JSON-derived figure remains
legacy and is not certified by this new draft.

## Initial-export checker correction

The first structural check flagged every message as off-page because the checker
treats all nested `mxPoint` values, including relative label offsets, as absolute
page coordinates. Its rejected value was the label's `(0, -9)` offset, not the
positive message endpoint coordinates. The actual PNG labels were inside the page.
No pipeline/skill change was made.

The corrected pitch uses standard relative edge-label `mxGeometry.y` instead of a
negative `mxPoint` offset. Original source and PNG are preserved independently;
`pitch-manifest.json` selects the distinct `-pitch-corrected` artifacts. This
pre-iteration geometry correction is not a counted post-pitch pass, a changed
growth threshold, or a reduced semantic baseline.

Opened the corrected PNG and its print-scale derivative after re-export: the
message-label placement is preserved. The unmodified checker now reports 54 visible
structures, 15,263 meaningful units, and zero invisible, off-page, duplicate or
metadata-padding defects. `pitch-xml-validation.json` preserves that result.
