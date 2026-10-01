---
"agentweaver": patch
---

Allow PostgreSQL worker replicas to resume composed workflow parents after their fan
children finish. The worker now shares the active-run claim needed to project child
artifacts before delivering the parent continuation.
