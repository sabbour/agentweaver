---
"Agentweaver.Abstractions": minor
"Agentweaver.Identity.Broker": minor
"Agentweaver.Projects.Config": minor
---

Bind new `DisablePending` credential claims to an initial exact-owner preflight, serialize disable and settle-only cancellation under the provider lock in `Projects.Config`, and allow later authorized writes to settle older pending disable claims safely.
