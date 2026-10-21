# Sign in and access

Use this page to sign in to the Agentweaver v1 browser app and request access
to a run. The app signs you in through the Identity Broker;
you do not enter a browser API key.

## Prerequisites

- The Identity Broker URL and registered OAuth client settings are configured
  for the web app. See [runtime settings](../reference/runtime-configuration.md).
- The registered redirect URI and scopes match the app's configuration.
- Your identity has the current owner-granted access required for the operation.

## Sign in

1. On the sign-in card, select **Sign in**.
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

**Expected result:** The app opens the page you requested. `/` opens
**Overview**. After a full-page redirect, the app opens **Projects**. If the
Broker requests consent, the app continues only after you make an explicit
choice.

## Open an existing run

You don't type a run ID. Open a run from a link or a notification, or select
**Open** next to a run on **Sessions**, **Overview**, the project
**Dashboard**, or **Orchestrations**.

1. Open the run. The page shows **Opening this run…** while the app signs in
   to the run silently.
2. If the silent sign-in fails, select **Sign in for this project and run**
   and complete the sign-in.

The Broker validates current authority for that project and run. Each run has
its own sign-in; the app keeps your project sign-in and does not reuse a token
bound to another run. When you leave the run page, the app switches back to
your project sign-in. The **Memories** page uses the same run sign-in when you
choose a run that your current sign-in doesn't cover.

**Expected result:** The run page loads only when your sign-in matches the
project and run in the address. The page then reads owner snapshots,
committed journal events, and usage.

## Sign out

Select **Account** in the navigation rail, then select **Sign out**. You can
also select **Sign out** in **Account settings** (`/settings`). The app clears
its in-memory session and its current authorization context. It does not disconnect GitHub connections or sign
you out of the external identity provider.

After you sign out, the app shows the sign-in card. If you reload the page
without signing out, the app can restore your sign-in silently when the Broker
still has a valid session and consent. The app rechecks current grants and
roles. If the Broker needs your input, the app shows the sign-in card.

## Understand sign-in states and errors

| State or error | What you can do |
| --- | --- |
| Configuration error on the sign-in card | Configure the Broker URL, registered client ID, redirect URI, and scopes. See [runtime settings](../reference/runtime-configuration.md). |
| Waiting for Identity Broker | Finish the popup flow before the ten-minute client deadline. |
| Declined consent, invalid state, or unmatched callback | Start sign-in again. The app rejects callbacks without the matching transaction. |
| `401` on an API call | The app attempts one token refresh when a refresh token exists. If it fails, sign in again. |
| `403` or ambiguous tenant context | Use an identity with current owner-granted access. The app does not guess a tenant. |
| **Sign in for this project and run** button | The silent run sign-in failed. Select the button to sign in to the project and run shown by the page. |

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
