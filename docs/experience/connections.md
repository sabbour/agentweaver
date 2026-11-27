# Project connections

Use this page to manage GitHub Repo App, GitHub Copilot, Remote MCP, and BYOK
provider metadata for a project. These settings use separate owner services.

## Prerequisites

- Sign in through the Identity Broker and open **Project settings** >
  **Connections**.
- Allow the browser to open a tracked popup for GitHub authorization.
- For GitHub Copilot, open the app over HTTPS and use a Gateway on the same
  origin as the web app.

## View available platform BYOK providers

Open **Project settings** > **Connections** and find **Available platform
providers**. Any project reader sees platform BYOK providers in `Available`
state from `GET /api/v1/projects/{projectId}/configuration/choices` without
signing in for project-bound draft management.

Each row shows the provider name, type, model, supported reasoning efforts,
optional model reference, and an **Active default** badge when it is the
platform default. Platform administrators manage these providers; run start still
enforces project model selection, policy, and egress rules.

## Manage project BYOK provider drafts

Open **Project settings** > **Connections** and find **BYOK provider drafts**.
The page lists saved providers. Select one to read its metadata, or select
**Add provider draft** to create one. Select **Load more** to see another page.

Enter a name, provider type, base URL, and model or deployment. You can also
set the wire API, Azure API version, reasoning efforts, and prompt capacity.
Select **Create draft** to save. The owner creates the provider ID. Use that ID
to find the provider later; you can change its name.

To edit a provider, open it and select **Edit metadata**. The page shows the
provider ID, state, revision, credential status (**Key saved** or **No key
saved**), host-request status, host, and port from the server response. Select
**Refresh status** to reload the current detail and host-request status without
saving the provider. Select **Save metadata** to save changes. The create and
edit metadata forms do not accept or store API keys or other credentials.

In the **BYOK provider drafts** list, a `Draft` provider with both saved
rate-card fields set to `null` shows **Pricing required**. Omitted, partial,
or malformed rate-card fields show **Pricing status unavailable**. A valid
rate-card ID and version show no pricing chip. An `Available` provider with
two `null` fields shows no chip. The chip describes the saved project draft
link only.

When a provider shows **No key saved** on the detail view, you can enter its
first API key in **Add API key** and select **Save key**. The browser clears the
key field before sending the request once without automatic retry. It does not
save the key in browser storage. Key rotation and key removal are not available
in this view.

If a key save returns an incomplete or pending state, the page keeps the
operation ID in memory for your current signed-in account, tenant, and project:

- Select **Check key status** to query the operation status without re-entering
  the key.
- If publication is pending, select **Retry publication** to recheck
  publication without re-entering the key. If the provider changed before
  publication finished, share the shown operation ID with an operator.
- If a write is quarantined, share the shown operation ID with an operator.

In-memory operation tracking survives navigation between providers, settings
sections, and same-account re-authentication, but a full browser reload or tab
close clears it.

| Host-request status label | Meaning |
| --- | --- |
| **Waiting to send** | The request is queued and has not reached Environment yet. |
| **Received by Environment** | Environment received the request for the shown host and port. |
| **Delivery failed** | Delivery to Environment failed. |
| **Status unavailable** | The response did not include a matching status. |

This status describes the host request only. It does not approve the host,
enforce network policy, or show that the provider is ready for runs.

Projects checks your current role for every request. You need to be a project
Owner or PlatformAdmin. The browser requests access for this project with no
run binding. If the page asks you to sign in, select **Sign in for project
access**. Use **Refresh**, **Refresh status**, or **Retry** when a request
fails. An empty list says **No provider drafts are set up for this project.**
After a failed list or detail read, the page hides old data and shows **Retry**.
If the project session expires or returns `401` or `403`, the page clears
loaded rows, selection, and editor fields, and asks you to sign in for project
access again on `401`. Sign-in returns you to the list.

After a save error, the form keeps your values. An unchanged retry by the same
signed-in user, tenant, and project uses the same operation ID. If an update
returns `409`, select **Discard edits and reload latest**, then review the
current provider and reapply your changes. After a save succeeds, editing stays
disabled until the current revision reloads.

## Connect GitHub Repo App

When you create a new project from a GitHub repository, the dialog uses the
connected account it displays. You do not need to copy the connection ID. The
dialog enables creation only after the Broker reports a connected account with
a usable ID, and it checks that the same ID is still current before it creates
the project. If the ID changes, review the updated account and select
**Create project** again. **Blank project** does not need a GitHub connection.

The saved `sourceControl.identityConnectionId` is a stable reference to the
Identity connection. It does not prove access to a repository, clone it, or pin
it to a run. Project creation does not create a repository-selection code.

For an existing project, use these steps to connect the account and set its
binding manually:

1. Open `/projects/:projectId/settings?section=connections`.
2. In **GitHub Repo App**, select **Connect GitHub Repo App** or
   **Reauthorize GitHub Repo App**.
3. Complete GitHub authorization in the tracked popup.
4. Wait for the app to reload connection status from the Identity Broker.
5. In **Repository**, set **Sign-in method** to **GitHub App**. In
   **Advanced**, set `sourceControl.identityConnectionId` to the matching
   Identity connection ID. Remove API and checkout secret references. Keep the
   webhook secret reference and other configuration, then select **Save
   changes** at the current revision.

**Expected result:** The Broker reports the connection state. A popup
callback alone does not confirm a connected account. Select
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
- If the Repo App provider is not configured, the Broker status request returns
  `503 github_repo_app_not_configured` after the session and owner checks. The
  Web app asks an operator to configure Identity Broker. Follow
  [the provider setup procedure](../guide/deploy-to-azure.md#configure-the-optional-github-providers);
  do not retry the connection until setup is complete.
- A project configured for a different App connection cannot use the displayed
  connection until you update `sourceControl.identityConnectionId`.

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
These route names describe checked-in source contracts, not service
availability in your environment. See
[HTTP API](../reference/api.md), [Identity and secrets](../architecture/identity-secrets.md),
and [web callback handling](../reference/web.md#authentication-flow).
