---
"agentweaver": patch
---

Restore Kata AgentHost readiness by moving model-controlled command execution into a hardened executor sidecar container, replacing a bubblewrap PID/procfs namespace that the kernel cannot create inside any Kubernetes container.
