# email-architecture: pass 04

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
Pitch 1677 → pass 1 23596
= 14.070364x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
MCP -> API is HTTP; API/worker -> sandbox is configure/A2A; Entra -> API is identity; GitHub -> API is capability; API/worker -> Postgres is persistence. There is no MCP/store edge. Independent crossing routes use real draw.io bridge arcs.
Evidence: k8s/base/api-deployment.yaml:53-76,337-349; k8s/base/worker-deployment.yaml:54-76,146-170; k8s/base/mcp-deployment.yaml:21; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:10-36; apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs; coordinator-provided component and assurance findings

## Complete final arrow trace
Each row was traced in the final exported PNG and checked against the editable endpoints.
Arrowheads point toward the named target; gutters avoid cards/labels.
Crossings are native `jumpStyle=arc` bridges, never paint-over masks. No false junction dots.
Neutral UML dashed dependency/lifeline strokes are not marigold semantic return rails.

| ID | Semantic endpoints | Relationship | Editable cell endpoints | Route waypoints | Result |
|---|---|---|---|---|---|
| e1 | mcp → api | HTTP API | mcp / api | direct horizontal gutter / activation route | clean |
| e2 | api → sandbox | configure / A2A | api / sandbox | [('275', '200'), ('275', '298'), ('805', '298'), ('805', '200')] | clean |
| e3 | entra → api | identity | entra / api | [('18', '414'), ('18', '220')] | clean |
| e4 | github → api | capability | github / api | [('283', '414'), ('283', '235')] | clean |
| e5 | api → postgres | read / write | api / postgres | [('267', '255'), ('267', '306'), ('797', '306'), ('797', '430')] | clean |
