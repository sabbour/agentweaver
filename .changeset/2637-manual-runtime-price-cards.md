---
"Agentweaver.EventsAndSessions": minor
"Agentweaver.Gateway": minor
"Agentweaver.Projects.Config": minor
---

Add the platform-admin manual runtime price-card write endpoint (`PUT /api/v1/platform/model-pricing/rate-cards/{id}/{version}` and Events owner route) with unbound `PlatformAdmin` authority verification, 64 KiB request bounds, and immutable `201`/`200`/`409` semantics.
