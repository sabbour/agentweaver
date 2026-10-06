---
"Agentweaver.FoundationProbe": patch
---

Enable declared P0 system-pool autoscaling at two to three existing-size nodes
so the Foundation Probe can request capacity. The AKS-only installer preserves
an observed count within those bounds, refuses changes after what-if, and records
native autoscaler configuration separately from runtime or scale-up proof.
