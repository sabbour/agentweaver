# canonical-durable-event-stream: pass 01

Mode: visual-upgrade.
Visual upgrade; known residual icon, overlap and connector defects are recorded below.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-01-print-sheet-1.png` or `-2.png`,
and opened individually at enlarged export resolution. The previous exported PNG was
inspected before this pass. This record was compiled after the tool-backed inspections.

Orientation defects remaining: 2; overlap defects remaining: 1;
arrow defects remaining: 0.
No new nodes, facts, themes or composition were introduced after pass 1.

Visible-semantic growth metric: `visible-semantic-canonical-xml-v1`.
Pitch 1670 → pass 1 23263
= 13.92994x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Producer -> EF append -> durable RunEvents -> replica-B subscriber -> SSE -> watcher. The store/read edge is the returned ordered batch, not a live in-process channel. The endpoint drains the batch before evaluating terminal close.
Evidence: apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15-36,66-87,145-190,220-320; apps/Agentweaver.Api/Endpoints/RunEndpoints.cs
