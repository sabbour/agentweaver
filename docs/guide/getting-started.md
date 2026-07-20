# Getting started

Use this guide to stand up the API, submit a run, watch it live, and approve the result.

## Install (one command)

📖 See [Prerequisites](#prerequisites) below if you don't have git/Node/.NET
installed yet — or the [README's Prerequisites
section](https://github.com/sabbour/agentweaver#prerequisites) for full
per-platform install commands.

From a cloned checkout, the installer checks prerequisites, installs web and .NET dependencies, and launches the dev environment:

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup
```

After the installer completes, skip to [Configure the API](#1-configure-the-api) below.

---

## Deploy to Azure (one command)

📖 See [Prerequisites](#prerequisites) below (you'll also need the Azure CLI
logged in via `az login`).

Prefer a live Azure deployment over local dev? Skip local setup entirely and run the smart installer instead:

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run azure:deploy
```

With no flags, in an interactive terminal, this prompts you through Azure
subscription, resource group, location, cluster/ACR/Key Vault names (smart
defaults, all editable), and your GitHub OAuth App client ID + secret — then
provisions AKS, PostgreSQL, Key Vault, ACR, identity, and monitoring, builds
and pushes images, and deploys and verifies the release. It prints an
outputs summary at the end (cluster, ACR, gateway host, verification
pass/fail counts) and never prints the OAuth client secret.

For non-interactive deploys (flags, environment variables, or a
`--params-file`), upgrading an existing deployment (`npm run azure:upgrade`),
and the full flag reference, see the [README's Deploy to Azure
section](https://github.com/sabbour/agentweaver#deploy-to-azure) and the
[npm script reference](#npm-script-reference) below.

---

## Prerequisites

You need these tools before you start:

| Tool | Windows (winget) | macOS (Homebrew) | Linux (Debian/Ubuntu) |
| --- | --- | --- | --- |
| .NET 10 SDK (`global.json` pins `10.0.100`) | `winget install Microsoft.DotNet.SDK.10` | `brew install --cask dotnet-sdk` | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash /dev/stdin --channel 10.0` |
| Node.js 20.19+ (or 22.12+) — required by Vite 8 | `winget install OpenJS.NodeJS.LTS` | `brew install node@20` | `curl -fsSL https://deb.nodesource.com/setup_20.x \| sudo -E bash - && sudo apt-get install -y nodejs` |
| git | `winget install --id Git.Git -e` | `brew install git` | `sudo apt-get update && sudo apt-get install -y git` |

`npm run setup` (`dev --setup`) checks these itself and prints the matching
install command above for your platform if one is missing.

### Installing prerequisites

::: details Windows

Install `winget` first if it isn't already available (it ships with Windows 11
and recent Windows 10 updates):

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force
Install-Module Microsoft.WinGet.Client -Force -Repository PSGallery
Repair-WinGetPackageManager -AllUsers
```

Then install git, Node.js, and the .NET SDK:

```powershell
winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements
winget install --id OpenJS.NodeJS.22 --exact --accept-source-agreements --accept-package-agreements
winget install --id Microsoft.DotNet.SDK.10 --accept-source-agreements --accept-package-agreements
```

`npm` ships bundled with Node.js — no separate install needed. Refresh `PATH`
in your **current** shell so the newly installed tools are found without
reopening the terminal:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path', 'User')
```

:::

::: details macOS

Install [Homebrew](https://brew.sh/) first if it isn't already available:

```bash
/bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
```

Then install git, Node.js, and the .NET SDK:

```bash
brew install git
brew install node@20
brew install --cask dotnet-sdk
```

`npm` ships bundled with Node.js — no separate install needed.

:::

::: details Linux (Debian/Ubuntu)

```bash
# git
sudo apt-get update && sudo apt-get install -y git

# Node.js 20.x + npm (via NodeSource -- distro repos are usually outdated)
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt-get install -y nodejs

# .NET SDK 10 (via the official install script -- apt package availability
# varies by distro/version)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

:::

`npm run setup` re-checks all of the above itself after you install them, and
prints the matching command again if anything is still missing or out of date.

Also needed, not installable via a package manager:

- An existing local Git repository that the agent can target
- A GitHub account with an active GitHub Copilot subscription — the web UI signs you in via OAuth.
- A **GitHub OAuth App** — needed so the API can perform the OAuth sign-in flow. [Create one](https://github.com/settings/developers) with callback URL `http://localhost:5000/auth/github/callback`.

## npm script reference

Every build/deploy/upgrade/release/dev workflow runs through one cross-platform Node CLI (`scripts/azure/cli.mjs`) — no bash or PowerShell required on any platform. The root `package.json` exposes these scripts:

| Script | What it does |
|---|---|
| `npm start` / `npm run dev` | Local dev orchestration (API + web), browser auto-open disabled. Alias for `azure:dev -- --no-browser`. |
| `npm run setup` | Local dev environment setup only: checks prerequisites (git/.NET 10/Node 20+), installs `apps/web`'s npm deps, restores .NET packages — skips the Azure pipeline entirely. This is what the [Install (one command)](#install-one-command) quick start uses. Alias for `dev -- --setup`. |
| `npm run azure:deploy` | The smart installer. With no flags **and** an interactive terminal, prompts you through subscription/resource group/location/cluster names/GitHub OAuth. With flags, env vars, or a params file (or no TTY), it runs non-interactively instead. Always deploys to Azure — for local-only setup use `npm run setup` instead. |
| `npm run azure:upgrade` | Builds a new immutable image tag (defaults to the current git HEAD short SHA), redeploys, and cycles the AgentHost warm pool. Refuses to run on a dirty working tree unless you pass `-- --allow-dirty`. |
| `npm run azure:release` | Semver release workflow (`major`/`minor`/`patch`): bumps `VERSION`, tags, generates a GitHub release, and composes over the same build/deploy engine as `deploy`/`upgrade`. Add `-- --dry-run` to preview without making changes. |
| `npm run azure:verify` | Post-deploy health verification against the live cluster (pods, gateway, HTTP probes) — read-only, safe to run anytime. |
| `npm run azure:dev` | Same as `npm run dev`, but opens your browser by default (omit `--no-browser`). |
| `npm run dev:web` | Builds and starts only the web UI (Vite dev server) against an API you're already running separately. |
| `npm run dev:api` | Builds and runs only the .NET API. |
| `npm run docs:dev` / `docs:build` / `docs:preview` | This documentation site (VitePress). |

Every `azure:*` script (and `dev`/`setup`) accepts `-- --help` to print its full flag list, for example `npm run azure:deploy -- --help`. Useful flags across commands:

- **`azure:deploy`**: `--params-file <path>` (or `--config <path>`) for non-interactive deploys driven by a JSON/JSONC file (see `scripts/azure/params.example.json`) — the config precedence is **flags > env vars > params file > detected defaults > interactive prompt**, so any flag always wins. Also: `--resource-group`, `--cluster-name`, `--acr-name`, `--location`, `--keyvault-name`, `--namespace`, `--image-tag`, `--github-client-id`, `--github-client-secret`, `--skip-postgres`, `--skip-oauth-key`.
- **`azure:upgrade`**: `--allow-dirty` to bypass the clean-working-tree check (dev/test escape hatch only — never use for a real upgrade).
- **`azure:dev` / `dev` / `setup`**: `--no-browser` (skip opening a browser tab), `--skip-build` (skip the web build step), `--setup` (local-only setup, no servers started — this is what `npm run setup` runs).
- **`azure:release`**: positional `major|minor|patch` bump argument, `--dry-run` (or `DRY_RUN=true`) to preview without tagging/publishing.

## 1. Configure the API

The API reads settings from `appsettings.json` plus the environment-specific file for `ASPNETCORE_ENVIRONMENT`. If you want to use `apps/Agentweaver.Api/appsettings.Local.json`, set the environment to `Local` before you start the API.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Local"
```

Use `apps/Agentweaver.Api/appsettings.Local.json` to configure your non-secret GitHub OAuth App settings and model provider:

```json
{
  "Auth": {
    "GitHub": {
      "ClientId": "<your-oauth-app-client-id>",
      "CallbackUrl": "http://localhost:5000/auth/github/callback",
      "FrontendUrl": "http://localhost:5173"
    }
  },
  "Providers": {
    "GitHubCopilot": {
      "Model": "claude-sonnet-4.6"
    }
  }
}
```

The `ClientId` and `ClientSecret` come from your GitHub OAuth App settings page. `CallbackUrl` must match the **Authorization callback URL** registered in the app exactly. Store the client secret with .NET user-secrets, not in `appsettings*.json`:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"
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

Set `VITE_API_URL` in `apps/web/.env` so the browser client points at your API (default `http://localhost:5000`), then open the local URL that Vite prints in the console.

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
