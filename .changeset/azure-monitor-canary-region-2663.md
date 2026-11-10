---
---

Fix installs on a canary-region AKS cluster (for example `eastus2euap`) with the Log Analytics workspace in a production region. Azure Monitor cannot associate a canary-region cluster with a data collection rule outside `eastus2euap`, and the rule must be in the workspace region, so `az aks enable-addons` failed with `InvalidAssociation` and blocked every redeploy. `scripts/azure/deploy.mjs --execute` now skips the add-on for this layout with a warning and records `action: unsupported-region` in the receipt. `npm run deploy:kustomize` accepts a missing `ama-logs` DaemonSet only when every node has a canary region label. Container Insights, `ContainerNetworkLogs`, and the egress audit data are not collected in this layout.
