---
"agentweaver": patch
---

Fix multi-GitHub-account sign-in in Entra mode: linking an additional GitHub account now forces GitHub's account picker (`prompt=select_account`) instead of silently re-authorizing the account already linked, and the active (default) linked account's token is now what the rest of the platform resolves. Legacy per-user token scopes are transparently rewritten onto the caller's active linked identity — restoring Copilot entitlement, session starts, and generation for Entra users — and the AgentHost pod is handed the active identity's Key Vault secret.
