# Account connections

Project settings separate GitHub repository access from GitHub Copilot model
access. Connecting one does not connect the other.

## Connect the GitHub Repo App

1. Open `/projects/:projectId/settings`.
2. Use the GitHub Repo App connection panel.
3. Complete GitHub authorization in the tracked popup.
4. Let the panel reload status from the Identity Broker.
5. Select the connection in project configuration with
   `sourceControl.authMode: "githubApp"` and `sourceControl.appConnectionId`.

The connection ID is an opaque Identity reference. Direct Broker repository
responses include installation and repository IDs as metadata. They do not
include provider tokens.

The panel uses direct, cookie-authenticated Broker requests. JSON mutations
obtain `/auth/github/repo-app/csrf` and send `X-CSRF-TOKEN`. Connect and install
use native form posts with `__RequestVerificationToken`, not fetch requests that
follow OAuth redirects.

## Connect GitHub Copilot

1. In project settings, use **GitHub Copilot connection**.
2. Start authorization and complete the tracked popup.
3. Wait for the completion request and refreshed owner status.

The callback is `/auth/github/copilot-app/callback`. This flow requires the
Gateway on the same origin as the web app for its link cookie. Status and
revision checks bind the returned connection to the selected project.

## Pin a repository for an existing run

1. Open the run's **Selection** tab.
2. If its accepted configuration uses `githubApp`, open the repository setup
   controls.
3. Complete App installation or authorization when required.
4. Refresh repository choices and select the intended repository.
5. Submit the pin for the root session.

The browser exchanges the repository name for a short-lived selection code.
Orchestrator validates that code against the accepted repository and stores the
pin. Changing today's project configuration does not rewrite a run's accepted
connection.

## States and errors

- A popup callback is a notification, not connection proof. The panel reloads
  status from the owner.
- A blocked or closed popup, expired transaction, mismatched state, or failed
  owner response leaves an error or unverified state.
- Leaving settings or clearing the browser session cancels the panel's pending
  work and closes its tracked Repo App popup.
- Owner-reported connection status and revisions determine what the panel shows.
- A run pin response of `202` reports acceptance. Read the returned owner state
  before treating the repository as pinned.

## Requests

| Step | Surface and request |
| --- | --- |
| Repo App status | Broker `GET /auth/github/repo-app/status` |
| Get antiforgery token | Broker `GET /auth/github/repo-app/csrf` |
| Authorize or install | Broker `POST /auth/github/repo-app/connect` or `POST /auth/github/repo-app/install` |
| List repositories | Broker `GET /auth/github/repo-app/repositories` |
| Select repository | Broker `POST /auth/github/repo-app/selection` |
| Disconnect Repo App | Broker `POST /auth/github/repo-app/disconnect` |
| Start or complete Copilot authorization | Gateway `POST /api/connections/copilot-user/v1/begin` or `POST /api/connections/copilot-user/v1/complete` |
| Read Copilot status | Gateway `GET /api/connections/copilot-user/v1/{connectionId:guid}` |
| Pin selected repository | Gateway `POST /api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin` |

The Gateway also exposes connection APIs for non-UI callers. See
[HTTP API](../reference/api.md), [Identity and secrets](../architecture/identity-secrets.md),
and [web callback handling](../reference/web.md#authentication-flow).
