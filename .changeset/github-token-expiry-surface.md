---
"agentweaver": patch
---

Surface GitHub token expiry prominently and improve renewal resilience.

- Show a warning banner and Re-link CTA in the UI when a GitHub OAuth token has expired or been revoked, so users know why coordinator runs fail
- Fix entitlement probe endpoint (switched from `copilot_internal/v2/token` to `GET /models`) so Copilot entitlement status displays correctly for all linked accounts
- Distinguish transient (network, 5xx) vs permanent (expired token, bad credentials) refresh failures — transient failures no longer sign the user out
- Add proactive background refresh service that renews expiring tokens up to 2 hours before expiry, preventing mid-run token failures
- Fix AgentHost sandbox executor to survive concurrent claim deletion during coordinator runs
