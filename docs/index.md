---
layout: home
hero:
  name: Agentweaver
  text: Reviewable agent runs in a sandboxed worktree
  tagline: Agentweaver runs an AI agent on a task inside a sandboxed git worktree and lets you review the changes before anything merges.
  actions:
    - theme: brand
      text: Getting started
      link: /guide/getting-started
    - theme: alt
      text: API reference
      link: /reference/api
    - theme: alt
      text: MCP server
      link: /reference/mcp
---

## How it works

Agentweaver starts from an existing local Git repository and an originating branch that you choose at submission time. The API creates a dedicated worktree for the run, starts one agent inside that boundary, and records every event in a durable SQLite log. The web UI and MCP server stream the same ordered events over server-sent events, so you can watch messages, tool calls, and tool outcomes as they happen. When the agent stops, Agentweaver commits the worktree state, asks for human review, and merges only after you approve. If the run fails, hits a bound, or the merge conflicts, the originating branch stays unchanged.
