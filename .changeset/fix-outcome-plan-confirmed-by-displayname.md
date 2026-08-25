---
"agentweaver": patch
---

Fix a regression where the "Outcome plan confirmed by ..." banner and lifecycle event still showed a raw Entra object ID (GUID) instead of a display name. PR #854 only covered the interactive human confirmation path (`ConfirmOutcomeSpecAsync`); Direct-mode auto-confirmation and autopilot/unattended outcome-spec confirmation (fresh runs, retried backlog-pickup runs, and run retries) still attributed `confirmedBy` to the raw `SubmittingUser` identity. These paths now carry a resolved human-readable display name (falling back to the raw identity only when no display name is known) so the GUID no longer leaks into the confirmation message.
