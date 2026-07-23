---
"agentweaver": patch
---

GitHub org allowlist now accepts multiple orgs via config
(`Auth:GitHub:AllowedOrg` / `GITHUB_ALLOWED_ORG`).

The GitHub organization authorization gate previously enforced membership of a
single, exact-match org. It now parses `Auth:GitHub:AllowedOrg` as a delimited
LIST (split on `,` and `;`, trimmed, empty entries dropped, de-duplicated
case-insensitively, order preserved) and authorizes a caller who is a member of
**any** listed org. For each allowed org the existing two-step check is applied
verbatim (authenticated `/orgs/{org}/members/{login}`, then the unauthenticated
`/orgs/{org}/public_members/{login}` SAML fallback). Fail-closed behavior is
unchanged: empty/whitespace config yields an empty list and blocks every
non-exempt request. When no org confirms membership but at least one org's
primary authenticated check was inconclusive (expired token / 5xx / network),
the result is `Inconclusive` rather than a hard denial, preserving the
refresh-time re-check semantics. The single-org list parser is shared by the
authorization service, the org-authorization middleware, and the API-key
middleware.

The value is now config-driven and non-committed: it flows from the deploy-time
`GITHUB_ALLOWED_ORG` environment variable through the `agentweaver-runtime-config`
ConfigMap into the API and worker deployments (mirroring `GITHUB_CALLBACK_URL`).
Committed defaults remain `microsoft`.
