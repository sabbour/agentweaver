---
"agentweaver": patch
---

Fix Cluster page diagnostics: the Sandbox claims "Warm pool used" column now reads the live v1beta1 `warmPoolRef.name` field, the permanently empty Sandbox objects section is removed, and Pending capacity now explains that zero means runs are getting a sandbox immediately.
