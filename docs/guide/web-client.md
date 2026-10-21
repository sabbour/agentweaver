# Use the v1 web client

The v1 Web client is a React application that uses the Identity Broker for
sign-in and the versioned Gateway for product API and event-stream requests.
It keeps access and refresh tokens in memory. GitHub Repo App account
authorization, repository discovery, and disconnect use cookie-authenticated
Identity Broker endpoints directly.

This article describes the checked-in v1 client and its contracts. The source
and route manifests do not prove that the Web image, Gateway, Broker, or owner
services are deployed or available.

## Before you begin

To run or build the client, install Node.js 24 and npm. To test sign-in and
product requests, you also need a deployed Identity Broker and Gateway, a
registered public Web client, its exact HTTPS callback URI, and a Web origin
allowed by the Gateway. Use the runtime configuration reference for the
deployment values.

## Understand how the client connects

The browser sends product API requests to the Gateway. The Gateway validates
the Broker token and forwards the original bearer to the owning service.
Owners check current membership, roles, bindings, and operation permissions.
The browser does not provide project, run, or session identity as a substitute
for authorization.

```mermaid
flowchart LR
  Browser[Agentweaver web client]
  McpClient[MCP clients]
  Broker[Identity Broker]
  Portal[AKS-managed wildcard HTTPS listener with exact-host routes]
  Web[Web service]
  Gateway[Gateway / BFF]
  Mcp[MCP service]
  Projects[Projects & Config]
  Orchestrator[Orchestrator]
  Events[Events & Sessions]
  Knowledge[Knowledge]
  ToolSignIn[Remote tool sign-in service]

  Browser -->|HTTPS /api/v1 and exact /openapi/v1.json| Portal
  Portal -->|/api/v1 and exact /openapi/v1.json| Gateway
  McpClient -->|HTTPS /mcp| Portal
  Portal -->|/mcp| Mcp
  McpClient -->|HTTPS protected-resource metadata| Portal
  Portal -->|/.well-known/oauth-protected-resource/mcp| Mcp
  Browser -->|HTTPS /| Portal
  Portal -->|/| Web
  Gateway -->|Live event SSE: bearer only| Events

  Browser -->|PKCE authorization, consent, token refresh| Broker
  Browser -->|Bearer API requests and ordered SSE| Gateway
  Browser -->|Repo App status, repositories, selection, disconnect: cookies + CSRF| Broker
  Browser -->|Native Repo App OAuth form in tracked popup| Broker
  Browser -->|Run-bound GitHub App installation and pin: bearer + exact tenant/run| Gateway
  Browser -->|Remote tool status, setup and callback| Gateway
  Browser -->|Tracked sign-in popup| ToolSignIn
  Gateway -->|Copilot connection BFF: optional tenant selector| Broker
  Gateway --> Projects
  Gateway --> Orchestrator
  Gateway --> Events
  Gateway --> Knowledge
```

The client uses these routes:

| Route | Purpose |
| --- | --- |
| `/` | Opens `/overview`. |
| `/overview` | Read your projects, **Recent runs in this browser**, and the items that need your input. |
| `/projects` | Browse project tiles and create a project. Creating a project does not make you its owner. |
| `/projects/:projectId` | Read the project **Dashboard**: setup readiness, recent runs, team, and quick actions. |
| `/projects/:projectId/orchestrations` | Read this project's runs from this browser and open a run. |
| `/projects/:projectId/team` | Read agents and edit an agent's role and charter. |
| `/projects/:projectId/settings?section=<key>` | Edit project settings by section, manage members, read history, and manage connections. |
| `/projects/:projectId/skills` | Manage Skill sources, imported revisions, and agent assignments. |
| `/projects/:projectId/memories` | Search and manage Knowledge records and transfer bundles for a run and an agent. `/knowledge` opens the same page. |
| `/projects/:projectId/runs/:runId` | Use the **Chat**, **Topology**, **Approvals**, **Activity**, **Changes**, **Usage**, and **Run settings** tabs. The `view` query selects a tab, such as `?view=approvals`. |
| `/settings` | Sign out and manage the GitHub Repo App account. |

## Navigate the client

The v1 client keeps the 0.x navigation rail, project switcher, floating content
panel, gallery tiles, dialogs, typography, and theme. Its views use v1 service
contracts instead of the 0.x API.

The rail has **Overview** and **Projects**. **Overview** is the home page. It
shows a summary of your projects, **Recent runs in this browser**, and **Needs
attention**: the questions and approvals that need your input in the run you
have open. The notification bell in the shell lists the same items.

Choose **Projects** to open the project gallery. Each tile shows the project
state, revision, and your access: **Can edit**, **Can view**, or **No access**.
The client reads access from your current authorization context. Select
**Create project** to open the creation dialog. Enter a **Project name** and
choose **Start from**: **Blank project** or **GitHub repository**. For a
GitHub repository, enter `owner/name` or a `github.com` address. The client
creates the project and then saves the repository in its configuration with
**GitHub App** sign-in. If you don't have access to the new project yet, the
project is kept, and a note says to add the repository in **Settings >
Repository** later. Creating a project does not make you its owner.

Within a project, use these destinations:

| Destination | Route | What you can do |
| --- | --- | --- |
| Dashboard | `/projects/:projectId` | Read setup readiness from the saved configuration, team and skills summaries, and recent runs. Use quick actions to finish setup or open Orchestrations and Memories. |
| Orchestrations | `/projects/:projectId/orchestrations` | Read this project's runs from this browser with a status summary. Select **Refresh** to reload. |
| Agents | `/projects/:projectId/team` | Read the **Total**, **In the team**, and **Not in the team** counts. Filter the agent tiles. Open an agent to read its ID, team position, and skills, or edit its role and charter. |
| Skills | `/projects/:projectId/skills` | Browse marketplace sources, preview and import Skills, read the inventory, and assign Skills to agents. |
| Memories | `/projects/:projectId/memories` | Choose a run and an agent under **Show memories for**, then work with records and revisions. |

Open **Settings** in the rail to reach **Account settings** or **Project
settings**. In project settings, use **Connections** for the GitHub Repo App,
Copilot, and Remote MCP. Existing `/orchestrations/:runId` links keep their
query parameters when they open the run.

### Find a run

You don't type a run ID. A run appears in **Recent runs in this browser**
after you open it once in this browser, for example from a link or a
notification. The list appears on **Overview**, the project **Dashboard**, and
**Orchestrations**. Each row has **Open** and **Remove**. **Remove** takes the
run off the list only; the run doesn't change. An empty list shows **Runs you
open appear here.** Signing out clears the list. The list is stored in this
browser and holds only IDs and the last state read.

The service has no run list API, so the client can't list every run. It has no
API to start a run, so the browser can't start a new chat or run. It has no
project metrics API, so the Dashboard shows no metrics. **Agents** has no
**Cast**, **Add**, or **Sync** controls, because the Gateway doesn't expose
casting.

### Unavailable destinations

Board, Flow, Workspace, Workflows, Observability, Diagnostics, Heartbeat,
Cluster, Assistant, Platform settings, and Sessions are not navigation choices.
Their data isn't exposed by this client's v1 APIs. Old links show one **Not
available in this version** page with a reason and a link back. Unknown
addresses show **Page not found**. Neither page invents data or calls the old
monolith.

See the [UI parity inventory](../reference/web-ui-parity.md) for the retained
patterns, v1 adaptations, and remaining limits.

The run page polls owner snapshots and replays journal pages before it opens
the live event stream. It merges overlapping replay and live events by event
ID and journal position. A pending action requires the exact request ID and
decision version from the latest owner snapshot. Owner acceptance is not shown
as delivery, completion, or a state transition; the refreshed snapshot shows
the resulting state.

## Configure and verify the client

1. Add or update an explicitly seeded public OAuth client in the approved
   Identity Broker configuration. The Broker does not support dynamic client
   registration. Set the redirect URI to the exact Web origin followed by
   `/auth/callback`, and use the registered scopes and client ID.
2. Set the Broker URL and issuer to the approved HTTPS values. Set the Gateway
   base path to `/api/v1` when the managed portal routes that same-origin path
   to Gateway; use another value only when it matches the reviewed route.
3. Open the client, complete the authorization-code flow with PKCE, and
   confirm that the callback returns to the registered URI.
4. Make a versioned API request through Gateway. Confirm that the owner
   enforces current authorization for the requested operation.

The consent screen lists the scopes requested by the registered client.
See [Identity Broker scopes](./identity-scopes.md) for their meanings and the
authorization checks behind the Agentweaver scopes.

## Restore sign-in after a reload

Access and refresh tokens stay in memory. After a successful sign-in, the
current tab stores a non-secret marker in `sessionStorage`. If that marker is
present after a reload, the client makes one hidden-frame authorization request
with `prompt=none` and PKCE. The Broker returns an authorization code only when
the same registered client already has valid consent for the requested scopes.
The silent request does not show a consent screen or create or extend consent.

If the Broker or identity provider needs user interaction, the client clears
the marker and the pending PKCE transaction, then shows the normal sign-in
screen. It does not retry silently until the user signs in again. Signing out
also clears the marker. The marker does not contain tokens, and the client does
not put access or refresh tokens in browser storage, cookies, or the URL. The
authorization code remains in the standard callback URL for the code exchange.

## Configure browser origins

Gateway and Broker CORS are separate. Set `Gateway:WebOrigin` to the Web
application's exact HTTPS origin. Gateway CORS policies are route-specific;
they do not allow credentials except for the Copilot connection BFF's
callback cookie. Direct Repo App browser requests use a separate credentialed
allow list in the Identity Broker.

The client requests current tenant and authority context from
`GET /api/v1/authorization/context`. It does not infer a tenant from a project,
run, token, or usage data. The client supplies `X-Agentweaver-Tenant` only for
routes included in the Gateway allow list, including project configuration,
run coordination, Knowledge, run selection and usage, and finite journal
replay. Live event SSE, Repo App account calls, PKCE/session calls, and
unrelated APIs do not receive the selector. Owners recheck current authority
for every protected operation. The context response is not a permission grant
or a cached authorization decision.

## Edit project settings and read history

**Project settings** has a section rail: **General**, **Access**,
**Repository**, **Models**, **Platform components**, **Run limits**,
**Connections**, **History**, **Danger zone**, and **Advanced**. The `section`
query selects a section. An unknown value opens **General**. Each rail entry
has the 0.x section icon, and **Danger zone** has a **Risk** badge.

- **General** renames the project.
- **Access** shows your permissions in this project and a **Members** panel.
  The panel lists members when the service offers the member list. A tenant
  administrator or a project owner can read it. Only a tenant administrator
  sees **Add member** (member ID and role: **Owner**, **Contributor**,
  **Viewer**, or **Orchestrator**) and **Remove**. If the service isn't ready
  for role changes, the panel shows **Role changes aren't available yet.** If
  this version of the service doesn't offer the list, the panel says so, and
  **Access** shows only your permissions.
- **Repository** sets the repository owner, name, and sign-in method.
- **Models** sets model defaults and model overrides by task.
- **Platform components** changes the implementations the project chose for
  each platform component, as one implementation ID or a comma-separated
  priority order. With no project choices, platform defaults apply.
- **Run limits** sets limits for new runs. An empty field uses the platform
  limit.
- **Danger zone** archives or restores the project.
- **Advanced** edits the configuration JSON. Use it only when the other
  sections don't cover the setting.

**Repository**, **Models**, **Platform components**, **Run limits**, and
**Advanced** share one form with **Save changes**, **Reload**, and the current
revision. After you edit JSON in **Advanced**, save or reload before you use
the other sections.

Empty task fields in **Models** inherit their defaults. The supported task
categories are Planning, Review, Design, Security, Creative writing, Complex
work, Code writing, and Straightforward work. The owner accepts Low, Medium,
High, and Extra high reasoning effort. Model support remains subject to owner
validation. A model credential belongs to one model. When you change the
default model, the form clears the saved credential and shows **Choose a
credential for this model.** **Save changes** stays disabled until you enter
a matching credential.

Available model and provider choices are not returned through the current
Gateway catalog. Enter existing references. The client does not substitute a
hardcoded catalog.

Saving adds a configuration revision. Runs that already started keep their
settings. In **History**, enter a revision number and select **View revision**
to read its settings. Historical settings are read-only and don't replace the
current form.

## Manage Skills

The project's **Skills** destination can manage public marketplace sources,
browse pinned source revisions, preview and import Skills, and assign imported
revisions to agents in the current project cast.

The page keeps the 0.x layout. A stat strip shows **Assigned**, **Enabled**,
**Marketplace sources**, and **Agents**. The toolbar has **Import skill** and
**Browse marketplaces**; each opens a dialog. The **Catalog** tab lists the
imported skill inventory. The **Assignments** tab lists project assignments
and assigns a skill to agents. After an import, the page opens
**Assignments**.

Project settings links to Skills. Skill assignment uses the current
configuration revision, and the owner rejects stale updates. Skills does not
mount a second configuration editor.

For a marketplace Skill, the client sends source revision, resolved commit,
selected path, and (for import) the preview digest and idempotency key. The
owner reads and validates the selected files; the browser does not upload
those files through the Gateway. For a local Skill, the browser submits the
selected `SKILL.md` and text resources for owner preview, then imports only
after the owner returns a matching digest.

Assignment uses the imported revision and digest, the current configuration
revision, and selected agents from the current cast. The client checks the
returned configuration revision before it replaces the assignment's revision.
It disables duplicate assignment requests while a save is pending. The UI reports the
saved assignment; it does not claim that a runtime loaded the Skill.

The read-only Skill inventory displays owner-reported assignments, revision
metadata, source provenance, and revocation state. It uses bounded opaque
cursors and keeps permission errors visible. Inventory metadata does not
prove runtime loading; the current public API does not expose
runtime-loaded status.

## Manage Knowledge records

The **Memories** page searches owner-reported result pages for the project,
run, and agent you choose under **Show memories for**. The **Run** list holds
the runs you opened in this browser. The **Agent** list holds the agents in
the project configuration. Kind tabs filter by **All**, **Memories**,
**Decisions**, **Proposals**, or **Session context**. It displays immutable revision history and supports the
owner-authorized operations implemented for the record type:

- Correct or archive supported Memory and SessionContext records.
- Restore a historical revision as a new Active+Pending revision.
- Approve an eligible Active Pending or Legacy Decision.
- Supersede an Active Decision with another Active Decision from the same
  project and agent results.

The owner validates the selected current revision, replacement link, and
resulting state. Decision supersession requires an owner that supports the
versioned update contract.

Knowledge transfer exports only the exact project's and agent's Memory and
Decision records with their revision chains. Import targets that same project
and agent, rejects collisions instead of merging records, and creates
Active+Pending records. You must approve a Decision separately. A bundle can
contain at most 25 records and 500 revisions, and its size is limited to
1 MiB.

## Connect GitHub and Copilot accounts

Use project **Settings > Connections**. GitHub and Copilot keep their existing
authorization flows and revision checks.

The Repo App account control uses Identity Broker status and an opaque
`connectionId`. Project configuration selects the connection with
`sourceControl.authMode: "githubApp"` and
`sourceControl.appConnectionId`. Configurations without `authMode` retain
legacy secret mode. Provider tokens, numeric installation and repository IDs,
and permission grants remain owner-held.

Repo App authorization uses a popup, an antiforgery cookie, a native form
post, and a host-only transaction cookie. The callback
`/settings/source-control?repoApp=connected` is only a notification. The
bootstrap captures that exact callback and removes the query before loading
assets. The opener accepts a message only from the tracked popup, the same
origin, and the current request generation, then refreshes connection status
from the Broker. A callback does not prove that the connection succeeded.
Leaving settings or clearing the browser session closes only the panel's
tracked popup and cancels its pending antiforgery request.

Repository discovery and account disconnect call the Broker directly with
cookie credentials. JSON mutations first request an antiforgery token and
send it as `X-CSRF-TOKEN`. User authorization uses a native form post to the
tracked popup; it does not send an OAuth redirect through `fetch`.

Project settings can also connect the signed-in user's GitHub Copilot account
through the Gateway's connection lifecycle BFF. The callback uses the public
Web origin plus `/auth/github/copilot-app/callback` as its `CallbackUri`.
The callback page is not cached, removes callback query values before it loads
assets, and does not store the callback code or state. Configure the ingress
to redact callback query strings from its own access logs. The owner response,
not the popup message, determines connection status.

When the accepted run configuration selects GitHub App mode and the owner
routes are available, the run's **Changes** tab reads safe repository metadata
from the Broker and creates a short-lived selection code. Installation and
repository pinning remain run-bound Gateway operations with the exact project,
run, and tenant. The owner checks the accepted run configuration and current
authority. Status or discovery failures remain visible; the UI does not
present them as an empty repository list or a disconnected account.

## Connect a Remote MCP tool service

In project **Settings > Connections > Remote MCP**, enter the saved connection
ID and select **Load connection**. The view shows the state returned by the
service. It rejects responses for another project or connection.

Use **Connection setup** for registration and authorization metadata. Enter
the checksum from the saved project connection setup. The current API does
not return a connection directory or that checksum. The browser does not
calculate a replacement checksum from its JSON formatting.

Authorize opens the service-provided HTTPS address in a tracked popup. After
sign-in returns, select **Load connection** to read the current result.
A callback is not proof of success. Refresh and disconnect use the exact
connection, credential, and configuration revisions returned by the owner.
The browser does not collect or store provider tokens.

## Configure the client

The Web client reads Vite environment variables. Start with
`apps/web/.env.example`:

| Variable | Default or requirement | Purpose |
| --- | --- | --- |
| `VITE_GATEWAY_URL` | `/api/v1` | Versioned Gateway base URL. |
| `VITE_IDENTITY_BROKER_URL` | Required for sign-in | Identity Broker base URL. |
| `VITE_IDENTITY_BROKER_ISSUER` | Optional when it matches the Broker base URL | Exact HTTPS issuer expected in authorization callbacks. |
| `VITE_OAUTH_CLIENT_ID` | Registered client ID | OAuth client identifier. |
| `VITE_OAUTH_REDIRECT_URI` | Current origin plus `/auth/callback` | Registered OAuth callback URI. |
| `VITE_OAUTH_SCOPES` | Registered scopes | Space-separated OAuth scopes. |

When a callback includes `iss`, the client accepts it only if the value
matches the configured HTTPS issuer. It also checks state, PKCE, popup source,
and callback origin. A callback without `iss` remains supported for the
existing Broker profile. An invalid popup callback is rejected and closed so
the sign-in window can show an error.

## Run and build the client

Run these commands from the repository root:

1. Install the locked Web dependencies:

   ```powershell
   npm --prefix apps/web ci
   ```

2. Start the development server:

   ```powershell
   npm --prefix apps/web run dev
   ```

3. Validate types, tests, coverage, lint, and the production build:

   ```powershell
   npm --prefix apps/web run typecheck
   npm --prefix apps/web test
   npm --prefix apps/web run coverage
   npm --prefix apps/web run lint
   npm --prefix apps/web run build
   ```

The production host provides unauthenticated `/health/live` and
`/health/ready` checks. Readiness returns `503` if the built
`wwwroot/index.html` is missing. These checks report host and page availability;
they do not verify Broker access or live API behavior.

## Understand client limitations

The optional managed portal routes `/` to Web on port 8080, `/api/v1` and the
exact `/openapi/v1.json` path to Gateway, and exact `/mcp` and protected
resource metadata paths to MCP on port 8443. These source manifests describe
the intended route mapping; they do not establish a running portal or backend.

The client opens runs you already opened in this browser, from a link, or from
a notification. You don't type a run ID. The client can't start a new chat or
run, and it can't list every run, because the service has no APIs for them. It does not expose run-root acceptance,
typed outcome/workflow/work-plan proposals, or child-session
registration/spawn/fork controls, even where owner routes exist. The client
can message existing sessions and request supported detach/archive actions.
The Gateway does not expose journal object-content reads, so the UI displays
opaque references rather than inventing transcript text.

You can't browse the files that a run produced. The service has no run-bound
artifact manifest, diff, or object-version API for files that change in the
workspace. The Web client does not read a host file system, and it does not
offer the older 0.x file endpoints.

The run's **Run settings** tab shows the model reference and the allowed
implementations for each platform component, in order. It doesn't show the
implementation that is running or a runtime SDK model ID, because the service
has no platform components API. Usage
reports run totals without per-event model-binding details. The UI does not
infer those identities from configuration or usage, and it does not replace
missing measurements with zero.

## Expected result

The client returns to its exact registered callback after sign-in and sends
product API and event-stream requests through the configured Gateway. The
client keeps tokens in memory; owner services remain responsible for current
authorization. A successful client build alone does not prove live sign-in or
API access.
