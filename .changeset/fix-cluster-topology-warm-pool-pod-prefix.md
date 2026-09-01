---
"agentweaver": patch
---

Fix Cluster page "Resource topology" graph never showing warm-pool sandbox instances, sandbox claims, or agent pods beyond the top-level pool summary.

Root cause: `DiagnosticsService.GetWarmPoolPodInventoryAsync` matched sandbox pods against a hardcoded `agentweaver-sandbox-` name prefix, but live warm-pool pods are generated from the `SandboxWarmPool`'s pod template with `generateName: "<pool-name>-"` (e.g. `agentweaver-agent-host-jbn86`). Since no live pod ever started with `agentweaver-sandbox-`, `warm_pools[].instances` always came back empty, even though the pool's `ready_replicas`/`available_replicas` counts (read from the CRD status) were correct — matching the "2/2 ready" summary the user saw while the per-instance list stayed empty.

Pods are now matched to their owning warm pool by the longest `"<pool-name>-"` prefix match against the pool names returned from the `SandboxWarmPool` CRD listing, instead of a hardcoded constant. This also makes the matching correct if additional warm pools are ever added.
