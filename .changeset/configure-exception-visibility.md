---
'agentweaver': patch
---

Surface unhandled exceptions from the AgentHost `/configure` endpoint instead of
letting them escape as an opaque, empty-body HTTP 500. The endpoint now logs the
real exception (still attributable to the specific run/pod before it recycles) and
returns a structured `agenthost_configure_unexpected_exception` JSON body, making the
recurring `agenthost_configure_failed` failure diagnosable.
