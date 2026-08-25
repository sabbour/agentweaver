---
"agentweaver": patch
---

Fix the `tool.approval_context` SSE event not being handled by the frontend
timeline reducer, so approval context is now correctly applied to the
coordinator run model instead of being silently dropped.
