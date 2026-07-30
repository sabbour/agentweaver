---
"agentweaver": patch
---

Wire `Auth:Mode` and `Auth:Entra:*` config through the AKS deploy pipeline (`AUTH_MODE`/`ENTRA_CLIENT_ID`/`ENTRA_TENANT_ID` deploy-time environment variables) so Entra sign-in mode can actually be enabled on deployed environments.
