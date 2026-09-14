---
"agentweaver": patch
---

Show live `run_command` progress in transaction traces with a correlated, output-free heartbeat.

Long-running sandboxed commands now emit `tool.execution_pending` progress frames over the existing
run stream, and the trace detail panel updates from that stream without browser polling. The
heartbeat is tied to the matching tool-call id and omits command text, output, exit code, working
directory, and environment data.
