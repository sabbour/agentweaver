---
"agentweaver": patch
---

Improve Platform Settings page layout: widen the form, replace a misused
`Field` wrapper with an `AppCard` for the platform-default GitHub Copilot
connection status, and use `PageSection`'s `description` prop instead of a
manually-styled paragraph.

Also default the BYOK provider picker to Azure (the most common deployment
choice) and clarify the Base URL hints per provider: "Azure" requires a bare
Azure OpenAI resource endpoint with no path, while "OpenAI-compatible" accepts
any full endpoint URL including a path (e.g. a Foundry project endpoint or an
Azure OpenAI `/openai/v1` endpoint).
