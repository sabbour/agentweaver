# Identity Broker permissions

The Identity Broker is Agentweaver's sign-in service. During sign-in, it lists
the permissions an app requests. Approving lets the app ask Agentweaver to
perform those actions. It does not give the app access to every project or add
a project role.

Agentweaver registers permissions for each app. The Web app requests the
permissions below. Other apps may request a different set.

## CLI sign-in and token storage

The standalone CLI uses the statically registered public client
`agentweaver-cli`, authorization code with S256 PKCE, and the exact callback
`http://127.0.0.1:49173/oauth/callback`. Its default request is
`openid api.read offline_access`; it requests `projects.admin` or
`projects.orchestrator` only when explicitly needed for the selected operation
or journey. These scopes do not grant project roles.

The CLI keeps access tokens in memory and stores only refresh tokens in the
operating system credential store. It uses Windows Credential Manager, macOS
Keychain, or Linux Secret Service; Linux requires an installed, unlocked
Secret Service session. There is no plaintext fallback. `auth logout` removes
only the selected local entry and does not revoke a Broker token family or
another process's in-memory access token.

The persisted CLI registration has a Broker-specific permission to request any
scope currently or later registered in the Broker catalog. It is not a literal
`*` scope and does not add scope names to the catalog: the Broker still rejects
unknown scopes and keeps explicit consent. This lets the CLI request scopes
registered for another configured client, including `projects.admin` and
`projects.orchestrator`, without changing the other clients' permissions.
The Broker also attaches the resource audiences configured on each requested
scope, so the CLI may receive those audiences too. Because newly registered
scopes become requestable, scope owners must not protect internal resources
only with a per-client scope allowlist; each resource must independently check
the caller's current authorization. OAuth scopes still do not add project
roles or grants, and admin and orchestrator authority remain separate sessions.

Login, refresh, and logout for the same selected credential identity are
serialized across processes. An interrupted or uncertain refresh requires a
fresh sign-in rather than retrying a possibly consumed refresh token.

For API acceptance, interaction is off by default. Only `agentweaver test
--interactive` allows browser authorization, and it obtains exact project/run
authority only after the runner has set up its owned fixture and selected the
run. Setup and run authorization must have the same actor and tenant. See the
[CLI guide](./cli.md) for target configuration, acceptance results, and
cleanup recovery.

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

For the Web client, starting a new chat first calls create with a token that is
not bound to a run. It persists the pending run ID across a top-level PKCE
redirect with `prompt=none`, `project_id`, and `run_id`, then exchanges the code
for a run-bound token and calls start. With an existing Broker session and
matching consent, this needs no UI. On `login_required` or `consent_required`,
the client falls back to interactive sign-in without retrying silently. Cancel
instead uses a token that is not bound to a run and a current Contributor,
Owner, or tenant administrator role. Creating a chat requires the project
Orchestrator role (`acceptRunSelection`); Owner or Contributor alone receives
`403`. A tenant admin grants that role through the project role assignments
API.

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
