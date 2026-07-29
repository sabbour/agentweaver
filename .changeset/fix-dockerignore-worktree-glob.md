---
'agentweaver': patch
---

Exclude Copilot-created worktree directories from Azure Container Registry build contexts so local deploys do not upload huge accidental tarballs from sibling repo checkouts.
