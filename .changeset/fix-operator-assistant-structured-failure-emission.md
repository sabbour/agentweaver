---
"agentweaver": patch
---

Emit structured `RunFailed` events for operator assistant provider failures (snapshot, client-creation, client-start, session-creation, streaming) through `IOperatorAssistantTurnSink` so the real error code and message reach the client instead of being masked by a generic "aborted before reporting a structured terminal failure" fallback.
