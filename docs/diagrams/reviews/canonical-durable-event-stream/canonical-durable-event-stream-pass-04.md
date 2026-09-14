# canonical-durable-event-stream: pass 04

Mode: correction-only.
No permitted defect found; saved a distinct source and fresh draw.io PNG export without composition/content changes.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-04-print-sheet-1.png` or `-2.png`,
and opened individually at enlarged export resolution. The previous exported PNG was
inspected before this pass. This record was compiled after the tool-backed inspections.

Orientation defects remaining: 0; overlap defects remaining: 0;
arrow defects remaining: 0.
No new nodes, facts, themes or composition were introduced after pass 1.

Visible-semantic growth metric: `visible-semantic-canonical-xml-v1`.
Pitch 1670 → pass 1 23263
= 13.92994x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Producer -> EF append -> durable RunEvents -> replica-B subscriber -> SSE -> watcher. The store/read edge is the returned ordered batch, not a live in-process channel. The endpoint drains the batch before evaluating terminal close.
Evidence: apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| e1 | producer → append | append | producer / append | direct horizontal gutter / activation route | clean |
| e2 | append → store | commit | append / store | direct horizontal gutter / activation route | clean |
| e3 | store → reader | ordered batch | store / reader | [('805', '220.5'), ('805', '311'), ('805', '311'), ('805', '414.5')] | clean |
| e4 | reader → sse | yield | reader / sse | direct horizontal gutter / activation route | clean |
| e5 | sse → client | SSE frames | sse / client | direct horizontal gutter / activation route | clean |
