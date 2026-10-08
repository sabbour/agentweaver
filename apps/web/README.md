# Agentweaver Web (v1)

This React 19 and Fluent UI client uses the Identity Broker and the versioned
Gateway/BFF. It is separate from the legacy 0.x run-submit/watch/review app.
See the [v1 web client guide](../../docs/guide/web-client.md) for its routes,
authorization boundary, supported surfaces, and current contract limitations.

## Configuration and development

Copy `.env.example` to `.env` and set the registered Identity Broker URL, OAuth
client ID, and scopes. The redirect URI defaults to the current origin's
`/auth/callback`; `VITE_GATEWAY_URL` defaults to `/api/v1`.

From the repository root:

```powershell
npm --prefix apps/web ci
npm --prefix apps/web run dev
npm --prefix apps/web run typecheck
npm --prefix apps/web test
npm --prefix apps/web run coverage
npm --prefix apps/web run lint
npm --prefix apps/web run build
```

The source client is not evidence that the Gateway, Broker, or any owner service
has been deployed.
