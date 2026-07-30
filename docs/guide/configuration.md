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
| `Auth:GitHub:AllowedOrg` | none | Comma/semicolon-delimited list of allow-rules. Each rule is one of: `org` (bare org — any member), `org/*` (explicit wildcard, same as bare org), or `org/team-slug` (only that specific team). A caller is allowed if they satisfy ANY rule. Team display names with spaces or uppercase are defensively slugified (e.g. `Azure/AKS PM` is treated as `Azure/aks-pm`). Example: `Azure/aks,Azure/AKS PM,azure-management-and-platforms/*`. |

Set the OAuth client secret locally with user-secrets:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"
```

When `Auth:Mode=Entra`, the platform sign-in is driven by Microsoft Entra ID instead of
GitHub. The interactive browser flow (`/auth/entra/authorize` → `/auth/entra/callback`)
uses the Microsoft identity platform v2.0 authorization-code-with-PKCE flow and redeems the
code server-side as a **confidential web client**, so a client secret is required.

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:Entra:ClientId` | none | Entra app registration (client) ID — **required** for Entra sign-in |
| `Auth:Entra:ClientSecret` | none | Entra client secret used to redeem the authorization code at the token endpoint — **required** for the server-side redirect flow |
| `Auth:Entra:TenantId` | none | Entra tenant ID — **required** unless `Auth:Entra:Authority` is set |
| `Auth:Entra:Authority` | none | Full authority URL (e.g. `https://login.microsoftonline.com/<tenant>/v2.0`); overrides `TenantId` for authority resolution |
| `Auth:Entra:RedirectUri` | `http://localhost:5000/auth/entra/callback` | Redirect URI registered on the Entra app; must exactly match the `/auth/entra/callback` URL |
| `Auth:Entra:Scopes` | `openid profile email <ClientId>/.default` | Space-delimited scopes requested at authorize time. The `<ClientId>/.default` scope yields an access token whose `aud` is the app itself and carries the platform App Roles claim |
| `Auth:Entra:FrontendUrl` | falls back to `Auth:GitHub:FrontendUrl` then `http://localhost:5173` | URL the API redirects to after a successful (or failed) Entra sign-in |

Set the Entra client secret locally with user-secrets:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:Entra:ClientSecret" "<your-entra-app-client-secret>"
```

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
