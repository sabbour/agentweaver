# email-architecture: pass 01

Mode: visual-upgrade.
Visual upgrade; known residual icon, overlap and connector defects are recorded below.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-01-print-sheet-1.png` or `-2.png`,
and opened individually at enlarged export resolution. The previous exported PNG was
inspected before this pass. This record was compiled after the tool-backed inspections.

Orientation defects remaining: 1; overlap defects remaining: 1;
arrow defects remaining: 2.
No new nodes, facts, themes or composition were introduced after pass 1.

Visible-semantic growth metric: `visible-semantic-canonical-xml-v1`.
Pitch 1677 → pass 1 23596
= 14.070364x. Later passes preserve that hierarchy.
No invisible, off-page, duplicate or metadata-padding cells were found by the checker.

## Source-backed reading and review
MCP -> API is HTTP; API/worker -> sandbox is configure/A2A; Entra -> API is identity; GitHub -> API is capability; API/worker -> Postgres is persistence. There is no MCP/store edge. Independent crossing routes use real draw.io bridge arcs.
Evidence: k8s/base/api-deployment.yaml:53-76,337-349; k8s/base/worker-deployment.yaml:54-76,146-170; k8s/base/mcp-deployment.yaml:21; apps/Agentweaver.Api/Sandbox/RunGitHubCapabilityCredentialProvider.cs:10-36; apps/Agentweaver.Api/Auth/EntraOnlyGitHubCredentialBoundary.cs; coordinator-provided component and assurance findings
