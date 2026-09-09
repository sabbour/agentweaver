<p align="center">
  <img src="docs/public/agentweaver.png" alt="Agentweaver logo" width="128" />
</p>

# Agentweaver

> ⚠️ **Alpha software.** Agentweaver is under active development. Expect breaking changes and incomplete features. Do not use it in production.

**Turn a goal into coordinated, reviewable work by a team of AI agents.**

Agentweaver is a self-hosted multi-agent orchestration platform for teams that need more than one agent in a chat window. It turns a stated outcome into a work plan, runs specialist agents in isolated worktrees, and keeps the final result under human control.

It is for software delivery, content authoring, product discovery, incident response, and other knowledge work. Use it when you need parallel work, clear ownership, live progress, and a reviewable result.

📖 **[Read the documentation](https://sabbour.me/agentweaver/)** or browse the source in [docs/index.md](docs/index.md).

## Why Agentweaver

- **Keep control of agent work.** Review the proposed outcome before work starts. Review the assembled result before it merges.
- **Coordinate specialists.** Reusable blueprints assign named agents to roles. Workflows define how those roles work together.
- **Inspect every run.** Watch agent events and topology live. Runs keep their work in isolated git worktrees.
- **Retain useful context.** Team memory and decisions give later runs the context that matters.

## What it includes

| Capability                                  | What it does                                                                                    |
| ------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| [Projects](docs/guide/projects.md)          | Binds a repository and AI configuration to teams, runs, and memory.                             |
| [Teams and blueprints](docs/guide/teams.md) | Defines reusable roles, policies, and agent teams.                                              |
| [Workflows](docs/guide/workflows.md)        | Runs configurable multi-role flows for delivery, research, content, operations, and evaluation. |
| [Board and runs](docs/guide/board.md)       | Organizes work and shows active orchestration from plan to review.                              |
| [Review](docs/guide/review.md)              | Uses an RAI check and human approval before a result merges.                                    |
| [MCP server](docs/guide/mcp-cli.md)         | Lets MCP clients work with Agentweaver through authenticated tools.                             |

## Quick start

Install Git, Node.js, and the .NET 10 SDK. On Windows, install WSL2 and `bubblewrap` before you run local agent work.

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup
npm run dev
```

`npm run setup` prepares the local application. `npm run dev` starts the API at `http://localhost:5000` and the web UI at `http://localhost:5173`.

Before you sign in, configure local authentication and model access. The [Getting started guide](docs/guide/getting-started.md) has platform setup, authentication, and first-run instructions.

## Learn more

- [Create a project and submit a run](docs/guide/getting-started.md)
- [Connect an MCP client](docs/guide/mcp-cli.md)
- [Configure authentication and model access](docs/guide/authentication.md)
- [Deploy to Azure Kubernetes Service](docs/guide/deployment-aks.md)
- [Understand the architecture](docs/guide/architecture-aks.md)
- [Contribute to Agentweaver](CONTRIBUTING.md)
- [Plan or publish a release](RELEASING.md)
