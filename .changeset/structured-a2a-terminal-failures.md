---
"agentweaver": patch
---

Normalize aborted A2A turns to a single structured `agent_turn_internal_error` across general, Responsible AI, and Build & Test agents, while retaining bounded redacted diagnostics instead of exposing raw unsupported-event reasons.
