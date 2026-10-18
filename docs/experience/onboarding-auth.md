# Sign in and access

Use this page to sign in to the Agentweaver v1 browser app and request access
to an exact project and run. The app signs you in through the Identity Broker;
you do not enter a browser API key.

## Prerequisites

- The Identity Broker URL and registered OAuth client settings are configured
  for the web app. See [runtime settings](../reference/runtime-configuration.md).
- The registered redirect URI and scopes match the app's configuration.
- Your identity has the current owner-granted access required for the operation.

## Sign in

1. Select **Continue with Identity Broker**.
2. Complete sign-in in the Broker popup. If the browser cannot open the popup,
   the app continues with a full-page authorization redirect.
3. If the Broker displays **Review access**, check the client ID and requested
   permissions.
4. Select **Approve** or **Decline**.
5. After approval, the app exchanges the authorization code and opens the
   authenticated project routes.

The app uses OAuth authorization code flow with PKCE. The PKCE verifier binds
the token exchange to the browser transaction; the `state` value matches the
authorization response to that transaction. Access and refresh tokens stay in
memory. The app stores only the short-lived PKCE transaction in session
storage so it can complete a full-page redirect. The authorization attempt
has a ten-minute client deadline.

**Expected result:** The app opens `/projects` after it establishes a Broker
session. If the Broker requests consent, the app continues only after you make
an explicit choice.

## Open an existing run

**Prerequisites:** You know the exact project ID and existing run ID.

1. Open the [project](./projects.md).
2. Enter the existing run ID in **Exact run ID** and select **Open run**.
3. On the run page, select **Sign in for this project and run**.
4. Complete the Broker flow.

The Broker validates current authority for that exact project and run. The app
does not reuse a token bound to another run. The Knowledge page uses the same
exact-run sign-in control.

**Expected result:** The run page loads only when the session's binding matches
the project and run in the route. The page then reads owner snapshots,
committed journal events, and usage.

## Clear the browser session

Select **Clear browser session** in the shell. The app clears its in-memory
session and current authorization context. It does not disconnect GitHub
connections or sign you out of the external identity provider.

After you reload, sign in again. The Broker can reuse its local session, but
the app rechecks current grants and roles. The browser returns to the sign-in
screen until you establish a new app session.

## Understand sign-in states and errors

| State or error | What you can do |
| --- | --- |
| Configuration error on the sign-in card | Configure the Broker URL, registered client ID, redirect URI, and scopes. See [runtime settings](../reference/runtime-configuration.md). |
| Waiting for Identity Broker | Finish the popup flow before the ten-minute client deadline. |
| Declined consent, invalid state, or unmatched callback | Start sign-in again. The app rejects callbacks without the matching transaction. |
| `401` on an API call | The app attempts one token refresh when a refresh token exists. If it fails, sign in again. |
| `403` or ambiguous tenant context | Use an identity with current owner-granted access. The app does not guess a tenant. |
| Run-bound sign-in prompt | Authorize the exact project and run shown by the page. |

## Requests

These sign-in routes belong to the Identity Broker except for the final
Gateway request.

| Operation | Request | Purpose |
| --- | --- | --- |
| Start authorization | `GET /connect/authorize` | Sends the registered OAuth settings, state, PKCE challenge, and optional exact project/run selectors. |
| Resume authorization | `GET /connect/authorize/resume` | Resumes the Broker's saved one-use authorization transaction. |
| Submit consent | `POST /connect/consent` | Sends your explicit decision with Broker antiforgery protection and cookies. |
| Exchange or refresh a token | `POST /connect/token` | Exchanges the code and verifier or refreshes the current session. |
| Read tenant context | `GET /api/v1/authorization/context` | Reads the current actor, tenant, binding, and effective authority. |

See [Identity and secrets](../architecture/identity-secrets.md) for the owner
boundary and [web authentication flow](../reference/web.md#authentication-flow)
for callback handling.
