---
"Agentweaver.Environment": patch
---

Create the provider-owned DNS-only AgentHost egress policy only when it is
missing. Keep it outside Kustomize, retain the existing broad Cilium grants,
and add only the named `patch` permission for that policy.
