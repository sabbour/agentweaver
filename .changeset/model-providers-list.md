---
"agentweaver": minor
---

Replaced the single-slot "BYOK provider" setting in Platform Settings with a "Model providers" list, matching the GitHub Copilot desktop app's Model providers dialog. GitHub Copilot is always shown first as a built-in, non-removable entry. Admins can now add, edit, and remove multiple custom providers (Custom endpoint, Azure OpenAI, Anthropic) via an "+ Add provider" picker and inline type-specific forms, and mark exactly one configured provider as "active" for inference — preserving the existing single-active-provider runtime behavior while allowing several providers to be pre-configured and kept ready with their saved API keys.
