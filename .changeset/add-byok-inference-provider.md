---
"agentweaver": minor
---

Added a deployment-wide bring-your-own-key (BYOK) inference provider option. Platform admins can configure an OpenAI-compatible, Azure, or Anthropic provider (base URL, model, API key) via the new `/api/admin/byok-provider` endpoint; when configured, it is used for all inference execution paths (one-shot runs, sandboxed AgentHost runs for projects/schedules/webhooks) instead of GitHub Copilot, and no per-user Copilot credential is required. Also removed the duplicate legacy Foundry runner/factory/dispatcher in favor of the generic `ModelSource.Byok` provider path.
