---
---

Add a tracked install-target registry (`scripts/azure/lib/targets.mjs`) and the second nonproduction target `aweast2np01` in `eastus2` with its own parameter files. `plan.mjs`, the AKS-only redeployment, and the `deploy.mjs` Identity PostgreSQL, identity routing, Broker state, and Broker runtime bootstrap options now accept a registered target selected by `--resource-group`. An unregistered name still fails before any Azure call. The Broker runtime step now reads the Key Vault name from the approved parameters, so it no longer fails from the CLI, and it checks the OIDC issuer against the cluster region. The `agentweaver` target keeps the same values and checks. No product component version changed.
