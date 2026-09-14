# email-components: pass 02

Mode: correction-only.
Pass 2 separated API and AgentHost dependency paths, moved API's route to the left perimeter, shortened the shared-package group label, and retained dashed open UML dependency heads.

Actual exported PNG opened at print scale in
`../canonical-agent-communication-handoff/pass-02-print-sheet-1.png` or `-2.png`,
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
