# Agentweaver.Api

ASP.NET Core Web API for the Agentweaver service — an AI-powered file-editing agent platform.

## Overview

Agentweaver lets users submit a natural-language task against a git branch. An AI agent runs in an isolated worktree, reads and writes files to complete the task, and streams each step live. The resulting diff is available for human review before merging back to the originating branch.

## Tech Stack

- **.NET 10** / ASP.NET Core
- **Microsoft.Extensions.AI** — model-agnostic AI abstractions
- **LibGit2Sharp** — git worktree management
- **SQLite** (Microsoft.Data.Sqlite) — run persistence

## Project Structure

```
Agentweaver.Api/
├── Contracts/       # Request/response DTOs
├── Git/             # Worktree & branch management
├── Infrastructure/  # DI setup, middleware
├── Runs/            # Run creation and lifecycle
├── Security/        # Auth/authz
└── Streaming/       # SSE event streaming
```

## Getting Started

```bash
dotnet run
```

The API starts on `https://localhost:5001` by default. See `appsettings.Development.json` for local configuration.

## Related Packages

| Package | Purpose |
|---|---|
| `Agentweaver.Domain` | Core domain models |
| `Agentweaver.SandboxFs` | Sandboxed file-system tools for the agent |
| `Agentweaver.AgentRuntime` | Agent loop execution |
