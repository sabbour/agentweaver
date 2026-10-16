# Experience guide

This section describes what you see and do in the v1 web app. Each journey maps
the controls to the backend requests that handle them.

The pages describe the checked-in implementation. They do not establish that a
deployment is available. For setup and validation, start with the
[guides](../guide/index.md).

## Start here

Read the [experience overview](./00-overview.md) for navigation, access, and the
relationship between the web app, Gateway, and MCP tools.

## Journeys

| Journey | What it covers |
| --- | --- |
| [Sign-in and access](./onboarding-auth.md) | Broker sign-in, consent, run-bound access, and clearing the browser session. |
| [Projects](./projects.md) | List and create projects, inspect project state, and open an existing run. |
| [Project configuration](./configuration.md) | Read and append configuration revisions. |
| [Skills and marketplace](./skills.md) | Manage public sources, preview and import skills, and assign them to cast agents. |
| [Account connections](./connections.md) | Connect the GitHub Repo App and GitHub Copilot; pin a repository for a run. |
| [Runs and sessions](./runs-sessions.md) | Inspect topology, send messages, answer gates, follow events, and read usage. |
| [Knowledge](./knowledge.md) | Search, create, correct, review, restore, and transfer agent records. |

## Reference

- [HTTP API](../reference/api.md): Gateway and native service routes.
- [Events](../reference/events.md): journal payloads, ordering, and outbox records.
- [Web app](../reference/web.md): active source tree, routes, and configuration.
- [MCP tools](../reference/mcp-tools.md): discovery, arguments, and owner results.
