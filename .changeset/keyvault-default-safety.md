---
'agentweaver': patch
---

Remove the unsafe hardcoded `KEYVAULT_NAME` default (`agentweaver-kv`) from the
Azure deploy tooling (`scripts/azure/variables.mjs`). That generic default was
never a real Key Vault in any provisioned subscription, and deploy commands
silently fell back to it (or to a manually-typed-but-wrong vault name) whenever
an operator forgot to set `KEYVAULT_NAME` explicitly -- corrupting the rendered
`agentweaver-runtime-config` ConfigMap and the `agentweaver-secrets`/
`agentweaver-user-tokens` SecretProviderClasses' `keyvaultName`/Key Vault URI
fields and silently breaking GitHub OAuth sign-in.

`KEYVAULT_NAME` is now REQUIRED with no generic default: `resolveVariables()`
fails fast with an actionable error if it is unset. `steps/30-deploy.mjs`
additionally verifies (`az keyvault show`) that the named vault actually
exists BEFORE rendering or applying any manifest, catching typos that happen
to name a real-but-wrong vault too (not just a made-up name). This is internal
deploy-tooling reliability hardening; there is no user-facing application
behavior change.
