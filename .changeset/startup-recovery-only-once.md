---
"agentweaver": patch
---

Share one startup recovery leader across API and worker replicas and keep its
lease until shutdown, so neither role repeats a successful sweep over new
coordinator runs. Stop retrying failed or timed-out leader sweeps after three
attempts per process; followers can still take over if the leader exits.
