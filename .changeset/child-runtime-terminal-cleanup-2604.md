---
"Agentweaver.Orchestrator": minor
"Agentweaver.Identity": minor
"Agentweaver.Environment": minor
---

Start committed child runtimes through the owner-controlled provisioning path
and track exact cleanup for every terminal run outcome. Completed and cancelled
runs stay final. Child startup waits for the terminal cleanup schema. Cleanup
works on older schemas. A failed bootstrap releases its registration and
placement. Dispatch does not wait for a busy startup lease.
