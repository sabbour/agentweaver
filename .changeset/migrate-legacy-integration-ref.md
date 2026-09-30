---
"agentweaver": patch
---

Allow an assembly attempt to publish its integration branch when an older run left a
flat integration branch at the same path. Preserve the previous branch under a
legacy-integration name so retries and later attempts can proceed without losing it.
