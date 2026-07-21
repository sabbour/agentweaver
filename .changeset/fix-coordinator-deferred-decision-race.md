---
"agentweaver": patch
---

Fixed a Coordinator race where a deferred decision applied only on the next
heartbeat instead of immediately when the approval gate armed, and switched
run-plan tests to poll for the ordered `coordinator.work_plan` stream event
rather than treating the earlier database commit as completion.
