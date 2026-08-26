---
"agentweaver": patch
---

Raise agentweaver-exec container memory limit from 2Gi to 4Gi (request from 1Gi to 2Gi) to prevent kernel OOM kills when an agent runs a preview server alongside its own process. Scheduling density is unchanged — CPU (1000m/pod) remains the binding constraint at ~3 pods/node; only the per-container memory limit (a cgroup ceiling, not a scheduling input) was raised.
