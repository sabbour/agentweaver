---
"agentweaver": patch
---

Remove the inert project-level GitHub identity override endpoints and dead frontend calls left
over after #934 removed the runtime override behavior: the `ProjectGitHubIdentityService` and the
`GET`/`PUT /api/projects/{id}/github-identity` endpoints are deleted, along with the corresponding
`getProjectGitHubIdentity`/`setProjectGitHubIdentityOverride` frontend API calls and types. The
`GitHubSignIn` "switch account" UI is unchanged and now unconditionally uses the per-user default
linked GitHub identity API instead of branching on a project override.

Note: the `project_github_identity_overrides` DB table, its entity, and
`ProjectGitHubIdentityOverrideStore` are intentionally kept (not deleted) — they are expected to be
repurposed as a "workflow Copilot owner" store, since GitHub App installation tokens have no
Copilot entitlements and automation runs still need a human user's Copilot-entitled token.
