# Sign-in and access

The sign-in screen opens before the project pages. It uses the Identity Broker,
not a browser API key.

## Sign in

1. Select **Continue with Identity Broker**.
2. Complete the Broker's external sign-in in the popup. If a popup cannot open,
   the client uses a full-page authorization redirect.
3. If **Review access** appears, inspect the client ID and requested permissions.
4. Select **Approve** or **Decline**.
5. After approval, the client exchanges the authorization code and opens the
   authenticated routes.

The client uses OAuth authorization code flow with PKCE. Access and refresh
tokens stay in memory. The PKCE transaction uses session storage so a full-page
redirect can complete; tokens are not stored there.

## Open a run

1. Open the [project](./projects.md).
2. Enter an existing run ID.
3. Select **Sign in for this project and run** on the run page.

The Broker validates current authority for that exact pair. A token for another
run is not reused. Knowledge uses the same exact-run sign-in control.

## Clear the browser session

Select **Clear browser session** in the shell. This clears the app's in-memory
session and current authorization context. It does not disconnect GitHub
connections or sign out the external identity provider.

After a reload, continue sign-in again. The Broker can reuse its local session,
then rechecks current grants and roles.

## States and errors

| State | What to do |
| --- | --- |
| Configuration error on the sign-in card | Configure the Broker URL, registered client ID, redirect URI, and scopes. See [web configuration](../reference/web.md#configuration). |
| Waiting for Identity Broker | Finish the popup flow. Authorization has a ten-minute client deadline. |
| Declined consent, invalid state, or unmatched callback | Start sign-in again. The client rejects callbacks without the matching transaction. |
| `401` on an API call | The client attempts token refresh once when a refresh token exists. If recovery fails, sign in again. |
| `403` or ambiguous tenant context | Use an identity with current owner-granted access. The client does not guess a tenant. |
| Run-bound sign-in prompt | Authorize the exact project/run shown by the page. |

## Requests

These routes are on the Identity Broker except the final Gateway call.

| Step | Request | Purpose |
| --- | --- | --- |
| Start authorization | `GET /connect/authorize` | Send registered OAuth settings, state, PKCE challenge, and optional exact project/run selectors. |
| Continue external login | `GET /connect/authorize/resume` | Resume the Broker's saved one-use authorization transaction. |
| Decide consent | `POST /connect/consent` | Submit the explicit decision with Broker antiforgery protection and cookies. |
| Exchange or refresh | `POST /connect/token` | Exchange the code and verifier, or refresh the current session. |
| Load tenant context | `GET /api/v1/authorization/context` | Read current actor, tenant, binding, and effective authority. |

See [Identity and secrets](../architecture/identity-secrets.md) for the owner
boundary and [web reference](../reference/web.md#authentication-flow) for callback handling.
