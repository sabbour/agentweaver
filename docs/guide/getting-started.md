# Getting started

Use this guide to stand up the API, submit a run, watch it live, and approve the result.

## Prerequisites

You need these tools before you start:

| Tool | Windows (winget) | macOS (Homebrew) | Linux (Debian/Ubuntu) |
| --- | --- | --- | --- |
| .NET 10 SDK (`global.json` pins `10.0.100`) | `winget install Microsoft.DotNet.SDK.10` | `brew install --cask dotnet-sdk` | `curl -sSL https://dot.net/v1/dotnet-install.sh \| bash /dev/stdin --channel 10.0` |
| Node.js 20.19+ (or 22.12+) — required by Vite 8 | `winget install OpenJS.NodeJS.LTS` | `brew install node@20` | `curl -fsSL https://deb.nodesource.com/setup_20.x \| sudo -E bash - && sudo apt-get install -y nodejs` |
| git | `winget install --id Git.Git -e` | `brew install git` | `sudo apt-get update && sudo apt-get install -y git` |
| **WSL2 + bubblewrap** — **Windows local dev only** (macOS/Linux run the sandbox natively; see [Why WSL2 on Windows?](#why-wsl2-on-windows) below) | `wsl --install` in an **elevated** PowerShell, then reboot; then inside the distro: `sudo apt-get install -y bubblewrap` | *Not required* | *Not required* |
| Azure CLI (`az`), logged in via `az login` — only needed for `azure:deploy`/`azure:upgrade`/`azure:verify` (not for local dev) | `winget install Microsoft.AzureCLI` | `brew install azure-cli` | `curl -sL https://aka.ms/InstallAzureCLIDeb \| sudo bash` |

`npm run setup` (`dev --setup`) checks the local-dev tools (git/.NET/Node)
itself and prints the matching install command above for your platform if
one is missing. On Windows it also emits an **advisory** warning if WSL2 is
not detected (it does not hard-fail on it, and does not check whether
`bubblewrap` is installed inside the distro — see the note below). It does not
check the Azure CLI, since local dev doesn't need it.

### Why WSL2 on Windows?

On **Windows**, `npm run dev` launches the API **inside WSL2** (via
`wsl --exec`), and it is a hard requirement for local dev there. Here's why:

Every agent run executes model-generated shell commands, so those commands
must run in a real sandbox with genuine **filesystem, PID, and network
isolation** — otherwise an agent command could read outside its workspace or
exfiltrate data. The API picks a sandbox backend at startup:

- **Native Windows** ("processcontainer") **cannot enforce network
  isolation** — its own diagnostic warns *"unrestricted network on Windows
  (allowlist enforcement unavailable); data exfiltration surface is open."* On
  a stock Windows 11 host it is also skipped entirely (its highest tier needs
  ViVeTool velocity keys that aren't enabled by default), so the runtime falls
  through to WSL2 regardless.
- **WSL2 + bubblewrap (`bwrap`)** provides real isolation: a workspace-confined
  filesystem plus PID/user/**network** namespaces (network is fully off unless
  a run explicitly enables it). This is the backend Windows local dev relies
  on.
- Without WSL2, `npm run dev` fails outright at `wsl.exe`. With WSL2 but
  **without** `bubblewrap` in the distro, there is **no safe fallback**: inside
  the WSL distro (a Linux environment) the sandbox picks its backend from the
  Linux executor chain in
  [`SandboxExecutorFactory.cs`](https://github.com/sabbour/agentweaver/blob/dev/packages/Agentweaver.SandboxExec/SandboxExecutorFactory.cs)
  — `LinuxBwrapExecutor` (needs `bwrap`) → `LinuxNativeMxcSandboxExecutor`
  (needs `lxc`) → `PassthroughExecutor`. If neither `bwrap` nor `lxc` is present,
  it falls all the way through to **`PassthroughExecutor`, which runs
  agent-generated commands directly on the host with ZERO isolation** (no
  filesystem confinement, no PID/network namespaces). This is **not** the
  `unshare`-based degradation the Windows-host native path uses — it is *no
  sandbox at all*. **Install `bubblewrap` in the distro** (as in the setup
  above) so local dev actually runs sandboxed; a missing `bubblewrap` means your
  agent runs are completely unsandboxed, not merely "weaker."

  > **Open question (not decided here):** whether the runtime should *hard-fail*
  > instead of silently selecting `PassthroughExecutor` when no isolation backend
  > is available is a separate runtime-behavior decision for the maintainer; this
  > note only documents the current fall-through behavior accurately.

**macOS and Linux are unaffected** — the API runs directly (no WSL) and the
runtime already prefers native `bwrap` there, so no WSL2 is involved in local
dev on those platforms.

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

Deploying to Azure? Also install the Azure CLI:

```powershell
winget install Microsoft.AzureCLI --accept-source-agreements --accept-package-agreements
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

Deploying to Azure? Also install the Azure CLI:

```bash
brew install azure-cli
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

Deploying to Azure? Also install the Azure CLI:

```bash
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
```

:::

`npm run setup` re-checks the local-dev tools itself after you install them,
and prints the matching command again if anything is still missing or out
of date.

Also needed, not installable via a package manager:

- An existing local Git repository that the agent can target
- A GitHub account with an active GitHub Copilot subscription — the web UI signs you in via OAuth.
- A **GitHub OAuth App** — needed so the API can perform the OAuth sign-in flow. [Create one](https://github.com/settings/developers) with callback URL `http://localhost:5000/auth/github/callback` for local dev. **Deploying to Azure instead?** The callback URL must match the Gateway's public host, not localhost — see the [callback URL note](#deploy-to-azure-one-command) below.

---

## Local development quick start

> **Windows:** complete [WSL2 + bubblewrap](#why-wsl2-on-windows)
> setup before this loop. `npm run setup` warns when WSL2 is missing, but
> `npm run dev` actually runs the API inside WSL2 and cannot work without it.

From a cloned checkout, prepare dependencies once:

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup
```

`setup` checks git/.NET/Node (and advises about WSL2 on Windows), installs
`apps/web` dependencies, restores .NET packages, and scaffolds
`apps/Agentweaver.Api/appsettings.Development.json` without overwriting an
existing file. It does **not** start servers or touch Azure.

Before first sign-in, complete [Configure local authentication and model
access](#1-configure-local-authentication-and-model-access). Then start both
servers from the repo root:

```bash
npm run dev
```

The first API build can take roughly **1–3 minutes**, especially on Windows
when the checkout is mounted into WSL2. Subsequent starts are faster; use
`npm run dev -- --skip-build` only when the Release build is already current.
API startup logs stream in the same terminal. The loop is ready when it prints:

```text
API is ready
Web UI is ready

API   http://localhost:5000
Web   http://localhost:5173
```

Leave that terminal running and open <http://localhost:5173>. If the API exits
during startup, `npm run dev` now fails immediately and leaves the actual .NET
error visible above the failure message instead of waiting silently.

---

## How local and Azure testing fit the branch flow

Git branching and the local runtime are independent. `npm run dev` runs
whatever commit is checked out in the current branch/worktree; it does not
contact GitHub or update protected `dev`. Use it freely for
pure local iteration before a PR exists.

Azure dev/test is also available **before merge**. Run `npm run azure:deploy`
or `npm run azure:upgrade` from any feature branch/worktree to validate that
exact `HEAD` on a personal or shared real cluster. The Azure cluster is the
integration/staging **environment**; there is intentionally no integration or
staging **git branch**.

```text
feature branch/worktree
  ├─ npm run dev                         local-only test, at any time
  ├─ azure:deploy / azure:upgrade ─────> Azure dev/test/staging environment
  │                                      (optional manual verification at any time)
  └─ PR CI ─> update to latest dev ─> required CI rerun ─> protected dev
                                                               │
                                                               └─ green SHA ─> release/vX.Y.Z soak ─> promotion to main
                                                                                                      └─ exact main SHA
                                                                                                           └─ tag/publish/deploy
```

GitHub Merge Queue is unavailable while this repository is owned by the
personal `sabbour` account. The enforceable fallback is standard protection:
every normal change uses a PR, the branch must be up to date with `dev`, and the four
blocking checks rerun before squash merge. Concurrent PRs may need repeated
updates/retests when another PR merges first. The
[Branch Topology Activation Plan](../../CONTRIBUTING.md#branch-topology)
describes retained growth guidance. Official releases are cut from an exact promoted
`main` commit; see
[RELEASING.md](../../RELEASING.md).

---

## Deploy to Azure (one command)

Prefer a live Azure deployment over local dev? Skip local setup entirely and run the smart installer instead:

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run azure:deploy
```

Use `azure:deploy` for the **first or full idempotent provisioning** of a
personal, shared dev/test, or staging environment. Once that environment
exists, use `npm run azure:upgrade` for the normal edit → build → redeploy
loop from the current `HEAD` — including an unmerged feature-branch `HEAD` —
then `npm run azure:verify` if you want to rerun only the live checks. For a
shared environment, coordinate ownership and prefer a clean commit;
`azure:upgrade -- --allow-dirty` is only a personal/throwaway test escape hatch.

> **This is a dev/test/staging deploy — not a release.** `azure:deploy` (and
> `azure:upgrade`) stand up or update a live Azure environment for development,
> testing, or staging use. They do **not** bump the version, create a git tag,
> or publish a GitHub Release, and you can run them as often as you like.
> Cutting an official, versioned release of the project is a *separate* command
> (`npm run azure:release`) with its own process — see
> [RELEASING.md](https://github.com/sabbour/agentweaver/blob/dev/RELEASING.md).

With no flags, in an interactive terminal, this prompts you through Azure
subscription, resource group, location, cluster/ACR/Key Vault names (smart
defaults, all editable), and your GitHub OAuth App client ID + secret — then
provisions AKS, PostgreSQL, Key Vault, ACR, identity, and monitoring, builds
and pushes images, and deploys and verifies the release. It prints an
outputs summary at the end (cluster, ACR, gateway host, **GitHub OAuth
callback URL**, verification pass/fail counts) and never prints the OAuth
client secret.

> **GitHub OAuth App callback URL — local vs. Azure.** The callback URL you
> register on the GitHub OAuth App must match where the app is actually
> running:
> - **Local dev** → `http://localhost:5000/auth/github/callback`
> - **Azure deployment** → `https://<gateway-host>/auth/github/callback` —
>   printed verbatim as **GitHub OAuth callback URL** in the outputs summary
>   above once the deploy finishes. It's only known *after* the AKS App
>   Routing managed certificate is provisioned on your first deploy — there's
>   no way to know it beforehand. So: deploy first with any placeholder
>   callback URL, then go back to the [OAuth App
>   settings](https://github.com/settings/developers) and paste in the
>   printed callback URL. GitHub OAuth Apps only support one callback URL
>   each — if you also do local dev, create a second OAuth App dedicated to
>   `localhost`, or swap the callback URL each time you switch between local
>   and Azure.

For non-interactive deploys (flags, environment variables, or a
`--params-file`), upgrading an existing deployment (`npm run azure:upgrade`),
and the full flag reference, see the [README's Deploy to Azure
section](https://github.com/sabbour/agentweaver#deploy-to-azure) and the
[npm script reference](#npm-script-reference) below.

---

## npm script reference

Every build/deploy/upgrade/release/dev workflow runs through one cross-platform Node CLI (`scripts/azure/cli.mjs`) — no bash or PowerShell required on any platform. The root `package.json` exposes these scripts:

| Script | What it does |
|---|---|
| `npm start` / `npm run dev` | Local dev orchestration (API + web), browser auto-open disabled. Alias for `azure:dev -- --no-browser`. |
| `npm run setup` | Local dev environment setup only: checks prerequisites (git/.NET 10/Node 20+), installs `apps/web`'s npm deps, restores .NET packages — skips the Azure pipeline entirely. This is what the [local development quick start](#local-development-quick-start) uses. Alias for `dev -- --setup`. |
| `npm run azure:deploy` | The smart installer. With no flags **and** an interactive terminal, prompts you through subscription/resource group/location/cluster names/GitHub OAuth. With flags, env vars, or a params file (or no TTY), it runs non-interactively instead. Always deploys to Azure — for local-only setup use `npm run setup` instead. |
| `npm run azure:upgrade` | Builds a new immutable image tag (defaults to the current git HEAD short SHA), redeploys, and cycles the AgentHost warm pool. Refuses to run on a dirty working tree unless you pass `-- --allow-dirty`. |
| `npm run azure:release` | Current semver publication workflow: bumps `VERSION`, tags, generates a GitHub release, and composes over the shared deploy engine. Its direct commit/push behavior must be split into protected release-PR preparation + exact-SHA publication before protected-branch enforcement; see [RELEASING.md](../../RELEASING.md#cutting-a-release). `--dry-run` remains safe. |
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

## 1. Configure local authentication and model access

`npm run dev` runs the API with `ASPNETCORE_ENVIRONMENT=Development`.
`npm run setup` copies
`apps/Agentweaver.Api/appsettings.Development.json.example` to
`appsettings.Development.json` if the destination is absent. Put only
non-secret local settings there:

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

The `ClientId` and `ClientSecret` come from your GitHub OAuth App settings
page. `CallbackUrl` must exactly match its **Authorization callback URL**.
Store secrets with .NET user-secrets, never in `appsettings*.json`:

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"
dotnet user-secrets set "Providers:GitHubCopilot:GitHubToken" "<github-token-with-copilot-access>"
cd ../..
```

## 2. Start the local development loop

From the repository root:

```powershell
npm run dev
```

This builds and starts the API, waits for `/health`, then starts Vite and waits
for the Web UI. Use `Ctrl+C` to stop the loop. For component-by-component
debugging, `npm run dev:api` and `npm run dev:web` remain available. On
Windows, the API-only script runs native `dotnet`; do not use it to execute
agents because it bypasses the supported WSL2 + `bubblewrap` path. Use the
full `npm run dev` loop for sandboxed agent runs.

## 3. Submit a run from the web UI

Open <http://localhost:5173> after both readiness messages appear. The default
Web configuration targets the local API at <http://localhost:5000>; set
`VITE_API_URL` in `apps/web/.env` only when intentionally pointing the Web UI
at a different API.

## 4. Create a project, run, and review

1. **Sign in** with GitHub when the web UI loads.
2. **Create a project** from the Project Gallery — blank or cloned from a GitHub repo.
3. **Cast a team** (optional) or start straight away.
4. **Start a task** from the project Board. The coordinator drafts an OutcomeSpec; confirm it to dispatch work.
5. **Watch** the live topology and per-agent execution stream.
6. **Review and merge** the assembled diff once the run reaches Human Review — nothing lands on your branch until you approve.

For full end-to-end walkthroughs, see [Example walkthroughs](./example-scenarios).
