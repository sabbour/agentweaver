# Agentweaver Product Overview

Agentweaver is an alpha platform for running AI coding agents safely and observably. It gives each agent task an isolated git worktree, streams the run in real time, and keeps a human review gate between generated changes and merge.

## Who it is for

- Engineers who want AI agents to work on real repositories without giving up review control.
- Platform teams that need sandboxing, auditability, and repeatable agent execution.
- Tool builders who want to expose agent runs and outcomes through a web UI, API, and MCP server.

## Core value

Agentweaver turns an agent request into a trackable run with clear boundaries:

1. The agent works in a sandboxed branch/worktree instead of the main checkout.
2. Every step, tool call, and file change is streamed for live inspection.
3. The resulting diff is assembled for human approval.
4. Nothing merges until a reviewer accepts the outcome.

## Key capabilities

- **Sandboxed execution:** agent runs are isolated in git worktrees, with AKS deployments using Kata VM isolation.
- **Live observability:** users can watch run events, tool activity, and file changes as they happen.
- **Human-in-the-loop review:** generated changes stay reviewable before merge.
- **Browser preview:** web work can be previewed from inside the run sandbox.
- **MCP integration:** Agentweaver exposes runs and outcomes to MCP-compatible clients.
- **Coordinator orchestration:** multi-agent work can be planned, decomposed, dispatched, observed, and assembled through a durable coordinator flow.

## Product principles

- Keep humans accountable for merges.
- Prefer isolated, reproducible execution over shared mutable workspaces.
- Make agent activity inspectable while it is happening, not only after it finishes.
- Reuse existing platform capabilities instead of creating parallel review, merge, memory, or policy systems.
- Treat the project as alpha software and optimize for fast, safe iteration.

## Current positioning

Agentweaver is not a production-ready autonomous engineering replacement. It is a developer platform for experimenting with AI-agent workflows while preserving the controls expected in professional software delivery: sandboxing, review, auditability, and explicit merge approval.
