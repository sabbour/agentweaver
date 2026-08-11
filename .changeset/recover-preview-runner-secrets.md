---
"agentweaver": patch
---

Recover soft-deleted Key Vault preview-runner credential keys before retrying a launch. Recovery is bounded, safe under concurrent creators, preserves purge protection, and rotates to a fresh credential without logging secret values.
