---
"agentweaver": minor
---

Add the missing interactive Entra ID browser sign-in flow: `GET /auth/entra/authorize` and `GET /auth/entra/callback` endpoints implementing the Microsoft identity platform v2.0 authorization-code-with-PKCE flow, with CSRF-protected state, server-side PKCE code_verifier storage, and one-time session-exchange codes (no tokens ever placed in redirect URLs). Also exempts `/api/auth/session/exchange` from platform role authorization so anonymous session bootstrap works in Entra mode.
