---
"agentweaver": patch
---

Harden GitHub OAuth refresh-token and web sign-in `state` handling (security):

- **Fail closed on refresh org re-check.** `/oauth/token` refresh now denies (403) when the
  brokered GitHub token is missing/expired or the org-membership re-check is inconclusive, instead
  of silently falling back to the issuance-time org claim. A user removed from the required org can
  no longer keep minting access tokens through the refresh chain by revoking/expiring their GitHub
  token. The membership check runs on a non-consuming peek, so a transient (inconclusive) denial
  leaves the refresh token usable once membership can be confirmed again; a definitive
  non-membership revokes the whole refresh chain.
- **Atomic single-use refresh-token consumption.** Refresh rotation now claims the presented token
  with a single conditional compare-and-swap (`ConsumedAt IS NULL`), so a concurrent replay of the
  same refresh token can no longer fork two independent live refresh branches; the loser triggers
  reuse detection and the chain is revoked.
- **No SAML bypass via public membership.** A SAML-enforcement (`403`) response on the authenticated
  private org-membership check is now treated as "SSO required" and is no longer overridden by the
  unauthenticated public-membership fallback.
- **Browser-bound OAuth `state`.** The web sign-in `state` is bound to the initiating browser via a
  Secure, HttpOnly, SameSite=Lax cookie (double-submit) and validated on callback, mitigating
  login-CSRF where an attacker grafts their pre-authorized `state`/`code` onto a victim's browser.
