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
| Azure CLI (`az`), logged in via `az login` — only needed for `azure:provision-infra`/`azure:deploy-from-local`/`azure:verify` (not for local dev) | `winget install Microsoft.AzureCLI` | `brew install azure-cli` | `curl -sL https://aka.ms/InstallAzureCLIDeb \| sudo bash` |

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
- A Microsoft Entra application configured with `http://localhost:5000/auth/entra/callback` for local browser sign-in.
- GitHub Repo and Copilot Apps when your projects require their respective GitHub capabilities.

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

Azure dev/test is also available **before merge**. Run `npm run azure:provision-infra`
or `npm run azure:deploy-from-local` from any feature branch/worktree to validate that
exact `HEAD` on a personal or shared real cluster. The Azure cluster is the
integration/staging **environment**; there is intentionally no integration or
staging **git branch**.

```text
feature branch/worktree
  ├─ npm run dev                         local-only test, at any time
  ├─ azure:provision-infra / azure:deploy-from-local ─────> Azure dev/test/staging environment
  ├─ azure:deploy-from-commit <ref> ──────────────────────> exact teammate/PR/older commit
  │                                      (optional manual verification at any time)
  └─ PR CI ─> update to latest dev ─> required CI rerun ─> protected dev
                                                               │
                                                               └─ green SHA ─> release/vX.Y.Z soak ─> promotion to main
                                                                                                      └─ exact main SHA
                                                                                                           └─ publish vX.Y.Z
                                                                                                                └─ deploy from release
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
npm run azure:provision-infra
```

Use `azure:provision-infra` for the **first or full idempotent provisioning** of a
personal, shared dev/test, or staging environment. Once that environment
exists, use `npm run azure:deploy-from-local` for the normal edit → build → redeploy
loop from the current `HEAD` — including an unmerged feature-branch `HEAD` —
then `npm run azure:verify` if you want to rerun only the live checks. For a
shared environment, coordinate ownership and prefer a clean commit;
`azure:deploy-from-local -- --allow-dirty` is only a personal/throwaway test escape hatch.
Use `npm run azure:deploy-from-commit -- <sha-or-ref>` when the source is an
already-committed ref that should be deployed without switching this checkout.

> **This is a dev/test/staging deploy — not a release.** `azure:provision-infra` (and
> `azure:deploy-from-local`) stand up or update a live Azure environment for development,
> testing, or staging use. They do **not** bump the version, create a git tag,
> or publish a GitHub Release, and you can run them as often as you like.
> Publishing and deploying an official version use `release:publish` and
> `azure:deploy-from-release`; `azure:release` composes both for the first
> shipment. See
> [RELEASING.md](https://github.com/sabbour/agentweaver/blob/dev/RELEASING.md).

With no flags, in an interactive terminal, this prompts you through Azure
subscription, resource group, location, cluster/ACR/Key Vault names, Postgres
server/location/access-mode settings (smart defaults, all editable — navigate
list prompts with the arrow keys and Enter, or type a digit; falls back to the
classic numbered prompt automatically when raw-mode input isn't available),
your Microsoft Entra application (client) ID and tenant ID. It then provisions AKS,
PostgreSQL, Key Vault, ACR,
identity, and monitoring, builds and pushes images, and deploys and verifies
the release. It prints an outputs summary at the end (cluster, ACR, gateway
host, and verification pass/fail counts). Register the matching Entra redirect
URI for each environment, such as `https://<gateway-host>/auth/entra/callback`
for Azure or `http://localhost:5000/auth/entra/callback` for local development.

For non-interactive deploys (flags, environment variables, or a
`--params-file`), upgrading an existing deployment (`npm run azure:deploy-from-local`),
and the full flag reference, see the [README's Deploy to Azure
section](https://github.com/sabbour/agentweaver#deploy-to-azure) and the
[npm script reference](#npm-script-reference) below.

If AKS must stay in one region (for example, to keep using App Routing's
default domain support) but Azure blocks PostgreSQL provisioning there, set
`PG_LOCATION` / `--postgres-location` to a supported production region and
also set `PG_ACCESS_MODE=public` / `--postgres-access-mode public`. This is a
required pair: private delegated-subnet access is single-region only, so
Agentweaver now fails closed before any Azure calls if you try a cross-region
Postgres server while leaving access mode at the default `private`. Public mode
uses Azure CLI `--public-access 0.0.0.0`, which creates the standard firewall
rule that allows connections from Azure-hosted resources only. That is a wider
trust boundary than same-VNet private access and may include other customers'
Azure workloads, so keep the default `private` mode whenever Postgres can stay
in the same region as AKS.

Public mode also changes the in-cluster egress policy for Postgres. Private mode
keeps the `NetworkPolicy` objects that allow port 5432 to the delegated subnet's
CIDR; public mode instead applies `CiliumNetworkPolicy` objects that allow port
5432 to `<PG_SERVER_NAME>.postgres.database.azure.com` via Cilium's `toFQDNs`
rules (the same mechanism as the existing app FQDN allowlist). FQDN-based egress
is intentional: a public Flexible Server's IP is Azure-managed and can change,
and a static `ipBlock` allowlist would then silently block every pod-to-Postgres
connection until it was reconciled by hand.

Separately, when the installer is about to create a brand-new PostgreSQL
Flexible Server, it now performs a fast `az postgres flexible-server list-skus`
capability check against the target `PG_LOCATION` and `PG_SKU` first. If Azure
already knows the subscription/region/SKU combination is restricted or has no
supported server editions, the installer stops immediately with that reason
instead of waiting through a long `Provisioning` hang from
`az postgres flexible-server create`.

Log Analytics and Application Insights also default to the AKS region, but
those resource types are not available everywhere AKS is available. Before
creating a missing monitoring resource, the installer checks Azure provider
metadata. If the preferred region is unsupported, it chooses a nearby common
region and logs the substitution; existing monitoring resources remain in
place during upgrades. Set `MONITORING_LOCATION` or pass
`--monitoring-location <region>` to provide a different preferred region.

---

## npm script reference

Every provisioning/deployment/release/dev workflow runs through one
cross-platform Node CLI (`scripts/azure/cli.mjs`) — no bash or PowerShell
required on any platform. The root `package.json` exposes these scripts:

| Script | What it does |
|---|---|
| `npm start` / `npm run dev` | Local dev orchestration (API + web), browser auto-open disabled. Alias for `dev:open -- --no-browser`. |
| `npm run setup` | Local dev environment setup only: checks prerequisites (git/.NET 10/Node 20+), installs `apps/web`'s npm deps, restores .NET packages — skips the Azure pipeline entirely. This is what the [local development quick start](#local-development-quick-start) uses. Alias for `dev -- --setup`. |
| `npm run azure:provision-infra` | The smart installer. With no flags **and** an interactive terminal, prompts you through subscription/resource group/location/cluster names and Entra configuration. With flags, env vars, or a params file (or no TTY), it runs non-interactively instead. Always deploys to Azure — for local-only setup use `npm run setup` instead. |
| `npm run azure:deploy-from-local` | Builds a new immutable image tag (defaults to the current git HEAD short SHA), redeploys, and cycles the AgentHost warm pool. Refuses to run on a dirty working tree unless you pass `-- --allow-dirty`. |
| `npm run azure:deploy-from-commit -- <sha-or-ref>` | Fetches and resolves an arbitrary committed ref, deploys it through a temporary detached worktree, and leaves the caller's checkout untouched. |
| `npm run release:publish` | From a prepared exact-main checkout, creates the annotated tag and GitHub Release without deploying. |
| `npm run azure:deploy-from-release -- vX.Y.Z` | Deploys an existing published release from an exact checkout of its tag commit. |
| `npm run azure:release` | Composes `release:publish` and `azure:deploy-from-release` for the first shipment. |
| `npm run azure:verify` | Post-deploy health verification against the live cluster (pods, gateway, HTTP probes) — read-only, safe to run anytime. |
| `npm run dev:open` | Same as `npm run dev`, but opens your browser by default (omit `--no-browser`). No Azure calls. |
| `npm run dev:web` | Builds and starts only the web UI (Vite dev server) against an API you're already running separately. |
| `npm run dev:api` | Builds and runs only the .NET API. |
| `npm run docs:dev` / `docs:build` / `docs:preview` | This documentation site (VitePress). |

Every `azure:*` script (and `dev`/`setup`) accepts `-- --help` to print its full flag list, for example `npm run azure:provision-infra -- --help`. Useful flags across commands:

- **`azure:provision-infra`**: `--params-file <path>` (or `--config <path>`) for non-interactive deploys driven by a JSON/JSONC file (see `scripts/azure/params.example.json`) — the config precedence is **flags > env vars > params file > detected defaults > interactive prompt**, so any flag always wins. Also: `--resource-group`, `--cluster-name`, `--acr-name`, `--location`, `--monitoring-location`, `--node-vm-size`, `--keyvault-name`, `--postgres-server-name`, `--postgres-location`, `--postgres-ha-mode`, `--postgres-access-mode <private|public>`, `--namespace`, `--image-tag`, `--entra-client-id`, `--entra-tenant-id`, `--skip-postgres`, `--skip-oauth-key`, and `--image-source <acr-build|ghcr|custom>`. `MONITORING_LOCATION` (or `--monitoring-location`) is the preferred location for new Log Analytics and Application Insights resources; it defaults to `LOCATION`, and the installer selects and logs a supported fallback if Azure does not offer all missing monitoring resource types there. Existing monitoring resources are never moved. `NODE_VM_SIZE` (or `--node-vm-size`) controls the AKS system/app/kata pool SKU for **new** clusters; the default is now `Standard_D4s_v6`, and existing clusters are unaffected because the installer skips `az aks create` / `az aks nodepool add` when those resources already exist. Set `PG_SERVER_NAME` (or pass `--postgres-server-name`) to route around the rare Azure-global Flexible Server name collision where the default `agentweaver-pg` is already reserved. Set `PG_HA_MODE` (or pass `--postgres-ha-mode`) to override the default `ZoneRedundant` with one of `ZoneRedundant` or `Disabled` in regions/environments where zone-redundant HA is unavailable, such as early-access/canary regions. Set `PG_LOCATION` (or pass `--postgres-location`) to keep Postgres in the same region as the main cluster by default, or to move it elsewhere when Azure capacity/policy requires it. Cross-region Postgres requires `PG_ACCESS_MODE=public` / `--postgres-access-mode public`; the default `private` mode intentionally errors out before any Azure calls because delegated subnet connectivity is single-region only. Public mode uses Azure's `0.0.0.0` "allow Azure services/resources" firewall rule rather than an unrestricted internet-open range. When using `--image-source ghcr`, pass `--ghcr-ref <ref>` and use only immutable refs (`vX.Y.Z` published releases or `sha-<hex>` tags); moving tags such as `dev`, `main`, `latest`, and `rc-*` are rejected. The GHCR owner/repository is always derived from the repo's GitHub origin remote, `--ghcr-token`/`GHCR_TOKEN` is available for private-package auth, and `--force` allows an intentional overwrite of a conflicting existing ACR tag. When using `--image-source custom`, pass all four fully-qualified refs together: `--image-api`, `--image-frontend`, `--image-mcp`, and `--image-agent-host`. Each ref must include an explicit registry and either a tag or digest. Custom mode is an explicit trust-boundary override: the installer imports exactly the images you specify, so use only registries and images you trust.
- **`azure:deploy-from-local`**: `--allow-dirty` to bypass the clean-working-tree check (personal/throwaway testing only).
- **`azure:deploy-from-commit`**: one required SHA or ref; no dirty-tree option because only committed source is eligible.
- **`azure:deploy-from-release`**: positional existing `vX.Y.Z` tag; the checkout must be clean and at that tag commit.
- **`dev:open` / `dev` / `setup`**: `--no-browser` (skip opening a browser tab), `--skip-build` (skip the web build step), `--setup` (local-only setup, no servers started — this is what `npm run setup` runs).
- **`release:publish` / `azure:release`**: `--dry-run`; `azure:release -- --resume vX.Y.Z` resumes a partially completed first shipment.

## 1. Configure local authentication and model access

`npm run dev` runs the API with `ASPNETCORE_ENVIRONMENT=Development`.
`npm run setup` copies
`apps/Agentweaver.Api/appsettings.Development.json.example` to
`appsettings.Development.json` if the destination is absent. Put only
non-secret local settings there:

```json
{
  "Auth": {
    "Entra": {
      "ClientId": "<your-entra-application-client-id>",
      "TenantId": "<your-entra-tenant-id>",
      "RedirectUri": "http://localhost:5000/auth/entra/callback",
      "FrontendUrl": "http://localhost:5173"
    }
  },
  "Providers": {
    "GitHubCopilot": {
      "Model": "claude-sonnet-5"
    }
  }
}
```

Use an Entra app registration's application (client) and tenant IDs. Its
redirect URI must exactly match `http://localhost:5000/auth/entra/callback`.
Repository access and GitHub Copilot use separate browser handoffs. Microsoft Entra ID remains the browser sign-in authority.

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

1. Sign in with Microsoft Entra ID.
2. If model-provider setup is required, ask a Platform Admin to complete it.
3. Create a blank project for local agent work.
4. If you need pull-request publishing, authorize repository access.
5. Cast a team, or continue without one.
6. Start a task from the project Board.
7. Review the OutcomeSpec. Then dispatch the work.
8. Watch the live topology and agent streams.
9. Review the assembled diff. Merge it when you approve the result.

For full end-to-end walkthroughs, see [Example walkthroughs](./example-scenarios).

## 5. Connect an MCP client

You can also operate Agentweaver from Claude Desktop, VS Code, GitHub Copilot CLI,
or GitHub Copilot desktop.

1. Sign in to the Agentweaver web app first. On deployments that use Microsoft
   Entra ID, complete the Entra sign-in.
2. Open **Account settings → MCP clients** and copy the displayed URL. For a
   hosted deployment, it is exactly `https://<deployment-origin>/mcp`.
3. Add that URL as a remote HTTP MCP server in your client. Do not add an
   authorization header or copy a bearer token.
4. Connect. The client discovers Agentweaver's OAuth metadata, opens a browser
   when sign-in is required, and completes authorization code + PKCE after you
   approve the `mcp:invoke` consent request.
5. Return to the client and confirm that the Agentweaver server is connected and
   its tools are listed.
6. In a GitHub Copilot client, install and select the **Agentweaver Driver**
   custom agent so Copilot uses the MCP tools with the correct discovery,
   confirmation, supervision, and review workflow.

The client stores and refreshes its OAuth credentials. Credentials do not belong
in the URL, shell command, or checked-in configuration. See
[Connect an MCP client](./mcp-cli) for the current setup path and validation step
for each supported client.
