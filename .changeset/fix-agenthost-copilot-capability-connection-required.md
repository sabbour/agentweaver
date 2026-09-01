---
"agentweaver": patch
---

Fixed the Operator Assistant (and other AgentHost-backed runs) surfacing an opaque
"AgentHost pod launch failed: Cannot launch AgentHost pod for run '...' without a live
run-bound Copilot capability snapshot" server error instead of the actionable "Connect your
GitHub Copilot account" prompt.

Root cause: a run's Copilot capability snapshot is captured against whichever GitHub Copilot
App binding is marked "Active" in the database, but nothing checks that the binding's
credential material still exists in Key Vault at that point. When a bound connection's
credential secret is missing or stale (confirmed live via
"Copilot App connection for project ... has an active binding record but its credential
secret is missing."), the snapshot is still accepted at run-preparation time. The actual
credential redemption only happens later, at AgentHost pod launch, where it fails and the
pod launcher threw a generic `InvalidOperationException` — which isn't recognized as an
actionable "reconnect GitHub" condition and gets wrapped into a generic 500 by every caller.

The AgentHost pod launcher now distinguishes a genuine wiring bug (no credential provider
registered at all, still fails loudly) from a configured provider that could not redeem the
run's credential, throwing the existing `GitHubCopilotConnectionRequiredException` in the
latter case so the API returns the standard `github_copilot_connection_required` 409 and the
frontend renders its established "Connect GitHub" call-to-action instead of a dead-end error.
