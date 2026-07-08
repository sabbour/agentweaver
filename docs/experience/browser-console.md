---
title: Browser console
---

# Browser console

The browser console is a terminal-styled app-level slide-in for operating Agentweaver from the web
app. Open it with the **Console** button in the top bar. The console is mounted once in the app shell,
so its transcript and bound-run state persist while navigating between pages. The obsolete `/console`
URL safely redirects to the Overview page and opens the same singleton panel.

## Terminal interface

The console renders as a true terminal, not a chat panel. It uses a dark terminal surface with a
monospace font and a full-height scrollback region that fills the viewport, so long sessions read
like a shell log. Input sits at the bottom on a fixed CLI prompt row, prefixed by a prompt glyph and
trailed by a blinking block cursor. The prompt input itself is borderless and transparent so it reads
as part of the terminal line rather than a form field. New output appends to the scrollback and the
view follows the latest line while you stay near the bottom.

## Prose and commands

The console has two input modes:

- Type normal prose to talk to the real coordinator. When a run is bound, prose is sent to that
  coordinator run. When no run is bound, the console asks before starting work.
- Type slash commands for an explicit MCP-backed control plane. Use `/help` in the console to see the
  palette.

Common commands:

| Command | Purpose |
| --- | --- |
| `/projects` | List projects. |
| `/use <name or id>` | Select the active project. |
| `/backlog` | List backlog and ready items. |
| `/ready <task>` | Move a backlog item to Ready. |
| `/runs` | List orchestration runs. |
| `/orchestrate <goal>` | Start a coordinator orchestration and bind the console to it. |
| `/monitor <runId>` | Bind to an existing run and stream updates. |
| `/confirm` and `/revise <feedback>` | Operate the OutcomeSpec gate for the bound run. |
| `/approve-assembly` | Approve the collective assembly review gate. |

## Gates stay visible

The console reuses the same run stream and timeline components as embedded run inspection. OutcomeSpec
confirmation, approvals, questions, review, and merge gates appear inline and are not bypassed by
prose or slash commands.

## Planned operator routing

The current console deliberately separates free-form coordinator prose from explicit slash commands.
A fuller type-anything operator agent that routes natural language across the whole MCP catalog is
planned in issue #201.

## Source

| Concern | Source |
| --- | --- |
| Singleton console panel | `apps/web/src/components/shell/AppShell.tsx` |
| `/console` redirect | `apps/web/src/components/shell/ConsoleRouteRedirect.tsx` |
| Top-bar Console button | `apps/web/src/components/shell/TopBar.tsx` |
| Terminal surface, scrollback, prompt, and blinking cursor | `apps/web/src/console/BrowserConsole.tsx:69`, `apps/web/src/console/BrowserConsole.tsx:180`, `apps/web/src/console/BrowserConsole.tsx:624` |
| Console architecture and gate reuse | `apps/web/src/console/BrowserConsole.tsx:32` |
| Slash command catalog | `apps/web/src/console/consoleCommands.ts:18` |

## See also

- [Coordinator & orchestration](./coordinator-orchestration.md)
- [Runs board & watch](./runs-board-watch.md)
- [MCP client](./mcp-client.md)
