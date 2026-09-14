# canonical-board-lifecycle: pass 04

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
Pitch 1652 → pass 1 23424
= 14.179177x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Five schematic arrows are queue, claim/start, await review, finish, and problem. These summarize typical changes, not exhaustive transitions. Problems includes recoverable assembly blocked; configured stages can replace defaults.
Evidence: apps/Agentweaver.Api/Runs/BoardProjectionService.cs:65-145; apps/Agentweaver.Api/Runs/WorkflowStageProjector.cs:21-80; packages/Agentweaver.Domain/BacklogTaskState.cs; apps/web/src/api/board.ts:20-44

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| e1 | backlog → ready | queue | backlog / ready | direct horizontal gutter / activation route | clean |
| e2 | ready → progress | claim + start | ready / progress | direct horizontal gutter / activation route | clean |
| e3 | progress → review | await review | progress / review | [('812', '206.6'), ('812', '310'), ('536', '310'), ('536', '386.7')] | clean |
| e4 | review → done | finish | review / done | direct horizontal gutter / activation route | clean |
| e5 | progress → failed | problem | progress / failed | [('800', '248.3'), ('800', '299'), ('268', '299'), ('268', '386.7')] | clean |
