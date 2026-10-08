# v1 web client

The v1 web client is a React application over the Identity Broker and the
versioned Gateway/BFF. The browser performs OAuth authorization and consent
with PKCE through the Broker, keeps access and refresh tokens in memory, and
sends API and event-stream requests only to the Gateway.

```mermaid
flowchart LR
  Browser[Agentweaver web client]
  Broker[Identity Broker]
  Gateway[Gateway / BFF]
  Projects[Projects & Config]
  Orchestrator[Orchestrator]
  Events[Events & Sessions]
  Knowledge[Knowledge]

  Browser -->|PKCE authorization, consent, token refresh| Broker
  Browser -->|Bearer API requests and ordered SSE| Gateway
  Gateway --> Projects
  Gateway --> Orchestrator
  Gateway --> Events
  Gateway --> Knowledge
```

The client does not send project, run, or session identity as a substitute for
authorization. The Broker issues tokens for the requested scope, and the
Gateway and owner services validate access on each request. Run-bound pages
require a token for that exact project and run. Structured Gateway error codes
and owner rejection responses remain visible instead of being treated as
success.

## Routes and behavior

| Route | Behavior |
| --- | --- |
| `/projects` | List projects visible to the signed-in identity and create a project. Creation does not assign an Owner. |
| `/projects/:projectId` | Read project state and open an existing run by its exact ID. |
| `/projects/:projectId/settings` | Read and append a revisioned project-configuration document. Owners validate the submitted configuration. |
| `/projects/:projectId/knowledge` | Search paginated Knowledge records for an exact project, run, and agent; proposal decisions and record revisions remain owner-authoritative. |
| `/projects/:projectId/runs/:runId` | Read run/session snapshots and journal events; view topology, outcomes and approvals, activity, accepted selection, and usage; send addressed messages or request supported tree actions. |

The run view polls authoritative owner snapshots and replays all journal pages
before opening the live event stream. It merges replay/live overlap by event ID
and journal position. Pending input, plan, outcome, and approval actions require
the exact request ID and decision version from the current owner snapshot.
Owner acceptance is not represented as delivery, completion, or a state
transition; the refreshed snapshot supplies the resulting state.
Knowledge search exposes owner-reported result pages rather than hiding records
after the first page.

## Configuration and commands

Configuration is provided through Vite environment variables. Start from
`apps/web/.env.example`:

| Variable | Default | Purpose |
| --- | --- | --- |
| `VITE_GATEWAY_URL` | `/api/v1` | Versioned Gateway base URL. |
| `VITE_IDENTITY_BROKER_URL` | unset | Identity Broker base URL. |
| `VITE_OAUTH_CLIENT_ID` | unset | Registered OAuth client ID. |
| `VITE_OAUTH_REDIRECT_URI` | current origin plus `/auth/callback` | Registered callback URI. |
| `VITE_OAUTH_SCOPES` | unset | Space-separated registered OAuth scopes. |

Run from the repository root:

```powershell
npm --prefix apps/web ci
npm --prefix apps/web run dev
npm --prefix apps/web run typecheck
npm --prefix apps/web test
npm --prefix apps/web run coverage
npm --prefix apps/web run lint
npm --prefix apps/web run build
```

## Contract limits

The Gateway exposes owner routes for accepting a supplied run-root identity,
proposing typed outcome/workflow/work-plan decisions, asking a coordinator
question, requesting approval, and registering, spawning, or forking child
sessions. These are owner-authoritative state operations; root or spawn
acceptance is not proof that a model run was scheduled or completed. The browser
client currently opens existing run IDs and does not expose root-acceptance,
proposal, or child registration/spawn/fork controls. It can message existing
sessions and request supported detach/archive actions. The Gateway does not
expose journal object-content reads, so the client shows opaque references
without inventing transcript content.

The accepted selection contains provider candidates and a model-selection
reference, not a provisioned provider pin or runtime SDK model ID. Usage is
returned as run totals without per-event model-binding details. The UI reports
those runtime identities as unavailable rather than inferring them from
configuration or usage. Usage totals distinguish priced and unpriced events
and do not replace missing measurements with zero.

This documentation describes the v1 source client only. It does not establish
that the web image, Gateway, Broker, or owner services have been published or
deployed.
