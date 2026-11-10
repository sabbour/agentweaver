---
---

Build the AKS subnet ID of the v1 Azure foundation with `resourceId()`, so a new-foundation what-if can resolve the subnet role assignment. Before this change, `deploy.mjs --new-foundation --execute` stopped on an `Unsupported` what-if change. The ID string and the role assignment name for the existing `agentweaver` install do not change.
