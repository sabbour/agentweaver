# email-coordinator-workflow: pass 04

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
Pitch 3297 → pass 1 33008
= 10.011526x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Nine chronological messages: submit; request confirmation; confirm; dispatch; child results; assembly; collective review request; approve; MergeWorktree/Scribe. The final combined participant is a compact approved-path grouping, not a claim that Git is Scribe or a PR publisher.
Evidence: apps/Agentweaver.Api/Coordinator/CoordinatorAssemblyService.cs:65-82 and assembly workflow construction; apps/Agentweaver.Api/Coordinator/CoordinatorDispatchService.cs:48-57; apps/Agentweaver.Api/Coordinator/CoordinatorWorkflowFactory.cs:81-96; coordinator-provided completed assurance research summary

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| message1 | human → coord | 1  submit goal; draft and persist OutcomeSpec | activation0-human / activation0-coord | direct horizontal gutter / activation route | clean |
| message2 | coord → human | 2  request confirmation / revision | activation1-coord / activation1-human | direct horizontal gutter / activation route | clean |
| message3 | human → coord | 3  confirm; persist WorkPlan DAG | activation2-human / activation2-coord | direct horizontal gutter / activation route | clean |
| message4 | coord → children | 4  dispatch dependency-ready children | activation3-coord / activation3-children | direct horizontal gutter / activation route | clean |
| message5 | children → coord | 5  report settled results; block failed dependents | activation4-children / activation4-coord | direct horizontal gutter / activation route | clean |
| message6 | coord → assembly | 6  integrate branches; run configured gates | activation5-coord / activation5-assembly | direct horizontal gutter / activation route | clean |
| message7 | assembly → human | 7  request one collective review | activation6-assembly / activation6-human | direct horizontal gutter / activation route | clean |
| message8 | human → assembly | 8  approve reviewed integration | activation7-human / activation7-assembly | direct horizontal gutter / activation route | clean |
| message9 | assembly → scribe | 9  MergeWorktree → Scribe | activation8-assembly / activation8-scribe | direct horizontal gutter / activation route | clean |
