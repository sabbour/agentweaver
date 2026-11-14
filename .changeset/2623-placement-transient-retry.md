---
"Agentweaver.Environment": patch
---

A retried run start now applies the first-start network policy again after a
transient Kubernetes or Projects & Config failure. Before this change, the
retry returned the earlier failure without a new Kubernetes request. A
conflicting policy object, a changed owner fence, or a changed run authority
still stops the start. The placement route now reports `upstream_unavailable`
and `upstream_timeout` as HTTP 503, not HTTP 403.
