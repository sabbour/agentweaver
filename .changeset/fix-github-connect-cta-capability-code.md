---
"agentweaver": patch
---

Fix "Connect GitHub" call-to-action not appearing when repository listing fails with
`github_capability_unavailable`. The shared `isGitHubRepoAppConnectionRequired` helper only
recognized `github_binding_unavailable`, so the "Create project from GitHub" dialog and the
"Connect existing repo" dialog (owner and repository pickers) fell back to a generic error with
a plain "Retry" button instead of offering to connect GitHub. Both codes now trigger the
"Connect GitHub" action.
