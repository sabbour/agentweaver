---
"Agentweaver.Abstractions": minor
"Agentweaver.Environment": minor
"Agentweaver.Providers.Sandbox.AgentSandbox": minor
---

Add an owner-fenced Environment Sandbox lease lifecycle and the default Kubernetes agent-sandbox adapter. Persist exact provider selection and recovery requests, verify the selected Workspace PVC and Cilium generation before readiness, release only the exact retired Sandbox placement, and expose a versioned current-lease projection to project owners and exact run-bound readers without widening mutation authority.
