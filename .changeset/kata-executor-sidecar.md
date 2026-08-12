---
"agentweaver": patch
---

Restore Kata AgentHost readiness by moving model-controlled command execution into a hardened executor sidecar container, replacing a bubblewrap PID/procfs namespace that the kernel cannot create inside any Kubernetes container. Sandboxed process groups are now resolved and terminated without `/proc/<pid>/task/<pid>/children`, which the Kata guest kernel does not provide, so preview processes start reliably and no command can leak daemonised processes into the executor container.
