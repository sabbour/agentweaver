---
"agentweaver": patch
---

Clear durable AgentHost pod bindings when a claim is released so assembly retries launch a replacement instead of reusing a deleted pod.
