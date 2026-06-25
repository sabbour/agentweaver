# Configuration

This page collects the API and web configuration in one place.

## API configuration

The API reads standard ASP.NET Core configuration sources. In local development, you can use `appsettings.Development.json`, `appsettings.Local.json` with `ASPNETCORE_ENVIRONMENT=Local`, environment variables, or user secrets.

### Storage and git settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Database:Path` | `agentweaver.db` in the app data directory | SQLite database file for runs and operational records (the per-event log is stored separately in the EF Core memory database) |
| `Worktrees:BasePath` | `worktrees` under the app data directory | Root folder for per-run git worktrees |
| `Git:Author:Name` | `Agentweaver` | Author name for run commits and merge commits |
| `Git:Author:Email` | `agentweaver@localhost` | Author email for run commits and merge commits |

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

## Web environment variables

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `VITE_API_URL` | No | `http://localhost:5000` | API base URL for the browser client |
| `VITE_API_KEY` | Yes | empty | Bearer API key sent on every request |

## Example local setup

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

```dotenv
VITE_API_URL=http://localhost:5000
VITE_API_KEY=dev-local-key
```
