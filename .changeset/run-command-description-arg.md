---
"agentweaver": patch
---

Accept a model-supplied `description` argument on the sandboxed `run_command` tool.

The native Copilot shell tool accepts a `description`, so the model frequently supplied one to the
sandboxed `run_command` too. That argument did not match this tool's schema, so the call fell through
to the disabled native shell and cost a full turn to a `tool.error` + `run.degraded` before the agent
retried without it. The argument is now accepted and ignored.
