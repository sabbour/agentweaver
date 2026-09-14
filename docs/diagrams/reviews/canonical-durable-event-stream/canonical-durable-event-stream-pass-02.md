# canonical-durable-event-stream: pass 02

Mode: correction-only.
Pass 2 restored the product hexagon and native database cylinder proportions; its silhouette no longer overlaps the subtitle. The five-arrow relay stays entirely in gutters.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-02-print-sheet-1.png` or `-2.png`,
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
