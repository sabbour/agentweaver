---
title: Browser console
---

# Browser console

> ⚠️ **Removed from the UI (#346).** The "Operator dock" (LeftNav/top-bar trigger →
> `BrowserConsole` sidebar) described below has been deleted from the web app. The
> MCP-driven Sessions/Assistant page (`SessionsPage.tsx` / `AssistantRunPage.tsx`,
> reachable via the Sessions nav item or `/assistant`) is now the sole chat entry
> point. The backend `/api/console/turn` facade endpoint still exists (no other
> caller was found), but nothing in the web app calls it anymore. The content below
> is kept for historical/API reference only.

The browser console is a singleton app-level operator dock. It combines a terminal-styled web shell with a backend conversational facade that can answer status questions, inspect Agentweaver state with safe read-only tools, and return explicit gates instead of bypassing review or destructive controls. For the API contract see the [reference](../reference/browser-console.md); for internals see the [deep dive](../deep-dive/browser-console.md).

## When it is available

Open it with the **Console** button in the top bar. The console is mounted once in the app shell through `ConsolePanelContext`, so its transcript and bound-run state persist while navigating (`apps/web/src/components/shell/ConsolePanelContext.tsx:1`). The obsolete `/console` URL opens the same singleton panel and redirects to `/overview` (`ConsoleRouteRedirect.tsx:1`).

## What the console knows from the route

The shell derives context from the current URL (`apps/web/src/console/BrowserConsole.tsx:295`):

- on an orchestration page, it binds both `project_id` and `run_id`;
- on a project page, it binds `project_id`;
- elsewhere, it stays global unless you pin a project with `/use` or bind a run with `/monitor`.

The context rail shows the current scope, project, run, and whether the binding came from the route or a console command (`BrowserConsole.tsx:684`).


![Agentweaver Console panel with context badges, shortcut buttons, and the message-first operator dock](/screenshots/browser-console.png)

> 📸 **Screenshot — `browser-console.png`**
> *Shows:* the **Agentweaver Console** singleton panel opened from the top bar, with scope/project/run context badges, `/help` / `/projects` / `/runs` / `/clear` shortcuts, the scrollback log, and the bottom prompt.
> *Path:* Sign in → navigate to `/overview` → click **Console** in the top bar.

## Step by step

1. **Open Console.** The panel title is **Agentweaver Console**, with shortcuts for `/help`, `/projects`, `/runs`, and `/clear` (`BrowserConsole.tsx:679`, `:692`).
2. **Ask in natural language.** The browser sends `message`, `text`, `conversation_id`, and route context to `POST /api/console/turn` (`BrowserConsole.tsx:606`; `apps/web/src/api/client.ts:463`).
3. **Read the response.** Normal answers render as assistant output. Tool-backed responses show action summaries; links open the related project or orchestration (`BrowserConsole.tsx:361`, `:642`, `:656`).
4. **Clarify when asked.** If the backend cannot determine the project, run, or target, the response renders as **Clarification needed** with suggested commands (`BrowserConsole.tsx:703`).
5. **Respect gates.** Requests to confirm, approve, merge, review, delete, stop, or cancel come back as a gate block. Use the explicit run view or gate-specific UI to proceed; free-form console text does not bypass those controls (`apps/Agentweaver.Api/Console/ConsoleTurnService.cs:315`, `:354`).

## What natural language can do today

The facade agent is deliberately read-only/status-oriented. It can list projects, inspect a project, list runs, read a backlog board, get run status, read a coordinator work plan, list coordinator children, build an orchestration topology snapshot, list blueprints, list catalog roles, list workflows, list decisions or the decision inbox, and list memory (`packages/Agentweaver.AgentRuntime/ConsoleFacadeAgent.cs:269`).

If a run is bound and your message is not a status request, the service treats it as coordinator steering (`SteeringKind.Send`) and tells you to watch the run stream for the response (`apps/Agentweaver.Api/Console/ConsoleTurnService.cs:278`).

## Slash shortcuts

Slash commands remain available as fast local shortcuts. Use `/help` to see the palette. Common commands include `/projects`, `/use <name or id>`, `/backlog`, `/runs`, `/orchestrate <goal>`, `/monitor <runId>`, `/confirm`, `/revise <feedback>`, and `/approve-assembly` (`apps/web/src/console/BrowserConsole.tsx:332`, `:456`).

## Error behavior

The API returns provider-specific errors when the facade cannot start or call GitHub Copilot. Authorization maps to `401`, rate limiting to `429`, and provider/configuration failures to `503` (`apps/Agentweaver.Api/Endpoints/ConsoleEndpoints.cs:48`, `:70`). The web error formatter turns API, network, conflict, and rate-limit failures into operator-readable messages (`apps/web/src/api/errors.ts:34`).

## Source

| Concern | Source |
| --- | --- |
| Singleton console context | `apps/web/src/components/shell/ConsolePanelContext.tsx` |
| `/console` redirect | `apps/web/src/components/shell/ConsoleRouteRedirect.tsx` |
| Terminal shell, context, submit, rendering | `apps/web/src/console/BrowserConsole.tsx` |
| API client method | `apps/web/src/api/client.ts:463` |
| Endpoints | `apps/Agentweaver.Api/Endpoints/ConsoleEndpoints.cs` |
| Routing and gate behavior | `apps/Agentweaver.Api/Console/ConsoleTurnService.cs` |
| Facade agent and safe tools | `packages/Agentweaver.AgentRuntime/ConsoleFacadeAgent.cs` |
| Provider error classifier | `packages/Agentweaver.AgentRuntime/Providers/AgentProviderException.cs` |

## Related reading

- [Browser console — Reference](../reference/browser-console.md)
- [Browser console — Deep Dive](../deep-dive/browser-console.md)
- [Coordinator & orchestration](./coordinator-orchestration.md)
- [Runs board & watch](./runs-board-watch.md)
