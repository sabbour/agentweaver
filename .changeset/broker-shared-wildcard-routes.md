---
"Agentweaver.Identity.Broker": patch
---

Route the Broker auth and Gateway API aliases through the shared wildcard
HTTPS listener so HTTP/2 connection coalescing preserves exact-host routing.
