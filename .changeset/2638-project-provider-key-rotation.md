---
"Agentweaver.Abstractions": patch
"Agentweaver.Projects.Config": patch
"Agentweaver.Identity.Broker": patch
"Agentweaver.Gateway": patch
"Agentweaver.Web": patch
---

Add read-only BYOK provider credential status endpoints without preflight writes, early Gateway `no-store` headers on credential routes, and project BYOK provider key rotation with fail-closed status handling in the web UI.
