---
"agentweaver": patch
---

Fix agent-host-maintenance workflow to push to GHCR instead of ACR. ACR login via azure/login OIDC is not available in this workflow context.
