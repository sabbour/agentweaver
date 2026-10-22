# Account connections

Use this page to connect GitHub Repo App for repository access or GitHub
Copilot for project-scoped model access. These are separate connections:
connecting one does not connect the other.

## Prerequisites

- Sign in through the Identity Broker and open **Project settings** >
  **Connections**.
- Allow the browser to open a tracked popup for GitHub authorization.
- For GitHub Copilot, open the app over HTTPS and use a Gateway on the same
  origin as the web app.

## Connect GitHub Repo App

1. Open `/projects/:projectId/settings?section=connections`.
2. In **GitHub Repo App**, select **Connect GitHub Repo App** or
   **Reauthorize GitHub Repo App**.
3. Complete GitHub authorization in the tracked popup.
4. Wait for the app to reload connection status from the Identity Broker.
5. In **Repository**, set **Sign-in method** to **GitHub App**. In
   **Advanced**, set `sourceControl.appConnectionId` to the matching Identity
   connection ID. Select **Save changes**.

**Expected result:** The Broker reports the connection state. The app does not
treat a popup callback by itself as proof of a connected account. Select
**Browse repositories** to load repository metadata. The response contains
installation and repository IDs as metadata, not provider tokens.

The Repo App panel sends cookie-authenticated requests directly to the
Identity Broker. JSON mutations first request `/auth/github/repo-app/csrf` and
send `X-CSRF-TOKEN`. Connect and install use native form posts with
`__RequestVerificationToken`; they do not use fetch requests that follow
OAuth redirects. If `sourceControl.authMode` is omitted, the project remains
in legacy secret mode.

## Connect GitHub Copilot

1. In **Project settings** > **Connections**, select **Connect GitHub Copilot**.
2. Complete authorization in the tracked popup.
3. Wait for the completion request and refreshed owner status.

The callback route is `/auth/github/copilot-app/callback`. The flow uses a
same-origin link cookie, so the Gateway must share the web app's origin. The
app checks the returned project scope and connection ID before it accepts the
completion and status responses.

**Expected result:** The panel displays the status returned by the owner.
Authorization callback completion alone does not establish the final
connection state.

## Pin a repository to an existing run

**Prerequisites:** Open an existing run whose accepted configuration uses
`githubApp` and names the Repo App connection you connected.

1. Open the run's **Changes** tab. It shows **GitHub App repository access**.
2. If the connection matches, select **Load repositories**.
3. Complete GitHub App installation or authorization if the panel requests it.
4. Select the repository returned by the Identity Broker.
5. Select **Pin repository to this run**.

The browser exchanges the selected installation and repository IDs for a
short-lived selection code. The Orchestrator validates that code against the
accepted repository selection and returns the pin result for the root session.
Changing today's project configuration does not rewrite a run's accepted
connection. A `202` response means the owner accepted the pin request; it does
not prove that a runtime loaded or used the repository.

## Understand connection states and errors

- A popup callback is a notification. The app reloads status from the owner.
- A blocked or closed popup, expired transaction, mismatched state, or failed
  owner response leaves an error or unverified state.
- Leaving project settings or signing out cancels pending
  Repo App work and closes its tracked popup.
- The owner-reported connection state and revision determine what the panel
  displays.
- A project configured for a different App connection cannot use the displayed
  connection until you update `sourceControl.appConnectionId`.

## Requests

| Operation | Surface and request |
| --- | --- |
| Read Repo App status | Identity Broker `GET /auth/github/repo-app/status` |
| Get Repo App antiforgery token | Identity Broker `GET /auth/github/repo-app/csrf` |
| Authorize or install Repo App | Identity Broker `POST /auth/github/repo-app/connect` or `POST /auth/github/repo-app/install` |
| List repositories | Identity Broker `GET /auth/github/repo-app/repositories` |
| Select a repository | Identity Broker `POST /auth/github/repo-app/selection` |
| Disconnect Repo App | Identity Broker `POST /auth/github/repo-app/disconnect` |
| Begin or complete Copilot authorization | Gateway root `POST /api/connections/copilot-user/v1/begin` or `POST /api/connections/copilot-user/v1/complete` |
| Read Copilot status | Gateway root `GET /api/connections/copilot-user/v1/{connectionId:guid}` |
| Pin selected repository | Gateway `POST /api/v1/projects/{projectId}/runs/{runId}/source-control/sessions/{sessionId}/pin` |

The browser uses the Repo App status, repository, and selection routes directly
on the Broker. The Gateway also exposes connection APIs for non-UI callers.
These route names describe source contracts, not service availability; the
Identity Broker and Orchestrator sources are candidates. See
[HTTP API](../reference/api.md), [Identity and secrets](../architecture/identity-secrets.md),
and [web callback handling](../reference/web.md#authentication-flow).
