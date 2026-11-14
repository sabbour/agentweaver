---
"Agentweaver.Gateway": patch
---

The Copilot callback relay now returns `413` with `request_body_too_large`
when the request body is larger than 16 KiB, with or without
`Content-Length`. Before this change, a chunked body returned `502`
`owner_response_too_large`, and a declared length returned `400`. The
Gateway does not forward an oversized body to the Identity Broker.
