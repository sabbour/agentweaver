# Agentweaver Product Overview

## Register

product

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

## Brand Personality

Three words: **calm, precise, approachable.**

Agentweaver watches long-running, potentially risky agent work. The UI's job is to keep the operator oriented and unhurried — never intimidating, never theatrical. It should read like a quiet, warm workbench: confident and exact where it matters (diffs, run state, approvals), friendly and legible everywhere else. Rich in capability, calm in presentation.

## Design References

- **Microsoft Copilot (copilot.com), Day theme** — warm-monochrome surface, near-black primary, soft rounded panels, generous whitespace, single left rail, no chrome-heavy top bar.
- **"M" / Scout (Microsoft)** — the authoritative warm light token set and component states (button/input/dialog): soft rings, subtle hover fills, near-black actions, gentle radii.

The house style is **warm monochrome**: a single warm-neutral canvas, near-black ink and actions, and no blue. Color is reserved for status only (green healthy, red danger, amber alpha/warning).

## Anti-references

Agentweaver must explicitly NOT look like:

- **Azure portal blue / enterprise-dense grids** (primary anti-reference) — no Communication Blue brand, no command-bar-over-dense-table default, no resource/blade chrome.
- A dark theatrical agent console (green-on-black terminal drama).
- A generic SaaS-cream startup dashboard, or a rainbow/gradient-heavy "AI product" look.

## Accessibility & Inclusion

Best-effort for now (no formal WCAG commitment yet), but hold sensible defaults: readable body contrast (avoid light-gray-on-cream), status never conveyed by color alone (pair with label/icon), visible focus, and reduced-motion support on any animation.

## Current positioning

Agentweaver is not a production-ready autonomous engineering replacement. It is a developer platform for experimenting with AI-agent workflows while preserving the controls expected in professional software delivery: sandboxing, review, auditability, and explicit merge approval.
