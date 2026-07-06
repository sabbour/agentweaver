---
title: Browser console
---

# Browser console

The browser console is a terminal-styled, full-screen chat interface for operating Agentweaver from
the web app. Open it with the **Console** button in the top bar or navigate to `/console`.

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

The console reuses the same run stream and timeline components as the run pages. OutcomeSpec
confirmation, approvals, questions, review, and merge gates appear inline and are not bypassed by
prose or slash commands.

## Planned operator routing

The current console deliberately separates free-form coordinator prose from explicit slash commands.
A fuller type-anything operator agent that routes natural language across the whole MCP catalog is
planned in issue #201.

## Source

| Concern | Source |
| --- | --- |
| `/console` route | `apps/web/src/App.tsx:47` |
| Top-bar Console button | `apps/web/src/components/shell/TopBar.tsx:70` |
| Console architecture and gate reuse | `apps/web/src/console/BrowserConsole.tsx:32` |
| Slash command catalog | `apps/web/src/console/consoleCommands.ts:18` |

## See also

- [Coordinator & orchestration](./coordinator-orchestration.md)
- [Runs board & watch](./runs-board-watch.md)
- [MCP client](./mcp-client.md)
