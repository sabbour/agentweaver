# Agentweaver

Agentweaver runs an AI agent on a task inside a sandboxed git worktree, streams every step live, and waits for human review before anything merges.

Read the docs: [docs/index.md](docs/index.md)

## Quick start

```powershell
dotnet run --project apps/Agentweaver.Api
dotnet run --project apps/Agentweaver.Mcp
npm --prefix apps/web install
npm --prefix apps/web run dev
```

## Key docs

- [Getting started](docs/guide/getting-started.md)
- [API reference](docs/reference/api.md)
- [MCP server reference](docs/reference/mcp.md)
- [Architecture overview](docs/architecture/overview.md)
