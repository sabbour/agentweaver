# Agentweaver · boundaries, not one process — pass-03

Audience: technical readers of the deep-dive documentation.
Takeaway: Intent enters through API or MCP; execution and durable state have separate owners.
Orientation: A5-landscape; one uncompressed editable page.
Canonical: docs/diagrams/src/00-system-overview-fig1.drawio; publication: docs/diagrams/00-system-overview-fig1.png.

## Actual image review

Opened 00-system-overview-fig1-pass-03.png enlarged and 00-system-overview-fig1-pass-03-print.png as an A5/96-dpi screen proof. The overview broker/delegate labels are separated. Opposing state/decision and service/recovery connectors now use distinct card attachment heights; native bridge arcs mark genuine geometric crossings. No new content or composition was added; unchanged diagrams were copied, exported and opened separately. Final evidence tracing found two semantic arrow defects to correct in pass 4: the deterministic runner writes isolated state (reverse the hosting diagram arrow), and the real host uses the planning seam (the boundary diagram must not imply Git invokes the planner). Other observed orientation/overlap/routing checks are clear.
This is a screen print-size review, not a physical paper proof.

## Grounding

Exactly three independent GPT-6 Astra research threads were already completed for this shard: ../canonical-api-host/research-boundaries.md, ../canonical-api-host/research-flows.md, ../canonical-api-host/research-assurance.md. No agents were launched by this batch. Implementation/config/test references below are factual evidence; prior artwork and audit dispositions are not factual evidence. Test sources were inspected; no runtime test success is implied.

## Source and symbol inventory

- **web** — Browser / Web host; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Web/Program.cs:39-65.
- **api** — API web role; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1274-1295.
- **mcp** — MCP host; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Mcp/McpBrokerAuthenticationHandler.cs:40-86; AgentweaverApiClient.cs:359-379.
- **worker** — Worker role; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:507-532,1255-1326.
- **orchestration** — Run orchestration; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:790-825; Coordinator/CoordinatorRunService.cs:1172-1250.
- **host** — AgentHost leaf; native:flowchart inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83; Assistant/AssistantRunService.cs:789-813.
- **postgres** — PostgreSQL; native:database inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Program.cs:1026-1075.
- **workspace** — Workspace + worktrees; native:uml inside custom:agentweaver Fluent chrome. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317.

## Relationships

- `web-to-api`: web → api: REST / SSE. Evidence: apps/Agentweaver.Api/Program.cs:1274-1295.
- `mcp-to-api`: mcp → api: forward broker. Evidence: apps/Agentweaver.Mcp/AgentweaverApiClient.cs:359-379.
- `api-to-orchestration`: api → orchestration: delegate. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:164-255.
- `orchestration-to-host`: orchestration → host: execute. Evidence: apps/Agentweaver.Api/Assistant/RemoteOperatorAssistantAgent.cs:77-83.
- `orchestration-to-postgres`: orchestration → postgres: persist. Evidence: apps/Agentweaver.Api/Program.cs:1026-1075.
- `host-to-workspace`: host → workspace: worktree. Evidence: apps/Agentweaver.Api/Runs/RunOrchestrator.cs:277-317.

## Credits and boundaries

Native process, decision, document, folder and cylinder symbols are editable built-in draw.io shapes, bundled with official draw.io Desktop 31.4.5. Library/code license: Apache-2.0, https://github.com/jgraph/drawio/blob/dev/LICENSE (bundled LICENSE/resources notices remain authoritative). No third-party raster logos or remote images were imported. Product-specific card chrome: custom:agentweaver; icon classifications listed below. Visual references read: docs/diagrams/drawio/fluent-template.drawio, fluent-library.xml, design-system.json and canonical-api-host review helpers. A5 normalized directly to 827 × 583 draw.io units at 100 dpi; the 1112 × 789 working template is not used as print size.

Deployment roles ≠ exclusive orchestration ownership. Operator history is not a MAF run graph.
No documentation, inventory, shared audit/plan, pipeline, skill, runtime, or foreign asset was edited. The parent owns Markdown provenance updates.
