---
"agentweaver": patch
---

fix(preview): keep preview startup and forwarding responsive while DNS warms

Preview startup now tolerates Kata writable-root setup latency and cold forwarder
health checks. The interface also reports DNS warm-up while a new preview hostname
becomes reachable, instead of implying the URL is immediately ready.
