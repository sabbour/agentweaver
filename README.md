<p align="center">
  <img src="docs/public/agentweaver.png" alt="Agentweaver logo" width="128" />
</p>

# Agentweaver

> ⚠️ **Alpha software.** Agentweaver is under active development. Expect breaking changes, incomplete features, and rough edges. Not intended for production use.

Agentweaver runs AI agents inside sandboxed git worktrees, mirrors run events into a shared store so any replica can stream them live, and waits for human review before anything merges.

📖 **[Read the docs at sabbour.me/agentweaver](https://sabbour.me/agentweaver/)** — or browse the source in [docs/index.md](docs/index.md)

## Features

- **Sandboxed execution** — every agent run lives in an isolated git worktree with Kata VM isolation on AKS
- **Live streaming** — watch every agent step, tool call, and file change in real time from any replica
- **Human-in-the-loop review** — nothing merges until you approve the assembled diff
- **Sandbox browser preview** — open a live in-browser preview of the app running inside a run's sandbox (port-forward)
- **MCP server** — expose Agentweaver runs and outcomes as MCP tools for Claude Desktop and compatible clients

## Quick start

**Local dev — one command:**
```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash
```
```powershell
irm https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1 | iex
```

**Deploy to AKS — one command** (requires `az login` + `kubectl` + `envsubst` + `openssl`):
```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
```
```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/sabbour/agentweaver/main/install.ps1'))) -Aks
```

> **AKS flags:** `--skip-postgres` / `-SkipPostgres` and `--skip-oauth-key` / `-SkipOauthKey`
> skip optional provisioning steps if those resources already exist.

<details>
<summary>From a cloned checkout</summary>

**Windows (PowerShell):**
```powershell
.\install.ps1            # local dev — checks prereqs, installs deps, launches start-dev.ps1
.\install.ps1 -Aks       # AKS deploy (requires WSL2 + az login + kubectl)
```

**macOS / Linux (bash):**
```bash
bash install.sh          # local dev — checks prereqs, installs deps, prints start commands
bash install.sh --aks    # AKS deploy (requires az login + kubectl + envsubst + openssl)
```
</details>

## Build & deploy

### Local build

```bash
# Build the .NET solution
dotnet build agentweaver.sln

# Build the web frontend
npm --prefix apps/web run build
```

### Run locally

Start each component from the repo root (three terminals):

```bash
# Terminal 1 — API backend
dotnet run --project apps/Agentweaver.Api

# Terminal 2 — MCP server (optional)
dotnet run --project apps/Agentweaver.Mcp

# Terminal 3 — Web UI (Vite dev server, hot reload)
npm --prefix apps/web run dev
```

> **Windows shortcut:** `.\start-dev.ps1` launches all three automatically.

Configure the GitHub OAuth client secret for local dev with .NET user-secrets (do not put it in `appsettings*.json`):

```powershell
cd apps/Agentweaver.Api
dotnet user-secrets set "Auth:GitHub:ClientSecret" "<your-oauth-app-client-secret>"
```

### Deploy / redeploy to AKS

**First deploy:**
```bash
curl -fsSL https://raw.githubusercontent.com/sabbour/agentweaver/main/install.sh | bash -s -- --aks
```

**Redeploy with a new image tag** (build, push, and redeploy in one command):
```bash
bash install.sh --aks --image-tag <git-sha>
```
```powershell
.\install.ps1 -Aks -ImageTag <git-sha>
```

> **Never use `:latest`.** The default tag is the short git SHA (`git rev-parse --short HEAD`). Always pin to a specific SHA for reproducible deployments. Image tags are immutable per build.

## AKS architecture

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
flowchart TB
    client(["🌐 Browser / AI client<br/>HTTPS :443"])

    subgraph aks["AKS Cluster — namespace: agentweaver"]
        gw{{"Gateway<br/>approuting-istio · TLS :443"}}

        subgraph core["Core services"]
            fe(["Frontend ×2<br/>React SPA"])
            api(["API ×2<br/>REST · auth · SSE"])
            worker(["Worker ×1+HPA<br/>pod-per-run mode"])
            mcp(["MCP ×1<br/>OAuth · MCP protocol"])
        end

        subgraph exec["Kata VM sandbox execution"]
            ahpool(["AgentHost Warm Pool ×2<br/>agentweaver-agent-host<br/>standby → /configure → active<br/>A2A :8088"])
        end

        ws[("Workspace PVC<br/>Azure Files RWX")]
    end

    kv(["Azure Key Vault<br/>user tokens · app secrets"])
    pg(["Azure PostgreSQL<br/>runs · RunEvents · memory"])
    gh(["GitHub<br/>OAuth · api.github.com"])

    client -->|"HTTPS :443"| gw
    gw -->|"/"| fe
    gw -->|"/api /auth /stream"| api
    gw -->|"/mcp"| mcp
    mcp -->|"API calls :8080"| api
    api & worker -->|"SandboxClaim + POST /configure"| ahpool
    api & worker --- ws
    ahpool --- ws
    api -->|"RunEvents cursor reads"| pg
    worker -->|"RunEvents writes"| pg
    api -->|"workload identity"| kv
    ahpool -->|"fetch user token"| kv
    api & mcp -->|"OAuth · REST"| gh

    classDef svc fill:#F3F2F1,stroke:#8A8886,color:#242424
    classDef core fill:#CFE4FA,stroke:#0F6CBD,stroke-width:2px,color:#242424
    classDef worker fill:#D9EFD9,stroke:#107C10,stroke-width:2px,color:#242424
    classDef runtime fill:#DDF3DD,stroke:#107C10,color:#242424
    classDef data fill:#FFF4CE,stroke:#C19C00,color:#242424
    classDef ext fill:#F0E8F8,stroke:#8764B8,color:#242424

    class gw,fe,mcp svc
    class api core
    class worker worker
    class ahpool runtime
    class ws data
    class kv,pg,gh ext
```

> Full component breakdown, networking, security model, and warm-pool lifecycle: [AKS Architecture →](docs/architecture-aks.md)

## Key docs

- [Getting started](docs/guide/getting-started.md)
- [API reference](docs/reference/api.md)
- [MCP server reference](docs/reference/mcp.md)
- [AKS architecture](docs/architecture-aks.md)
