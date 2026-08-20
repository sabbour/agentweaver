---
"agentweaver": patch
---

feat(auth): add adopt-session-token endpoint for GitHubLegacy mode

Adds `POST /api/auth/github/adopt-session-token` so that callers already
authenticated with a GitHub bearer token in GitHubLegacy mode can promote
that token into the `IGitHubTokenStore` without requiring a separate
device-flow sign-in. This unblocks GitHub-origin project operations
(clone, webhook provisioning) for GitHubLegacy deployments.
