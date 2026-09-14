# email-components: pass 04

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
Pitch 1358 → pass 1 23030
= 16.958763x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
Four selected ProjectReferences: Api -> AgentRuntime; AgentHost -> AgentRuntime; AgentRuntime -> AgentTools; AgentTools -> Domain. No MCP dependency arrow and no network routing implication.
Evidence: apps/Agentweaver.Api/Agentweaver.Api.csproj; apps/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj; packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj; packages/Agentweaver.AgentTools/Agentweaver.AgentTools.csproj

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| e1 | api → runtime | ref. | api / runtime | [('19', '234.4'), ('19', '414.5')] | clean |
| e2 | host → runtime | ref. | host / runtime | [('413.5', '304'), ('268', '304'), ('268', '386.7')] | clean |
| e3 | runtime → tools | ref. | runtime / tools | direct horizontal gutter / activation route | clean |
| e4 | tools → domain | ref. | tools / domain | direct horizontal gutter / activation route | clean |
