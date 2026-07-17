---
title: The Assistant and Sessions
---

# The Assistant and Sessions

The **Assistant** is a conversational chat surface for Agentweaver itself — instead of filling out forms to submit runs, browse the backlog, or check on a coordinator orchestration, you talk to it in plain language and it drives the same MCP tools a connected external agent would use.

A **Session** is one Assistant conversation. Sessions live at the top level of the app, alongside Projects — they are not scoped to any single project, because a conversation might touch several projects (or none) as it goes.

## Why chat instead of forms?

Most of Agentweaver's surface area — submitting runs, managing the backlog, reviewing a coordinator's work plan, checking cluster health — is also exposed as MCP tools (see [MCP CLI & tools](./mcp-cli)). The Assistant is what happens when you point that same tool surface at a chat model running *inside* Agentweaver: ask "what's blocked on the board right now?" or "kick off a run to fix the flaky auth test and let me know when it needs review," and the assistant calls `backlog_*` / `run_submit` / `run_status` on your behalf and reports back.

::: tip Same tools, no separate MCP client needed
Anything the Assistant does is something an external MCP client (Claude Desktop, VS Code, your own script) could also do against the same server. The Assistant just gives you that capability without leaving the browser.
:::

## Starting a session

Open **Sessions** in the left nav (it's a collapsible top-level section, next to Projects) and click **New Session**. Type your first message — the assistant responds using whichever MCP tools are relevant, and the conversation becomes an entry in your session list.

## Resuming a session

Sessions persist. Close the tab, come back a day later, or get routed to a different API replica behind the load balancer — reopening a session from the list picks the conversation back up with its full history intact.

Under the hood this works by durably replaying the conversation's persisted message history rather than depending on any single process keeping the conversation in memory (see [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime) for the mechanism). Practically, this means:

- A session **idle for 30 minutes** is marked completed automatically, but sending a new message to it resumes it — nothing is lost.
- You can have up to **3 sessions actively in progress** at once; resuming an existing one never counts against that limit.

## Deleting a session

Each row in the Sessions list has a delete action. Deleting removes the run record and its persisted transcript — this cannot be undone, so a confirmation dialog appears first.

## See also

- [Sessions & the Assistant — User Guide](/experience/assistant-sessions) — the UI walkthrough
- [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime) — durable rehydration, MCP tool access, and sandboxing
- [API reference — Assistant endpoints](/reference/api#assistant-endpoints) — `POST /api/assistant/runs` and related endpoints
