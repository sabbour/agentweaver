# Getting started

Use this guide to stand up the API, submit a run, watch it live, and approve the result.

## Prerequisites

You need these tools before you start:

- .NET 10 SDK (`global.json` pins `10.0.100`)
- Node.js 20.19+ (or 22.12+) — required by Vite 8
- An existing local Git repository that the agent can target
- A GitHub Copilot or Microsoft Foundry model credential

## 1. Configure the API

The API reads settings from `appsettings.json` plus the environment-specific file for `ASPNETCORE_ENVIRONMENT`. If you want to use `apps/Agentweaver.Api/appsettings.Local.json`, set the environment to `Local` before you start the API.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

Use `apps/Agentweaver.Api/appsettings.Local.json` to define an API key and your model provider settings. Replace the placeholders with your own values.

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
dotnet run --project apps/Agentweaver.Api
```

The API listens on the default ASP.NET Core development URL unless you override it through standard host settings.

## 3. Submit a run from the web UI

Open the web UI to submit runs, watch live events, and review results. Install dependencies and start the Vite dev server:

```powershell
cd apps/web
npm install
npm run dev
```

Set `VITE_API_URL` and `VITE_API_KEY` in `apps/web/.env`, then open the local URL that Vite prints in the console.
