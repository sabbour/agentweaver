# Identity Broker permissions

The Identity Broker is Agentweaver's sign-in service. During sign-in, it lists
the permissions an app requests. Approving lets the app ask Agentweaver to
perform those actions. It does not give the app access to every project or add
a project role.

Agentweaver registers permissions for each app. The Web app requests the
permissions below. Other apps may request a different set.

## `openid`

Lets Agentweaver confirm who you are. It lets you sign in and connects your
sign-in to your Agentweaver account. The sign-in service uses an account ID to
recognize you.

## `profile`

Requests your profile details. Agentweaver does not include profile details in
the sign-in tokens it gives to the Web app.

## `email`

Requests your email address. Agentweaver does not include your email address in
the sign-in tokens it gives to the Web app.

## `offline_access`

Lets the app renew your sign-in while the page is open. The sign-in service can
give the app a renewal token so it can ask for new sign-in tokens without
asking you to sign in each time. The Web app keeps tokens in memory. Reloading
the page clears them, so you may need to sign in again.

## `api.read`

Lets the app send requests to Agentweaver. This permission alone does not give
the app access to a project. Agentweaver checks your current project access and
whether the requested action is allowed.

## `projects.admin`

Lets the app ask to manage a project. These requests also need `api.read`.
Agentweaver checks your project access and role before each action. This
permission does not make you a project administrator or add a role.

## `projects.orchestrator`

Lets the app work with project runs. Requests for a run also need `api.read`.
Agentweaver checks your current project access, that you are assigned to manage
runs, and that the saved settings match the requested project and run. This
permission alone does not give you project access or a role.

To start a new chat, the app first calls create with a token that is not bound
to a run. It persists the pending run ID across a top-level PKCE redirect with
`prompt=none`, `project_id`, and `run_id`, then exchanges the code for a
run-bound token and calls start. With an existing Broker session and matching
consent, this needs no UI. On `login_required` or `consent_required`, the client
falls back to interactive sign-in without retrying silently. Cancel also
requires the run-bound token. A tenant admin grants the caller the project
Orchestrator role through the project role assignments API.

## OpenID Connect references

OpenID Connect is the standard Agentweaver uses for sign-in. It defines the
`openid`, `profile`, and `email` permissions. See its
[scope definitions](https://openid.net/specs/openid-connect-core-1_0.html#ScopeClaims).
Microsoft Learn explains
[OpenID Connect scopes](https://learn.microsoft.com/en-us/entra/identity-platform/scopes-oidc),
including `offline_access`. The sign-in provider sends profile and email
details to Agentweaver for account display, but Agentweaver's tokens for the
Web app include only an account ID.

## Unknown scopes

If a permission is not listed here, the app or service that defines it decides
what it allows. Ask your administrator before approving it.
