---
"agentweaver": patch
---

Fix Copilot binding creation so credential secrets are read back before bindings are committed, and log cleanup failures when persistence rolls back after a secret write.
