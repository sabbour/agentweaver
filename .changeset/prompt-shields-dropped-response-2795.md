---
"Agentweaver.Orchestrator.Core": patch
"Agentweaver.Orchestrator": patch
---

Treat a dropped Prompt Shields response body as a temporary classifier outage.
Withhold content and keep the existing retry response instead of letting the
body-read exception escape.
