# Configuration

This page collects the API, CLI, and web configuration in one place.

## API configuration

The API reads standard ASP.NET Core configuration sources. In local development, you can use `appsettings.Development.json`, `appsettings.Local.json` with `ASPNETCORE_ENVIRONMENT=Local`, environment variables, or user secrets.

### Storage and git settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Database:Path` | `scaffolder.db` in the app data directory | SQLite database file for runs, event log, and operational records |
| `Worktrees:BasePath` | `worktrees` under the app data directory | Root folder for per-run git worktrees |
| `Git:Author:Name` | `Scaffolder` | Author name for run commits and merge commits |
| `Git:Author:Email` | `scaffolder@localhost` | Author email for run commits and merge commits |
| `RunBounds:MaxSteps` | `50` | Maximum tool-call loop iterations before the run is bounded |
| `RunBounds:MaxMinutes` | `10` | Maximum wall-clock run duration in minutes |

### Authentication settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Auth:Keys` | none | Array of `{ Token, User }` entries for multi-user API access |
| `Auth:ApiKey` | none | Single-key shortcut when you only need one bearer token |
| `Auth:User` | none | User name paired with `Auth:ApiKey` |

### CORS settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Cors:AllowedOrigins` | `[]` | Array of origins the browser is allowed to call from (e.g. `http://localhost:5173` for the web UI in development) |

### Provider settings

| Key | Default | Purpose |
| --- | --- | --- |
| `Providers:GitHubCopilot:ApiKey` | none | API key for the GitHub Copilot-compatible endpoint |
| `Providers:GitHubCopilot:Endpoint` | `https://api.githubcopilot.com` | Base URL for the GitHub Copilot provider |
| `Providers:GitHubCopilot:Model` | `gpt-4o` | Model name used for GitHub Copilot runs |
| `Providers:MicrosoftFoundry:ApiKey` | none | API key for Microsoft Foundry |
| `Providers:MicrosoftFoundry:Endpoint` | none | Microsoft Foundry endpoint URL |
| `Providers:MicrosoftFoundry:Deployment` | none | Deployment name used for Microsoft Foundry runs |

Some local samples still show `Providers:Foundry` with `DeploymentName`. The current runtime reads `Providers:MicrosoftFoundry:Endpoint`, `Providers:MicrosoftFoundry:ApiKey`, and `Providers:MicrosoftFoundry:Deployment`.

## CLI environment variables

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `SCAFFOLDER_API_URL` | No | `http://localhost:5000` | API base URL |
| `SCAFFOLDER_API_KEY` | Yes | none | Bearer API key sent on every request |

## Web environment variables

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `VITE_API_URL` | No | `http://localhost:5000` | API base URL for the browser client |
| `VITE_API_KEY` | Yes | empty | Bearer API key sent on every request |

## Example local setup

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
$env:SCAFFOLDER_API_URL = "http://localhost:5000"
$env:SCAFFOLDER_API_KEY = "dev-local-key"
```

```dotenv
VITE_API_URL=http://localhost:5000
VITE_API_KEY=dev-local-key
```
