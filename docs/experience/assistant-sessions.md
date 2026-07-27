# Sessions & the Assistant

Sessions are a global, top-level surface for conversational access to Agentweaver — a chat-driven alternative to filling out the run submission form, browsing the board, or checking a coordinator run's status by clicking through pages.

Scope: this page describes the Sessions list and the Assistant chat UI. For the MCP tool surface the assistant itself calls, see [MCP client — User Guide](./mcp-client.md); for the durable-rehydration mechanism that keeps a conversation resumable, see [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime).

## Where Sessions lives

**Sessions** is a top-level item in the left nav, at the same level as **Projects** — not nested under any single project, since one conversation can span multiple projects or none at all. It renders as a collapsible section:

- Click the **Sessions** header to expand/collapse the list of recent sessions inline in the nav.
- Click **New Session** to start a fresh conversation.
- Click **All sessions** to open the full Sessions page.

## The Sessions page

The Sessions page lists every conversation you've started, newest first. Each row shows:

- A title (derived from the conversation's opening message)
- When it was created
- A status indicator
- A **delete** icon

Clicking a row opens that conversation and resumes it exactly where you left off — full message history included, even if it's been idle for a while or the request happens to land on a different backend replica than the one that handled your last message.

## Chatting with the assistant

Type a message and send it. The assistant can, among other things:

- Look up and summarize the state of the project board (backlog, ready, active runs)
- Submit a new run with `run_submit`, then report back on its progress
- Answer questions about a run's status, files changed, or review state
- Walk through cluster diagnostics if something looks off

It's calling the same MCP tools an external client would use — see [Reference — MCP tools](/reference/mcp-tools) for the full catalog. Tool calls the assistant makes appear inline in the conversation so you can see what it actually did, not just what it says it did.

### Suggested prompts

Before you've sent a first message, the empty state shows a handful of suggested-prompt chips — realistic starting points like "List my projects and each one's most recent run status" or "Start a quick smoke-test run". Clicking one fills the composer with that text so you can review or edit it before sending; it does not send automatically. These are meant to help first-time users and anyone doing a quick smoke test get going without having to think of a prompt from scratch.

## Deleting a session

Hover a row and click the delete icon (or open the conversation and use the equivalent action). A confirmation dialog appears:

> **Delete this conversation?** This removes the session and its history. This cannot be undone.

Confirming calls the same generic run-delete endpoint used elsewhere in the product (`DELETE /api/runs/{id}`) — a session is stored as a run record under the hood, just one with `AgentName == "Operator"`.

## Resuming after a gap

If you come back to a session after it's gone idle (30 minutes of inactivity closes it automatically) or after a deploy has cycled the pod that was holding it in memory, sending a new message just works — the assistant's history is rebuilt from the persisted transcript before your message is processed. You won't see any difference in behavior; the only user-visible signal is that the conversation's status flips back from *completed* to *in progress*.

## See also

- [The Assistant and Sessions — Getting Started](/guide/assistant) — concepts and starting a session
- [Assistant runtime — Deep Dive](/deep-dive/assistant-runtime) — how rehydration and MCP tool sandboxing work
- [API reference — Assistant endpoints](/reference/api#assistant-endpoints) — endpoint details
