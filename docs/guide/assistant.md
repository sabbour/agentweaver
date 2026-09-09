---
title: The Assistant and Sessions
---

# The Assistant and Sessions

The **Assistant** is a conversational chat surface for Agentweaver itself — instead of filling out forms to submit runs, browse the backlog, or check on a coordinator orchestration, you talk to it in plain language and it drives the same MCP tools a connected external agent would use.

A **Session** is one Assistant conversation. Sessions live at the top level of the app, alongside Projects — they are not scoped to any single project, because a conversation might touch several projects (or none) as it goes.

## Why chat instead of forms?

Most of Agentweaver's surface area is also exposed as MCP tools. This includes backlog management, coordinator work plans, and cluster health. See [MCP CLI & tools](./mcp-cli). The Assistant calls `backlog_*`, `coordinator_start`, and `run_status` for you.

::: tip Same tools, no separate MCP client needed
Anything the Assistant does is something an external MCP client (Claude Desktop, VS Code, your own script) could also do against the same server. The Assistant just gives you that capability without leaving the browser.
:::

## Starting a session

Open **Sessions** in the left nav (it's a collapsible top-level section, next to Projects) and click **New Session**. Type your first message — the assistant responds using whichever MCP tools are relevant, and the conversation becomes an entry in your session list.
Your message remains visible while the new session connects and while persisted history is
reconciled. When the server copy arrives, Agentweaver replaces the pending presentation without
showing the message twice.
If live updates disconnect after a send succeeds, Agentweaver refreshes the durable
conversation history and reconnects automatically. A **Retry sync** action remains available
until the transcript can be reconciled.

The Assistant uses the same signed-in Agentweaver identity as the browser request. The API
validates that identity and current project access, then issues a five-minute Agentweaver broker
token for the exact MCP resource and sends it only to the per-turn Assistant runtime. The browser's
Entra bearer is never sent to MCP. Repository and Copilot capabilities use their respective GitHub
App authorizations, so you do not need to sign in again inside the conversation.

When Agentweaver is configured to run assistant turns in an **AgentHost pod**, start the
session from a project that has its GitHub Copilot App connected. The pod is created only after
Agentweaver captures a short-lived capability bound to that project and session; it never falls
back to a machine or ambient GitHub credential. If the connection is missing, the session stays
unstarted and the app offers the project connection action instead.

## Resuming a session

Sessions persist. Close the tab, come back a day later, or get routed to a different API replica behind the load balancer — reopening a session from the list picks the conversation back up with its full history intact.

Under the hood this works by durably replaying the conversation's persisted message history rather than depending on any single process keeping the conversation in memory (see [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime) for the mechanism). Practically, this means:

- A session **idle for 30 minutes** is marked completed automatically, but sending a new message to it resumes it — nothing is lost.
- You can have up to **3 sessions actively in progress** at once; resuming an existing one never counts against that limit.

## Deleting a session

Each row in the Sessions list has a delete action. Deleting removes the run record and its persisted transcript — this cannot be undone, so a confirmation dialog appears first.
Because sessions are personal rather than project-owned, you can delete your own session even if the project that was open when you started it has since been deleted or you no longer have access to that project.

## See also

- [Sessions & the Assistant — User Guide](/experience/assistant-sessions) — the UI walkthrough
- [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime) — durable rehydration, MCP tool access, and sandboxing
- [API reference — Assistant endpoints](/reference/api#assistant-endpoints) — `POST /api/assistant/runs` and related endpoints
