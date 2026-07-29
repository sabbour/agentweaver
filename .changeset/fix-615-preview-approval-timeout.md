---
"agentweaver": patch
---

Raise the default approval timeout for agent-initiated `start_preview` requests from 5 minutes to 15 minutes, and let operators override it with `Sandbox:Preview:ApprovalTimeoutMinutes` or `SANDBOX_PREVIEW_APPROVAL_TIMEOUT_MINUTES`.
