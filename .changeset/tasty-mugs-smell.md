---
"agentweaver": minor
---

Add `--node-vm-size` / `NODE_VM_SIZE` to `azure:provision-infra` so new AKS clusters can override the node-pool VM SKU when a subscription or region disallows the default. The default new-cluster SKU is now `Standard_D4s_v6` (up from `Standard_D4s_v3`); existing clusters are unaffected because the installer skips cluster and node-pool creation when those resources already exist.
