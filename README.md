# Agentweaver

> ⚠️ **Alpha software.** Agentweaver is under active development. Expect breaking changes, incomplete features, and rough edges. Not intended for production use.

Agentweaver runs an AI agent on a task inside a sandboxed git worktree, streams every step live, and waits for human review before anything merges.

Read the docs: [docs/index.md](docs/index.md)

## Quick start

**Windows (PowerShell):**
```powershell
# Clone, then from the repo root:
.\install.ps1            # local dev (default) — checks prereqs, installs deps, launches start-dev.ps1
.\install.ps1 -Aks       # AKS deploy (requires WSL2 + az login + kubectl)
```

**macOS / Linux (bash):**
```bash
# Clone, then from the repo root:
bash install.sh          # local dev (default) — checks prereqs, installs deps, prints start commands
bash install.sh --aks    # AKS deploy (requires az login + kubectl + envsubst + openssl)
```

**One-liner (no clone needed):**
```bash
# bash
curl -fsSL https://raw.githubusercontent.com/asabbour/agentweaver/main/install.sh | bash
```
```powershell
# PowerShell
iwr https://raw.githubusercontent.com/asabbour/agentweaver/main/install.ps1 | iex
```

> **AKS flags:** `--skip-postgres` / `-SkipPostgres` and `--skip-oauth-key` / `-SkipOauthKey`
> skip the optional provisioning steps if those resources already exist.

## Key docs

- [Getting started](docs/guide/getting-started.md)
- [API reference](docs/reference/api.md)
- [MCP server reference](docs/reference/mcp.md)
- [Architecture overview](docs/architecture/overview.md)
