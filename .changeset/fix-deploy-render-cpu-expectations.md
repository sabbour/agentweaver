---
"agentweaver": patch
---

Fix `deploy-render.test.mjs` CPU resource expectations to match the AgentHost/exec
resource rebalance from #886 (agent-host 400m/1000m -> 300m/800m, exec
600m/1000m -> 700m/1200m). The Node toolchain tests job is path-conditional and
didn't run for that k8s-only change, so the stale expectations went undetected
until the v0.19.1 release PR touched a triggering path.
