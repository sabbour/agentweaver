---
"agentweaver": patch
---

Fix `execute_tool` telemetry spans reporting an inflated, identical duration for every tool in a parallel batch when one sibling blocks. When a `web_fetch` call waits out its 5-minute HITL approval deadline, the GitHub Copilot SDK's sequential dispatch stalls delivery of the other tools' lifecycle events; because the span was bounded by when our consumer loop observed those events, near-instant tools (e.g. `list_decisions`, `get_memory`, `list_inbox`) were reported at the same ~5-minute duration. Spans are now bounded by the SDK event's own `Timestamp`, so each tool's recorded duration reflects its real execution window rather than consumer-loop back-pressure.
