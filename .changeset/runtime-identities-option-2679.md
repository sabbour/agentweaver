---
---

Add the guarded `--bootstrap-runtime-identities` option to `scripts/azure/deploy.mjs`. With the AKS-only template and parameters for a registered target, it deploys `infra/bicep/p1-runtime-identities.bicep` with the target name, the resource group tags, and the source SHA. It stops if the cluster and resource group locations differ, and accepts the result only when the deployment succeeds and returns the five service identities in order with valid IDs and the cluster OIDC issuer.
