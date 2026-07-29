---
'agentweaver': patch
---

Fix Cluster diagnostics by removing the false `github_installation_token` alarm, measuring `agent_pod_quota` from the real pod and SandboxClaim object quotas instead of the removed CPU cap, and enabling the Cluster page's 30-second auto-refresh by default.
