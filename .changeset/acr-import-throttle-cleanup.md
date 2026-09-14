---
"agentweaver": patch
---

Make ACR release imports more resilient. GHCR imports now retry registry throttling before they fail. Staging tag cleanup now uses a short timeout and never blocks a completed deploy.
