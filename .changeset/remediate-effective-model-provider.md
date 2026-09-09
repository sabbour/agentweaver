---
"agentweaver": patch
---

Report truthful model-provider context for supported AI actions. Agentweaver revalidates provider selection immediately before model calls and persists redacted execution provenance.

If the provider changes, Agentweaver returns `409 model_provider_changed` before invocation. The UI distinguishes Expected, Using, and Used provider context.
