---
'agentweaver': patch
---

Restore the production AgentHost A2A listener so hardened deployments bind the
expected mTLS endpoint on port 8088 and reject clients whose certificates are
not signed by the mounted Agentweaver CA.
