# Experience guide

This section explains how Agentweaver **feels to use** — the end-to-end journeys a person takes through the product, across both surfaces it exposes:

- The **web UI**, a browser application for humans who want to see and steer work.
- The **MCP server**, a tool surface that lets an AI client (such as a Copilot agent) drive the same system programmatically.

Both surfaces sit on the same backend and the same data model, so a project created in one is visible in the other, and a run submitted from an MCP client shows up live on the board in the browser. These docs describe each journey once and then map the web actions to their MCP equivalents, so you can move between surfaces without relearning the product.

Where the [guide](../guide/) tells you the steps to accomplish a task and the [deep dive](../deep-dive/00-system-overview.md) explains how the system is built, this section focuses on the **experience and the reasoning behind it** — what each screen and tool is for, what mental model it assumes, and how the pieces connect.

## Start here

- [**Overview**](./00-overview.md) — the two surfaces, the shared backend, the navigation taxonomy, and the UI ↔ MCP mapping. Read this first.

## Journeys

| Journey | What it covers |
|---|---|
| [Onboarding & authentication](./onboarding-auth.md) | Entra sign-in, separate GitHub capabilities, model readiness, and MCP broker consent. |
| [Projects](./projects.md) | The project gallery, creating and linking projects, the project dashboard, and settings. |
| [Runs, board & watch](./runs-board-watch.md) | Submitting work, the board buckets, and watching a run unfold live. |
| [Coordinator & orchestration](./coordinator-orchestration.md) | Define Outcome versus Direct start, live topology, child runs, and steering. |
| [Review, workspace & merge](./review-workspace-merge.md) | Human review, artifact diffs, browsing the workspace, and local merge. |
| [Team, casting & memory](./team-casting-memory.md) | The agent roster, the casting wizard, decisions, and durable memory. |
| [Workflows & backlog](./workflows-backlog.md) | Workflow definitions, the backlog board, and unattended pickup. |
| [Operations](./operations.md) | Settings, diagnostics, heartbeat, flow, and sandbox policy. |
| [MCP client](./mcp-client.md) | An end-to-end assistant journey, representative tools, and generated catalog discovery. |
| [Assistant sessions](./assistant-sessions.md) | Personal conversations, durable history, and renewable MCP broker access. |
| [Project skills](./project-skills.md) | Acquiring trusted instructions and assigning them to team members. |

## How to read this section

Each journey doc follows the same shape: the **mental model** first, then the **web UI experience**, then the **MCP equivalents**, then **edge cases** and the reasoning behind the design. Diagrams show the flow; cross-links connect related journeys. You can read straight through in the order above, or jump to the journey that matches what you are trying to do.
