---
"Agentweaver.FoundationProbe": patch
---

Separate the Probe image's build provenance from the actual deployed
infrastructure source and scope. Require exact native deployment receipts
without labeling an AKS-only deployment as a full foundation deployment.
Version 0.0.1 requires the new explicit deployment binding in its target and
receipt; regenerate target configuration from verified native receipts.
Previously published 0.0 images remain unchanged.
