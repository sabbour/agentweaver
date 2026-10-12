---
"Agentweaver.FoundationProbe": patch
---

Separate the Probe image's build provenance from the actual deployed
infrastructure source and scope. Require exact native deployment receipts
without labeling an AKS-only deployment as a full foundation deployment.
The corrected initial 0.0.0 requires the new explicit deployment binding in its target and
receipt; regenerate target configuration from verified native receipts.
Only the explicitly confirmed initial Probe tag may replace its approved old
index once. Preserve the old image by digest and all historical receipts.
Other image and NuGet baselines remain unchanged. This pending patch records
ordinary future release intent; do not apply a version bump for this initial replacement.
