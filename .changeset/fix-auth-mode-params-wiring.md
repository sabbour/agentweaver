---
"agentweaver": patch
---

Fix `azure:provision-infra` silently ignoring `AUTH_MODE`/`ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID`: these variables were readable via `variables.mjs` but were never wired into `provision-infra.mjs`'s own config schema or its `resolveVariables()` env override, so `--params-file`, no `--auth-mode`/`--entra-client-id`/`--entra-tenant-id` flags existed either, and only a raw exported environment variable actually took effect. A redeploy without that env var set would silently reset a live environment's sign-in mode back to `GitHubLegacy`. Adds `--auth-mode`, `--entra-client-id`, and `--entra-tenant-id` CLI flags plus matching params-file fields, validates `AUTH_MODE` against the exact `GitHubLegacy`/`Entra` values `AuthModeResolver.Parse()` accepts, and requires `ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID` when `AUTH_MODE=Entra`.
