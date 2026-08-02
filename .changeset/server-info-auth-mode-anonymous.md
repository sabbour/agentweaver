---
"agentweaver": patch
---

fix(auth): make `/api/server/info` genuinely anonymous and return the configured auth mode

`GET /api/server/info` is what the web app calls before sign-in to decide whether to show
the Entra or GitHub sign-in button, but it was unreachable and incomplete:

- Despite `.AllowAnonymous()`, the custom bearer-token (`GitHubTokenAuthMiddleware`) and
  GitHub-org authorization middlewares keep their own hardcoded anonymous-path allowlists
  and never consulted endpoint metadata, so every unauthenticated call got a 401.
- The response body omitted `auth_mode` / `auth_mode_label` / `auth_mode_recommended`
  entirely, so even a successful call could not report the deployment's auth mode.

The frontend defaults to `github-legacy` whenever the field is missing or the call fails,
so Entra deployments (`AUTH_MODE=Entra`) silently showed "Sign in with GitHub". The
endpoint is now exempt in all auth middlewares (and marked public in the OpenAPI document)
and returns the auth mode resolved through the existing `AuthModeResolver`.
