---
"agentweaver": patch
---

Prevent post-Preview assembly from reusing an AgentHost pod configured for a different
coordinator run that happens to share the same Kubernetes claim name.
