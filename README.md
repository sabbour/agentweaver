# Scaffolders

Scaffolders runs an AI agent on a task inside a sandboxed git worktree, streams every step live, and waits for human review before anything merges.

Read the docs: [docs/index.md](docs/index.md)

## Quick start

```powershell
dotnet run --project apps/Scaffolder.Api
dotnet run --project apps/Scaffolder.Cli -- run submit
npm --prefix apps/web install
npm --prefix apps/web run dev
```

## Key docs

- [Getting started](docs/guide/getting-started.md)
- [API reference](docs/reference/api.md)
- [CLI reference](docs/reference/cli.md)
- [Architecture overview](docs/architecture/overview.md)
