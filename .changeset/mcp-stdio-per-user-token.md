---
"agentweaver": minor
---

Fixed a High-severity security-assessment finding: stdio MCP clients (e.g. the
CLI, editor integrations) previously authenticated backend calls with the
shared `AGENTWEAVER_API_KEY`, which the API maps to the trusted
`agentweaver-internal` identity and exempts from project-ownership checks —
letting any stdio client reach every project on the backend, not just the
operator's own.

Stdio clients should now set `AGENTWEAVER_TOKEN` to a per-user bearer token
(an Agentweaver-minted OAuth access token, or a GitHub token such as `gh auth
token`) so the backend attributes calls to the real user and enforces
project ownership. Credential precedence is: inbound per-request token (HTTP
transports) → `AGENTWEAVER_TOKEN` → `AGENTWEAVER_API_KEY` (last-resort
fallback).

**Breaking change for stdio deployments still relying on the shared key**:
if `AGENTWEAVER_TOKEN` is not set and `AGENTWEAVER_API_KEY` is, the MCP
server now refuses to start in stdio mode by default. Set
`AGENTWEAVER_ALLOW_SHARED_KEY=true` to explicitly opt back into the
insecure fallback (e.g. for first-party service-to-service callers that
intentionally use the shared identity). See `docs/guide/mcp-cli.md` for
migration guidance.
