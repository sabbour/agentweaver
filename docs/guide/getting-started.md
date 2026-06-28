# Getting started

Use this guide to stand up the API, submit a run, watch it live, and approve the result.

## Install (one command)

The installer checks prerequisites, installs web and .NET dependencies, and launches the dev environment:

```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash
```
```powershell
# Windows PowerShell
irm https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1 | iex
```

Both commands will clone the repo to `~/agentweaver` if you don't already have a local checkout. If you have already cloned the repo, run `bash install.sh` (or `.\install.ps1`) from the repo root instead.

After the installer completes, skip to [Configure the API](#1-configure-the-api) below.

---

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

Set `VITE_API_URL` in `apps/web/.env` so the browser client points at your API (default `http://localhost:5000`), then open the local URL that Vite prints in the console. The web UI signs you in through GitHub — you do not need to configure a static API key for the browser.

```dotenv
VITE_API_URL=http://localhost:5000
```

## 4. Create a project, run, and review

1. **Sign in** with GitHub when the web UI loads.
2. **Create a project** from the Project Gallery — blank or cloned from a GitHub repo.
3. **Cast a team** (optional) or start straight away.
4. **Start a task** from the project Board. The coordinator drafts an OutcomeSpec; confirm it to dispatch work.
5. **Watch** the live topology and per-agent execution stream.
6. **Review and merge** the assembled diff once the run reaches Human Review — nothing lands on your branch until you approve.

For full end-to-end walkthroughs, see [Example walkthroughs](./example-scenarios).
