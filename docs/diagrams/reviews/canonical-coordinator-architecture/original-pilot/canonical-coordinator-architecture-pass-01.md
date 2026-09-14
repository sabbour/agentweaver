# Pass 1: visual upgrade

Input: inspected pitch PNG and `research-and-contract.md`. The legacy artifact was a
visual reference only. Output is one uncompressed A5-landscape source and actual pinned
draw.io Desktop 31.4.5 PNG (1664 x 1172).

## Growth gate

Metric: `visible-semantic-canonical-xml-v1`.
Baseline: **3,162**; result: **28,462**; ratio: **9.001265x**.
Visible structures: 8 -> 96. Official checker passes with zero invisible, off-page,
duplicate or metadata-padding cells. Comments, whitespace, embedded raster data and
unsupported styling metadata are not counted. No external images are embedded.

Growth comprises six grounded responsibility cards; durable/support and system grouping;
independently editable native notation, card accents, title/subtitle, responsibility
rows, metadata and role pills; source-backed Direct-mode qualification; a five-tone
legend; and nine logical handoffs including state/recovery and the outer revision rail.
The repeated card chrome has a consistent visible function, not duplicate hidden cells.
Preflight measurements and the failed first export are retained in `pass-01-preflight.md`.

## Meaning and symbol audit

Use the research record's six-node model. Added useful relationship detail:

- `e-dispatch-state`: Dispatch -> Durable state, persist subtask/run status.
  `CoordinatorDispatchService.cs:977-989` (under `apps/Agentweaver.Api/Coordinator`).
- `e-state-recovery`: Durable state -> Dispatch, recover persisted plan.
  `CoordinatorReconciler.cs:117-133`; `CoordinatorDispatchService.cs:615-651`;
  `tests/Agentweaver.Tests/Coordinator/CoordinatorLeaseHeartbeatTests.cs:74-119`.

Native symbols: C4 person (human control); UML component (API/service/tools);
UML folder (Ready work collection); flowchart document (spec/plan/records);
flowchart decision (scope/review gates); flowchart predefined process (child/assembly);
database cylinder (actual store); cloud (optional remote AgentHost).
All surrounding product responsibility cards are `custom:agentweaver`.
Library rights and exact sources are in `research-and-contract.md`.

## Actual PNG inspections

Opened the exported PNG enlarged and the 96-DPI print-size raster.
Warm surfaces/ink/strokes, Segoe UI, Consolas metadata, 5 px semantic accents,
rounded near-white cards, shadows, icon/title/subtitle/metadata/badge hierarchy,
group-title treatment and fixed five-tone palette are visible.

All nine arrow directions agree with the grounded model. The central state-write and
recovery routes use packed lanes and a real draw.io bridge at their crossing; no false
junction dots. Labels do not collide with neighboring cards; two short output/review
labels are offset below the shaft. Revision is the sole dashed marigold rail.

Remaining defect: setting database size on all large icons caused the two native
subprocess sidebar strokes to clip out. Correct ONLY this rendering overlap in pass 2
by removing the database-only `size` override from `assembly-icon` and `children-icon`.
No new content or compositional redesign is permitted after this pass.

Remaining orientation defects: 0. Overlap/clipping defects: 2. Arrow defects: 0.
