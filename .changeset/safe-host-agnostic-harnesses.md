---
"agentweaver": patch
---

Make API, UI, and MCP harnesses safe for arbitrary HTTPS hosts with mandatory TLS,
environment-only credentials, redirect rejection, recursive secret and URL redaction,
and failure-safe owned-resource cleanup. Align OAuth certificate deployment and
verification with runtime usability rules and roll API pods when certificate families
change.
