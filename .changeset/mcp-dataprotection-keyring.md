---
"agentweaver": patch
---

Persist the ASP.NET Core Data Protection key ring for API and MCP in Azure Key Vault. Return 404 for stale MCP session ids instead of 500 so clients can open a new session.
