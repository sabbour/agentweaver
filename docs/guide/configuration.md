# Configuration

This page collects the API and web configuration in one place.

## API configuration

The API reads standard ASP.NET Core configuration sources. In local development, keep secrets out of JSON files: use .NET user-secrets or environment variables for secret values, and use `appsettings.Development.json` / `appsettings.Local.json` only for non-secret settings.

### Storage and git settings

Agentweaver stores its operational state (runs, projects, the per-run event log, team memory, and decisions) in a single EF Core database. The backend is selected with `Database:Provider`.

| Key | Default | Purpose |
| --- | --- | --- |
| `Database:Provider` | `sqlite` | Database backend: `sqlite`, `sqlserver`/`azuresql`, or `postgres`/`postgresql` |
| `Database:Path` | data directory under `%LOCALAPPDATA%/agentweaver` | SQLite only — the directory of this path holds the `memory.db` file (the file name is always `memory.db`) |
| `Database:ConnectionString` | none | Connection string fallback for SQL Server / PostgreSQL when no named connection string is set |
| `ConnectionStrings:MemoryDb` | none | Connection string for SQL Server (`sqlserver`/`azuresql`); also a fallback for PostgreSQL |
| `ConnectionStrings:Postgres` | none | Connection string for the PostgreSQL provider (uses the `Agentweaver.Api.Migrations.Postgres` migrations assembly) |
| `Worktrees:BasePath` | `worktrees` under the data directory | Root folder for per-run git worktrees |
| `Git:Author:Name` | `Agentweaver` | Author name for run commits and merge commits |
| `Git:Author:Email` | `agentweaver@localhost` | Author email for run commits and merge commits |

::: tip Default storage location
With the default `sqlite` provider, the database file is `memory.db` inside the app data directory (`%LOCALAPPDATA%/agentweaver` on Windows, the platform-equivalent local application data folder elsewhere). See [Memory reference](/reference/memory) for the schema and provider details.
:::

### Authentication settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:GitHub:ClientId` | none | GitHub OAuth App client ID — required for sign-in |
| `Auth:GitHub:ClientSecret` | none | GitHub OAuth App client secret — required for sign-in |
| `Auth:GitHub:CallbackUrl` | none | OAuth callback URL registered in the GitHub App (must match exactly) |
| `Auth:GitHub:FrontendUrl` | none | URL the API redirects to after a successful sign-in |
| `Auth:GitHub:AllowedOrg` | none | Comma/semicolon-delimited list of allow-rules. Each rule is one of: `*` (all organizations), `org` (bare org — any member), `org/*` (explicit organization wildcard), or `org/team-slug` (only that specific team). A caller is allowed if they satisfy ANY rule. Team display names with spaces or uppercase are defensively slugified (e.g. `Azure/AKS PM` is treated as `Azure/aks-pm`). Example: `Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`. |

Set the OAuth client secret locally with user-secrets:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"
```

#### Repo App user authorization

Interactive repository access is authorized separately from product sign-in and from the
legacy GitHub OAuth configuration. An Entra-authenticated human starts
`POST /api/auth/github/repo-app/authorizations`; the browser completes the App callback at
`GET /auth/github/repo-app/callback`. The API persists only opaque transaction and credential
references. It uses PKCE S256 and a one-time `__Host-` callback cookie; do not register the
legacy `/auth/github/callback` URL for this App.

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:RepoApp:ClientId` | none | Repo GitHub App OAuth client ID |
| `Auth:RepoApp:ClientSecret` | none | Repo GitHub App OAuth client secret; set through user-secrets or Key Vault |
| `Auth:RepoApp:CallbackUrl` | none | Exact registered callback URL, ending in `/auth/github/repo-app/callback` |
| `Auth:RepoApp:BaseUrl` | `https://github.com` | GitHub authorization origin |
| `Auth:RepoApp:Scopes` | `repo read:user` | Explicit user-authorization scopes |
| `Auth:RepoApp:FrontendUrl` | `http://localhost:5173` | Trusted application origin for fixed post-callback routes |

The begin request accepts only `settings` or `projects` as `return_route_key`; it never
accepts an arbitrary URL or path. Refresh and disconnect use the corresponding
`POST /api/auth/github/repo-app/authorization/refresh` and
`DELETE /api/auth/github/repo-app/authorization` endpoints. Both require the same
human Entra subject as authorization begin.

#### Repo App installation and webhook

The API identity reads the Repo App PEM and webhook secrets through its configured secret
store; in hosted deployments those names resolve only through the API's Key Vault access.
The PEM, App JWT, and installation access token are never configuration values, persisted
records, logs, or API responses. Configure GitHub's single Repo App webhook to the
App-level receiver implemented by the API; do not configure per-project webhook URLs.

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:RepoApp:AppId` | none | Numeric Repo App ID used as the App-JWT issuer |
| `Auth:RepoApp:PrivateKeySecretName` | none | Secret-store name of the Repo App PEM, readable only by the API |
| `Auth:RepoApp:WebhookSecretName` | none | Secret-store name of the active webhook HMAC secret |
| `Auth:RepoApp:PreviousWebhookSecretName` | none | Secret-store name of the prior HMAC secret during a rotation |
| `Auth:RepoApp:PreviousWebhookSecretExpiresAt` | none | UTC expiration after which the previous secret is rejected |
| `Auth:RepoApp:WebhookMaxBodyBytes` | `1048576` | Maximum unauthenticated raw request-body size |
| `Auth:RepoApp:WebhookVerificationTimeoutSeconds` | `5` | Body-read and signature-verification timeout (maximum `10`) |
| `Auth:RepoApp:ApiUrl` | `https://api.github.com` | GitHub API origin for App installation-token minting |

Project App grants are pinned with the GitHub numeric installation and repository IDs. The
configuration request accepts only those identifiers: the API resolves the matching
installation's permission map and repository full name from GitHub. Repository names remain
display-only and are never authorization inputs. A provider permission expansion or reduction
invalidates the affected unattended grant and activation; reconfigure and explicitly reactivate
after GitHub permissions are corrected.
Installation tokens are scoped to Agentweaver's server-declared unattended repository
permissions and never inherit unrelated installation permissions.

#### Purpose-bound broker

The trusted run lifecycle captures and revalidates immutable, purpose-specific snapshots for
root launches, child launches, retries, and resumes. Interactive repository snapshots are bound
to the initiating Entra subject and exact live Repo App user authorization; they do **not**
require an installation or repository grant. Unattended repository snapshots remain bound to an
active installation, canonical repository grant, and unchanged permission digest. No API, MCP,
or sandbox route exposes the broker. Repository and Copilot adapter delivery is owned by #947;
this broker layer never configures a sandbox or model process.

Two-App credential reads use current secret versions only. Revocation writes a tombstone before
deleting the current value. Azure Key Vault soft-delete and purge protection can retain older
provider versions for the configured retention period; this is an accepted recovery risk,
mitigated by retention policy and least-privilege Key Vault RBAC that forbids versioned reads
outside the API credential vault.

#### Project Copilot App binding

An Entra-authenticated **explicit Project Owner** starts a project-specific Copilot App
binding with `POST /api/projects/{id}/github/copilot/authorizations`. The callback is
`GET /auth/github/copilot-app/callback`; it is pinned to the project and rechecks the
same Owner assignment before completing. Poll
`GET /api/projects/{id}/github/copilot/authorizations/{transactionId}` only exposes
`pending`, `completed`, `failed`, or `expired` to the initiating Entra subject. A
human Project Owner or human Platform Admin can disconnect with
`DELETE /api/projects/{id}/github/copilot/binding`.

The Copilot App has no repository permissions, installation, PEM, or repository
operations. Its client ID, client secret, and optional Key Vault secret path must differ
from the Repo App's values. Registration validation rejects a Copilot private key or
repository permission, a shared App credential, or a Repo App configured to request user
authorization during installation.

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:CopilotApp:ClientId` | none | Copilot GitHub App OAuth client ID; must differ from `Auth:RepoApp:ClientId` |
| `Auth:CopilotApp:ClientSecret` | none | Copilot App OAuth client secret; store in user-secrets or Key Vault |
| `Auth:CopilotApp:CallbackUrl` | none | Exact callback URL ending in `/auth/github/copilot-app/callback` |
| `Auth:CopilotApp:BaseUrl` | `https://github.com` | GitHub authorization origin |
| `Auth:CopilotApp:Scopes` | `read:user` | Explicit non-repository user-authorization scopes |
| `Auth:CopilotApp:FrontendUrl` | `http://localhost:5173` | Trusted application origin for the fixed callback route |
| `Auth:CopilotApp:SecretPath` | none | Optional Key Vault path; must not equal the Repo App secret path |

When `Auth:Mode=Entra`, the platform sign-in is driven by Microsoft Entra ID instead of
GitHub. The interactive browser flow (`/auth/entra/authorize` → `/auth/entra/callback`)
uses the Microsoft identity platform v2.0 authorization-code-with-PKCE flow. Agentweaver
redeems the code server-side and supports both a confidential-client variant (when
`Auth:Entra:ClientSecret` is configured) and a PKCE-only variant (when the Entra app allows
public client flows and no client secret is configured).

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:Entra:ClientId` | none | Entra app registration (client) ID — **required** for Entra sign-in |
| `Auth:Entra:ClientSecret` | none | Optional Entra client secret for confidential-client token redemption. Omit it when the tenant blocks password credentials and the app registration has public client flows enabled (`isFallbackPublicClient: true`) |
| `Auth:Entra:TenantId` | none | Entra tenant ID — **required** unless `Auth:Entra:Authority` is set |
| `Auth:Entra:Authority` | none | Full authority URL (e.g. `https://login.microsoftonline.com/<tenant>/v2.0`); overrides `TenantId` for authority resolution |
| `Auth:Entra:RedirectUri` | `http://localhost:5000/auth/entra/callback` | Redirect URI registered on the Entra app; must exactly match the `/auth/entra/callback` URL |
| `Auth:Entra:Scopes` | `openid profile email <ClientId>/.default` | Space-delimited scopes requested at authorize time. The `<ClientId>/.default` scope yields an access token whose `aud` is the app itself and carries the platform App Roles claim |
| `Auth:Entra:FrontendUrl` | falls back to `Auth:GitHub:FrontendUrl` then `http://localhost:5173` | URL the API redirects to after a successful (or failed) Entra sign-in |

If your tenant allows password credentials and you want confidential-client redemption, set
the Entra client secret locally with user-secrets:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:Entra:ClientSecret" "<your-entra-app-client-secret>"
```

If your tenant blocks client secrets, leave `Auth:Entra:ClientSecret` unset. Agentweaver will
redeem the authorization code with PKCE only, which works with Entra app registrations that
allow public client flows.

::: tip AKS deploy pipeline wiring
On the AKS deploy pipeline, `Auth:Mode`/`Auth:Entra:ClientId`/`Auth:Entra:TenantId`/
`Auth:Entra:RedirectUri` are set from the deploy-time `AUTH_MODE`/`ENTRA_CLIENT_ID`/
`ENTRA_TENANT_ID` environment variables (mirroring how `GITHUB_ALLOWED_ORG` etc. flow into
`Auth:GitHub:*` for GitHub mode) — see `scripts/azure/variables.mjs` and
`k8s/base/api-deployment.yaml`. `Auth:Entra:ClientSecret` (`Auth__Entra__ClientSecret`) is
deliberately **not** wired through the deploy pipeline: this environment is PKCE-only per the
tenant policy noted above, so there is no deploy-time env var / ConfigMap key for it. If a
future tenant allows a confidential-client secret, set `Auth__Entra__ClientSecret` manually via
the Key Vault CSI `SecretProviderClass`, mirroring the existing `github-client-secret` wiring.
:::

### CORS settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Cors:AllowedOrigins` | `[]` | Array of origins the browser is allowed to call from (e.g. `http://localhost:5173` for the web UI in development) |

### Provider settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Providers:GitHubCopilot:Model` | `claude-sonnet-4.6` | Model name used for GitHub Copilot runs. The token comes from the signed-in user's OAuth session — no API key is needed. |
| `Providers:GitHubCopilot:RuntimeCliPath` | `""` (empty) | Optional explicit path to the native Copilot CLI binary. When empty (the default), the SDK auto-resolves its bundled runtime from `bin/.../runtimes/{rid}/native/copilot`. Set this only when auto-resolution can't find a runtime for the host RID. Grounded in `apps/Agentweaver.Api/appsettings.json` and `packages/Agentweaver.AgentRuntime/Providers/GitHubCopilotClientFactory.cs:50`. |
| `Generation:Model` | `gpt-5.6-sol` | Global fallback for server-side blueprint, skill, workflow, and coordinator outcome-spec generation. Does not change normal project/run agent execution models. |
| `Generation:BlueprintModel` | `Generation:Model` | Optional global fallback for blueprint generation when a project has no `blueprint_generation_model`. |
| `Generation:SkillModel` | `Generation:Model` | Optional global fallback for skill generation. |
| `Generation:WorkflowModel` | `Generation:Model` | Optional global fallback for workflow YAML generation when a project has no `workflow_generation_model`. |
| `Generation:OutcomeSpecModel` | `Generation:Model` | Optional global fallback for coordinator outcome-spec drafting when a project has no `outcome_spec_generation_model`. |

Project Settings can override generation models per project with `blueprint_generation_model`,
`workflow_generation_model`, and `outcome_spec_generation_model`. Those project settings are
individual and nullable; `null` means "inherit the global Generation fallback".

The runtime CLI path also accepts two environment-variable fallbacks, checked in order after the config key: `AGENTWEAVER_COPILOT_CLI_PATH`, then `COPILOT_CLI_PATH` (`GitHubCopilotClientFactory.cs:50`). If the configured path does not exist on disk, Agentweaver logs a warning and falls back to SDK auto-resolution rather than failing (`GitHubCopilotClientFactory.cs:117`).

::: tip "Copilot runtime not found"
The GitHub Copilot SDK ships a native CLI and normally resolves it automatically from the build output. On a host whose RID was never provisioned into that output — for example a local WSL dev build on an architecture the publish step didn't produce — the SDK can fail at runtime with a "Copilot runtime not found" style error. Two fixes:

- **Point Agentweaver at an installed CLI.** Set `Providers:GitHubCopilot:RuntimeCliPath` (or `AGENTWEAVER_COPILOT_CLI_PATH` / `COPILOT_CLI_PATH`) to the full path of a Copilot CLI binary on the host.
- **Let the local build download it.** Plain `dotnet build` / `dotnet run` (including `npm run dev` in WSL) now downloads the native CLI for the build host's RID into `bin/{config}/net10.0/runtimes/{rid}/native/copilot`. The download is skipped only during `dotnet publish` (the container image pre-downloads a single copy), so a normal local build resolves the runtime on its own.
:::

### Logging verbosity

The committed `appsettings.json` quiets framework and EF Core noise while keeping the app's own logs at `Information`:

| Category | Level | Purpose |
| --- | --- | --- |
| `Default` | `Information` | Baseline level for uncategorised logs. |
| `Agentweaver` | `Information` | The app's own logs (`Agentweaver.*`) stay verbose. |
| `Microsoft` | `Warning` | Quiets general framework `Information` noise. |
| `Microsoft.AspNetCore` | `Warning` | Quiets per-request hosting/routing `Information` logs. |
| `Microsoft.EntityFrameworkCore` | `Warning` | Suppresses EF Core query/SQL `Information` spam (e.g. `Microsoft.EntityFrameworkCore.Database.Command`). |

Grounded in `apps/Agentweaver.Api/appsettings.json` (`Logging:LogLevel`). Override per-environment with `appsettings.{Environment}.json` or `Logging__LogLevel__<Category>` environment variables; the cluster deployment does not re-enable EF/framework `Information` logs.

## Web environment variables

The web UI authenticates users through GitHub OAuth and sends the resulting session token automatically — it does not require a static API key.

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `VITE_API_URL` | No | `http://localhost:5000` | API base URL for the browser client. In container deployments this is injected at runtime as `/api` via `window.__AGENTWEAVER_CONFIG__`. |

## Example local setup

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

```dotenv
VITE_API_URL=http://localhost:5000
```
