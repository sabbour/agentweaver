---
---

Fix the Azure Monitor add-on step for an AKS cluster outside a canary region. `scripts/azure/deploy.mjs --execute` sent `--ampls-resource-id` to `az aks enable-addons`, and Azure CLI accepts that option only for a private cluster. The installer cluster is public, so the step failed after a successful infrastructure deployment. The installer no longer sends the option. The workspace accepts ingestion from public networks, and queries stay private.
