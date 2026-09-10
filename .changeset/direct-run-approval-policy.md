---
"agentweaver": minor
---

Let API, MCP, and web users choose safe-tool auto-approval and autopilot when starting a run directly, without routing work through heartbeat pickup. Heartbeat claims now atomically snapshot the latest persisted pickup settings onto each reserved run so stale replicas, updates between claims, activation gaps, and retries cannot reset the selected policy.
