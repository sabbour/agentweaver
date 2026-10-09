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

The production image runs the ASP.NET Core 10 host in `host/`. It serves the
Vite build as static files with SPA fallback and provides `/env-config.js` from
container environment variables, encoded as UTF-8 base64 values for the client.
Set `VITE_IDENTITY_BROKER_ISSUER` when its exact HTTPS issuer differs from the
full URL path in `VITE_IDENTITY_BROKER_URL`.
The Copilot callback path serves the same `index.html` with no-store and
no-referrer headers; the host suppresses request diagnostics that could record
callback query strings. Release packing publishes the framework-dependent host
from its locked NuGet dependencies and copies it with the Vite assets into the
digest-pinned ASP.NET runtime image.
Configure the Gateway with `Gateway:WebOrigin` set to this site's exact HTTPS
origin. See the [Gateway guide](../../docs/guide/gateway.md) for its
route-specific CORS policies and Copilot callback-cookie exception.

GitHub Repo App account status, repository discovery, selection, and disconnect
use direct cookie-authenticated Identity Broker requests with antiforgery tokens.
User authorization starts with a native form post targeted at the tracked popup,
not a fetch request. Run-bound installation and pinning remain exact
Gateway-authorized operations. The exact
`/settings/source-control?repoApp=connected` callback is captured and scrubbed by
the first inline script before assets load; the popup only notifies its opener,
which verifies status with the Broker.
