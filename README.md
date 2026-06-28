# Agentweaver

> ⚠️ **Alpha software.** Agentweaver is under active development. Expect breaking changes, incomplete features, and rough edges. Not intended for production use.

Agentweaver runs an AI agent on a task inside a sandboxed git worktree, streams every step live, and waits for human review before anything merges.

Read the docs: [docs/index.md](docs/index.md)

## Features

- **Sandboxed execution** — every agent run lives in an isolated git worktree with Kata VM isolation on AKS
- **Live streaming** — watch every agent step, tool call, and file change in real time
- **Human-in-the-loop review** — nothing merges until you approve the assembled diff
- **Sandbox browser preview** — open a live in-browser preview of the app running inside a run's sandbox (port-forward)
- **MCP server** — expose Agentweaver runs and outcomes as MCP tools for Claude Desktop and compatible clients

## Quick start

**Local dev — one command:**
```bash
curl -fsSL https://raw.githubusercontent.com/asabbour/agentweaver/main/install.sh | bash
```
```powershell
irm https://raw.githubusercontent.com/asabbour/agentweaver/main/install.ps1 | iex
```

**Deploy to AKS — one command** (requires `az login` + `kubectl` + `envsubst` + `openssl`):
```bash
curl -fsSL https://raw.githubusercontent.com/asabbour/agentweaver/main/install.sh | bash -s -- --aks
```
```powershell
& ([scriptblock]::Create((irm 'https://raw.githubusercontent.com/asabbour/agentweaver/main/install.ps1'))) -Aks
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

## Key docs

- [Getting started](docs/guide/getting-started.md)
- [API reference](docs/reference/api.md)
- [MCP server reference](docs/reference/mcp.md)
- [Architecture overview](docs/architecture/overview.md)
