---
"Agentweaver.Gateway": patch
---

Expose runtime cleanup status in the Gateway OpenAPI contract. The native CLI
uses this status to wait for accepted-run cleanup before it removes project
access.
