# canonical-board-lifecycle: pass 02

Mode: correction-only.
The visual-upgrade draft was regrounded before correction passes in WorkflowStageProjector: default Active, Problems, Human Review, Done, plus Backlog and Ready. Pass 2 separated problem/review ports and fitted Done's exact persisted statuses on one line.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-02-print-sheet-1.png` or `-2.png`,
and opened individually at enlarged export resolution. The previous exported PNG was
inspected before this pass. This record was compiled after the tool-backed inspections.

Orientation defects remaining: 0; overlap defects remaining: 0;
arrow defects remaining: 0.
No new nodes, facts, themes or composition were introduced after pass 1.

Visible-semantic growth metric: `visible-semantic-canonical-xml-v1`.
Pitch 1652 → pass 1 23424
= 14.179177x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Five schematic arrows are queue, claim/start, await review, finish, and problem. These summarize typical changes, not exhaustive transitions. Problems includes recoverable assembly blocked; configured stages can replace defaults.
Evidence: apps/Agentweaver.Api/Runs/BoardProjectionService.cs:65-145; apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs:21-80; packages/Agentweaver.Domain/BacklogTaskState.cs; apps/web/src/api/board.ts:20-44
