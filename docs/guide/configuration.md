# Configuration

This page collects the API and web configuration in one place.

## API configuration

The API reads standard ASP.NET Core configuration sources. In local development, you can use `appsettings.Development.json`, `appsettings.Local.json` with `ASPNETCORE_ENVIRONMENT=Local`, environment variables, or user secrets.

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
| `Auth:Keys` | none | Array of `{ Token, User }` entries for multi-user API access |
| `Auth:ApiKey` | none | Single-key shortcut when you only need one bearer token |
| `Auth:User` | none | User name paired with `Auth:ApiKey` |
| `Auth:GitHub:AllowedOrg` | none | When set, GitHub-authenticated callers must belong to this org (enforced by `GitHubOrgAuthorizationService`); when unset, org authorization is treated as not configured |

### CORS settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Cors:AllowedOrigins` | `[]` | Array of origins the browser is allowed to call from (e.g. `http://localhost:8080` for the web UI in development) |

### Provider settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Providers:GitHubCopilot:ApiKey` | none | API key/token for the GitHub Copilot-compatible endpoint. `Providers:GitHubCopilot:GitHubToken` is also accepted and takes precedence — this is the key the AKS deployment populates from Key Vault. |
| `Providers:GitHubCopilot:Model` | `gpt-4o` | Model name used for GitHub Copilot runs |
| `Providers:MicrosoftFoundry:ApiKey` | none | API key for Microsoft Foundry (required at runtime) |
| `Providers:MicrosoftFoundry:Endpoint` | none | Microsoft Foundry endpoint URL (required at runtime) |
| `Providers:MicrosoftFoundry:Deployment` | none | Deployment name used for Microsoft Foundry runs (required at runtime) |

Some local samples still show `Providers:Foundry` with `DeploymentName`. The current runtime reads `Providers:MicrosoftFoundry:Endpoint`, `Providers:MicrosoftFoundry:ApiKey`, and `Providers:MicrosoftFoundry:Deployment`.

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
| `VITE_API_KEY` | No | empty | Optional bearer key for non-interactive/local use. Not needed when signing in through the web UI; the Docker build deliberately unsets it. |

## Example local setup

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

```dotenv
VITE_API_URL=http://localhost:5000
```
