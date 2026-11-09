---
"Agentweaver.Providers.Sandbox.AgentSandbox": patch
---

Read an absent BuildTest Pod `hostNetwork`, `hostPID`, `hostIPC`, or `readOnly` value as `false`, as the Kubernetes API server omits these fields when they are false. BuildTest Pods now pass read-back checks on real clusters. The collector workspace mount must still state `readOnly: true`.
