---
---

Every installation now enables the AKS Azure Monitor add-on with the installation's `<prefix>-law` workspace. `scripts/azure/deploy.mjs --execute` enables the add-on for both infrastructure templates, or adopts it when it is already enabled. It stops when the add-on sends to another workspace or uses shared-key authentication. `npm run deploy:kustomize` stops before writes when DaemonSet `ama-logs` in `kube-system` is not ready. There is no opt-out.
