# canonical-durable-event-stream-sequence: pass 04

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
Pitch 3250 → pass 1 30833
= 9.487077x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Eight chronological messages: append; locked write; commit reply; sequence acknowledgment; reconnect; cursor query; durable batch reply; SSE delivery. Left-pointing replies retain their correct recipient. No channel-publish phase is invented for EF.
Evidence: apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| message1 | producer → stream | 1  append(runId, event) | activation0-producer / activation0-stream | direct horizontal gutter / activation route | clean |
| message2 | stream → store | 2  lock run · allocate MAX + 1 · save | activation1-stream / activation1-store | direct horizontal gutter / activation route | clean |
| message3 | store → stream | 3  commit succeeds | activation2-store / activation2-stream | direct horizontal gutter / activation route | clean |
| message4 | stream → producer | 4  acknowledge assigned sequence | activation3-stream / activation3-producer | direct horizontal gutter / activation route | clean |
| message5 | client → sse | 5  reconnect with last delivered cursor | activation4-client / activation4-sse | direct horizontal gutter / activation route | clean |
| message6 | sse → store | 6  query Sequence > cursor | activation5-sse / activation5-store | direct horizontal gutter / activation route | clean |
| message7 | store → sse | 7  return ordered durable batch | activation6-store / activation6-sse | direct horizontal gutter / activation route | clean |
| message8 | sse → client | 8  emit id + event + data; advance cursor | activation7-sse / activation7-client | direct horizontal gutter / activation route | clean |
