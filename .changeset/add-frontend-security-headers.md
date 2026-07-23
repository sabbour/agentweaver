---
"agentweaver": patch
---

Added a Content-Security-Policy and defense-in-depth security headers
(`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`) to
the `Agentweaver.Web` static host response pipeline, addressing a Low-severity
security-assessment finding (missing security headers/CSP). The CSP is
same-origin (`default-src 'self'`) with a strict `script-src 'self'` (no
`unsafe-inline`/`unsafe-eval`) and `style-src 'self' 'unsafe-inline'` (required
by @fluentui/react-components' runtime style injection).

Also documented the accepted residual risk for the companion Low-severity
finding (OAuth session token stored in `sessionStorage`, JS-readable) with a
code comment in `apps/web/src/config.ts` — the token is not duplicated across
storage locations or logged today, and a full migration to an HttpOnly/Secure
session cookie (which also requires adding CSRF protection) is tracked as a
separate, larger follow-up rather than attempted in this pass.
