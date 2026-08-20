---
"@agentweaver/api": patch
---

feat(auth): add adopt-session-token endpoint for GitHubLegacy mode

Adds POST /api/auth/github/adopt-session-token so that callers already
authenticated with a GitHub bearer token can promote that token into the
IGitHubTokenStore without requiring a separate device-flow sign-in.
Only available in GitHubLegacy auth mode.
