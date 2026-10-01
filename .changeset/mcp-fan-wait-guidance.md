---
"agentweaver": patch
---

Persist the original workflow request kind and pin current review output so run detail distinguishes an actionable manual review from automatic fan-out waits. MCP clients poll the existing run through child continuation instead of suggesting approval or starting a duplicate run.
