---
"agentweaver": patch
---

Fully remove the inert project-level GitHub identity override surfaces left over after #934
removed the runtime override behavior: the `ProjectGitHubIdentityService`/`ProjectGitHubIdentityOverrideStore`
services, the `GET`/`PUT /api/projects/{id}/github-identity` endpoints, the
`project_github_identity_overrides` table (dropped via migration), and the corresponding
frontend per-project identity switcher UI. Callers must now use the per-user default linked
GitHub identity; there is no project-scoped override.
