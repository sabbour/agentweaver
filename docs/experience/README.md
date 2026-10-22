# Experience guide

Use this guide to learn what you can do in the checked-in Agentweaver v1 web
app and which owner requests each page sends. An **owner** is the service that
validates an operation and stores its authoritative state.

This guide describes source behavior, not service availability. Some referenced
services are source-only or unpublished candidates. A route in this guide does
not prove that a deployment exposes it. Start with the
[experience overview](./00-overview.md) for the app's routes, access model, and
implementation limits.

## Journeys

| Journey | Use this page to |
| --- | --- |
| [Sign-in and access](./onboarding-auth.md) | Sign in through the Identity Broker and request access to a run. |
| [Projects](./projects.md) | Browse and create projects, read the Dashboard, and open a run you opened before. |
| [Project configuration](./configuration.md) | Edit project settings by section and manage members. |
| [Skills and marketplace](./skills.md) | On the **Skills** page, configure public sources, preview and import skills, and assign a revision to agents. |
| [Account connections](./connections.md) | Connect GitHub Repo App or GitHub Copilot and pin a repository to an existing run. |
| [Runs and sessions](./runs-sessions.md) | Use the run tabs, send messages, respond to requests, and read usage. |
| [Memories](./knowledge.md) | Search and manage memories for a run and an agent. |

## Related documentation

- [Guides](../guide/index.md): setup, operation, and validation.
- [HTTP API](../reference/api.md): Gateway and native service routes.
- [Events](../reference/events.md): journal payloads, ordering, and outbox records.
- [Web app](../reference/web.md): active source tree, routes, and configuration.
- [MCP tools](../reference/mcp-tools.md): tool discovery, arguments, and owner results.
