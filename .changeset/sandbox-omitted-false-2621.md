---
"Agentweaver.Providers.Sandbox.AgentSandbox": patch
---

Read an absent Pod `readOnly` or `hostNetwork` value as `false`, as the Kubernetes API server omits these fields when they are false. Writable workspace Pods now pass sandbox placement checks.
