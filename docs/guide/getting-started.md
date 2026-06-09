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

While the run is active, the CLI prints ordered events such as `agent.message.delta` token chunks, `tool.call`, `tool.result`, `tool.error`, and lifecycle updates. If you need to reconnect later, run the watch command with the run id:

```powershell
dotnet run --project apps/Scaffolder.Cli -- run watch <run-id>
```

The backend replays only the events after the last sequence the client saw, then continues live.

## 6. Review and approve

When the agent finishes, the run commits its worktree changes and enters `awaiting_review`. If you are watching in an interactive session the CLI prompts you immediately. You can also run the review command explicitly:

```powershell
dotnet run --project apps/Scaffolder.Cli -- run review <run-id>
```

The CLI fetches the run, prints the unified diff between the originating branch and the run's worktree branch, and prompts you to approve or decline.

**Approve** merges the worktree branch back into the originating branch. When you run the API locally and the originating branch is your current checkout with a clean working tree, the merge also updates your working directory — you see the files change on disk immediately. If your working tree has uncommitted changes, the approve is blocked and the CLI tells you what to fix; the run stays at the review gate and you can approve again once the tree is clean. A successful merge prints `Merged successfully.` and the new commit hash.

**Decline** leaves the originating branch unchanged and closes the run.

A terminal merge conflict (branch divergence that cannot be resolved automatically) prints `Merge failed.` and keeps the worktree intact for manual inspection.

## 7. Use the web UI instead

The web UI offers the same submit, watch, and review flow in the browser. Configure its API settings, install dependencies, and start the Vite dev server:

```powershell
cd apps/web
npm install
npm run dev
```

Set `VITE_API_URL` and `VITE_API_KEY` in `apps/web/.env`, then open the local URL that Vite prints in the console.
