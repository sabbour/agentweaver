---
"Agentweaver.Abstractions": minor
"Agentweaver.Providers": minor
---

Add ordered, layered provider composition: permitted Guardrails and Telemetry
providers resolve and pin as ordered sets, and Network Policy resolves its
required L3/L4 and optional L7 layers as platform-selected, platform-owned
providers with a confirmed applied egress intent generation. No runtime
policy enforcement or deployment is implied. See #1767, PR #1770.
