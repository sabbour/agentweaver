---
"agentweaver": patch
---

Bound sandboxed `run_command` execution so a non-terminating command can no longer park a run silently forever. Commands now have a configurable default budget of 30 minutes, emit a degraded-run signal when they exceed it, and return guidance telling agents to use `start_preview_process` for long-lived preview/dev servers.
