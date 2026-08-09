---
"agentweaver": minor
---

Add `--image-source ghcr` to `azure:deploy-from-release`, so an already-published release can be redeployed by importing its existing GHCR images instead of rebuilding them from source. This skips a full container rebuild and never touches cluster, ACR, Postgres, identity, or monitoring infrastructure.
