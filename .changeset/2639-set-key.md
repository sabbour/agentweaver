---
"Agentweaver.Identity.Broker": patch
"Agentweaver.Gateway": patch
---

Add `application/octet-stream` UTF-8 credential upload support on the scoped BYOK `PUT .../credential` routes (`If-Match` and `Idempotency-Key`) with non-buffering Gateway forwarding and enable `agentweaver platform byok set-key` and `agentweaver project byok set-key` (`--from-stdin`, pre-stdin revision/status checks, zeroed buffers, and `GET`-based recovery).
