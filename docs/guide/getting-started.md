# Getting started

Use this guide to stand up the API, submit a run, watch it live, and approve the result.

## Prerequisites

You need these tools before you start:

- .NET 10 SDK (`global.json` pins `10.0.300`)
- Node.js 18 or later
- An existing local Git repository that the agent can target
- A GitHub Copilot or Microsoft Foundry model credential

## 1. Configure the API

The API reads settings from `appsettings.json` plus the environment-specific file for `ASPNETCORE_ENVIRONMENT`. If you want to use `apps/Scaffolder.Api/appsettings.Local.json`, set the environment to `Local` before you start the API.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

Use `apps/Scaffolder.Api/appsettings.Local.json` to define an API key and your model provider settings. Replace the placeholders with your own values.

```json
{
  "Auth": {
    "Keys": [
      { "Token": "dev-local-key", "User": "local-developer" }
    ]
  },
  "Providers": {
    "GitHubCopilot": {
      "ApiKey": "<github-copilot-api-key>",
      "Model": "claude-sonnet-4.6"
    },
    "MicrosoftFoundry": {
      "ApiKey": "<foundry-api-key>",
      "Endpoint": "https://<resource>.services.ai.azure.com/api/projects/<project>",
      "Deployment": "gpt-4.1"
    }
  }
}
```

## 2. Start the API

From the repository root, start the backend:

```powershell
dotnet run --project apps/Scaffolder.Api
```

The API listens on the default ASP.NET Core development URL unless you override it through standard host settings.

## 3. Configure the CLI

Set the CLI environment variables in the same shell you use to run commands:

```powershell
$env:SCAFFOLDER_API_URL = "http://localhost:5000"
$env:SCAFFOLDER_API_KEY = "dev-local-key"
```

## 4. Submit a run from the CLI

Submit a task and start watching its events:

```powershell
dotnet run --project apps/Scaffolder.Cli -- run submit
```

The CLI prompts for the repository path, originating branch, task, and model source. On success it prints the run id and switches into live watch mode.

## 5. Watch live output

While the run is active, the CLI prints ordered events such as `agent.message`, `tool.call`, `tool.result`, `tool.rejected`, and lifecycle updates. If you need to reconnect later, run the watch command with the run id:

```powershell
dotnet run --project apps/Scaffolder.Cli -- run watch <run-id>
```

The backend replays only the events after the last sequence the client saw, then continues live.

## 6. Review and approve

When the agent finishes, the run enters the review gate and the CLI prompts you to approve or decline the diff. You can also review the run explicitly:

```powershell
dotnet run --project apps/Scaffolder.Cli -- run review <run-id>
```

Approval records `review.approved` and attempts the merge back into the originating branch. A decline records `review.declined` and leaves the originating branch untouched.

## 7. Use the web UI instead

The web UI offers the same submit, watch, and review flow in the browser. Configure its API settings, install dependencies, and start the Vite dev server:

```powershell
cd apps/web
npm install
npm run dev
```

Set `VITE_API_URL` and `VITE_API_KEY` in `apps/web/.env`, then open the local URL that Vite prints in the console.
