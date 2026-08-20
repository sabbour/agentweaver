---
"agentweaver": patch
---

fix(deploy): auto-load params.<username>.json for deploy-from-local

Prevents AUTH_MODE from resetting to GitHubLegacy on every deploy-from-local run.
