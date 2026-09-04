---
"agentweaver": patch
---

Return `project_model_provider_reconnect_required` from the project Copilot connection status endpoint when the binding's credential secret is stale or unusable, and update the web UI to say "Reconnect the project GitHub Copilot authorization used for unattended AI work" instead of the misleading "Try again later" message.
