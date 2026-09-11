<p align="center">
  <img src="docs/public/agentweaver.png" alt="Agentweaver logo" width="128" />
</p>

# Agentweaver

> ⚠️ **Alpha software.** Agentweaver is under active development. Expect breaking changes and incomplete features. Do not use it in production.

**Run teams of AI agents on your own infrastructure.**

Describe the work you want done. Agentweaver can generate the agent roles, skills, and workflow for that work, then run the team in isolated environments on infrastructure you control.

Workflows turn probabilistic agent work into a governed path toward a defined outcome, with the gates and approvals you set. Start and supervise work in the Agentweaver interface or through MCP from an assistant, editor, or CLI.

![Architecture diagram: Clients connect through identity and authorization services to the API control plane. The API coordinates workflows, git services, catalog and memory, and sandboxed AgentHost execution on AKS. PostgreSQL, Key Vault, Git repositories, and model providers provide durable state and external services.](docs/public/pitch-architecture.png)

📖 **[Read the documentation](https://sabbour.me/agentweaver/)** or browse the source in [docs/index.md](docs/index.md).

## Why Agentweaver

- **Generate the team and process.** Start from a description or reusable blueprint to define roles, skills, and the workflow the agents follow.
- **Set review gates.** Confirm the intended outcome before work starts and approve the assembled result before it merges.
- **Inspect every run.** Watch agent events and topology live. Runs keep their work in isolated git worktrees.
- **Use your preferred surface.** Operate the same projects, teams, workflows, and runs from the web interface or an MCP client.
- **Carry context forward.** Team memory and decisions give later runs prior constraints and learnings.

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
