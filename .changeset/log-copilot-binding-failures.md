---
"agentweaver": patch
---

Log the underlying reason when a GitHub Copilot App binding attempt or connection status check fails with `github_binding_unavailable`, instead of silently swallowing the exception. This was previously undiagnosable in production because the failure path had no logging at all.
