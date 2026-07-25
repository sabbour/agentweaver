---
'agentweaver': patch
---

Fix a bug where GitHub org-membership checks could intermittently return a
hard "authorize SSO" 403 (`OrgAuthResult.OrgAccessNotGranted`) even for
callers whose org membership is genuinely public. When the primary
authenticated membership check hit SAML-enforcement (403), the fallback
unauthenticated `public_members` check's own rate-limit responses were
silently treated as a confirmed "not a public member" instead of a
retryable inconclusive result. The fallback's rate-limited/inconclusive
result is now correctly surfaced as `Inconclusive` so the caller retries
instead of hard-denying access.
