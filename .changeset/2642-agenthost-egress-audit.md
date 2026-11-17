---
"Agentweaver.Environment": minor
---

Add opt-in ACNS flow-log collection for AgentHost egress audits. The Azure installer configures forwarding to the install workspace and grants the Environment identity workspace-scoped Log Analytics Reader. Kustomize safely manages the owned collector and requires a ready, DaemonSet-owned `ama-logs` pod on every sandbox node before an audit deployment. Commit deployments pass the selected audit mode to the exact-source helper. AKS-only plans show the audit mode and later forwarding update.
