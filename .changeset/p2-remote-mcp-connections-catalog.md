---
"Agentweaver.Abstractions": minor
"Agentweaver.Environment": minor
"Agentweaver.Gateway": minor
"Agentweaver.Web": minor
---

Environment discovers Remote MCP tool catalogs from an injected trusted source, stores them as immutable discovery revisions, and re-checks the Registry pin at discovery time. It also exposes a project connection directory with configuration checksums and an invocation validity check. The Gateway routes `GET /api/v1/projects/{projectId}/remote-mcp/connections` to the Environment owner. Abstractions adds the review reference contract that reviewed tool snapshots consume (#1923, #2135).
