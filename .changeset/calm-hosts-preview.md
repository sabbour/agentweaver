---
"agentweaver": patch
---

Validate the run-bound Copilot credential before AgentHost launch, preserve actionable provider failures instead of masking them with workflow validation, make missing-claim cleanup idempotent, retain the Build & Test fallback for provider-ready direct code runs, and add correlated sanitized failure diagnostics.
