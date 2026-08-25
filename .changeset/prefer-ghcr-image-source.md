---
"agentweaver": minor
---

Deployment scripts now default to GHCR (`--image-source ghcr`) instead of ACR-build. This is faster for release deployments since GHCR images are pre-built by CI. ACR-build remains available as an explicit option.
