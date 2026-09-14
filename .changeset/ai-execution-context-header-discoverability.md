---
"agentweaver": patch
---

Declare the `If-Model-Provider-Key` precondition header on every AI-guarded API operation. Seventeen endpoints require a prepared AI execution context, but only `POST /api/projects/{id}/orchestrations` documented the header, so an OpenAPI-guided client could discover a guarded route, receive `409 ai_execution_context_required`, and have no way to learn how to satisfy it. A shared `RequiresAiExecutionContext()` route extension now declares the header on all of them, marking it optional with a stated condition where the endpoint only invokes a model for some request shapes (for example `/steer`, which needs it for every verb except `stop`). The three 409 hint messages also name the header and say to resend the request. Behavior is unchanged; this is discoverability only.
