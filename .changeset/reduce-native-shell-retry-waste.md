---
'agentweaver': patch
---

Reduce wasted tool-calling round-trips where the model tried the SDK's native shell tool first (always denied) before falling back to the sandboxed `run_command` tool, by adding explicit guidance to the shared agent base prompt to use `run_command` directly.
