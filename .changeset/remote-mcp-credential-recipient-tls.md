---
"Agentweaver.Identity.Broker": minor
---

Add a separate, default-off Identity Broker TLS listener for the configured Environment credential recipient.
Keep profiles separate and reject incomplete configuration, wrong service roles, ordinary HTTPS, and a different listener before credential access.
This source does not enable remote MCP credential delivery or configure existing identities and certificates.
