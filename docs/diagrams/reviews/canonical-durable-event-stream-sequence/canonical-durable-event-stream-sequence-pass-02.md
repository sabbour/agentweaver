# canonical-durable-event-stream-sequence: pass 02

Mode: correction-only.
Pass 2 corrected product/database silhouettes. Horizontal numbered messages attach to explicit activation bars, not headers. Context annotations occupy unused temporal lanes.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-02-print-sheet-1.png` or `-2.png`,
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
