---
"agentweaver": patch
---

Remove static project-level GitHub identity override from CallerTokenScopeProvider. All agent runs now use the submitting user's own linked GitHub identity, eliminating agenthost Copilot auth failures caused by stale or missing project-level tokens.
