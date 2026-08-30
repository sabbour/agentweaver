---
"agentweaver": patch
---

Fix `deploy-from-commit` and `deploy-from-release` not auto-loading the per-user
`params.<username>.json` config file the way `deploy-from-local` already does. Previously
these two subcommands required every deploy variable (e.g. `KEYVAULT_NAME`) to be set by
hand in the shell environment, unlike `deploy-from-local`.
