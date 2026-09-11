---
"agentweaver": minor
---

Let API, MCP, and web users choose safe-tool auto-approval and autopilot when starting a run directly, without routing work through heartbeat pickup. Heartbeat claims now atomically snapshot the latest persisted pickup settings onto each reserved run so stale replicas, updates between claims, activation gaps, and retries cannot reset the selected policy. Explicit safe-tool auto-approval now includes `start_preview`: it skips only the human wait, emits a sanitized policy-snapshot audit decision, and preserves all preview validation and unsafe-tool boundaries.
