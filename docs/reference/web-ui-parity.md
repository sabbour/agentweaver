# Web UI parity and v1 adaptations

The v1 Web client reuses the 0.x visual language and supported interactions.
It does not restore the old monolith's API or infer missing service data.
This inventory supports [#1859](https://github.com/sabbour/agentweaver/issues/1859),
[#2120](https://github.com/sabbour/agentweaver/issues/2120) check 10, and the
authorized page provider tracked in
[#2130](https://github.com/sabbour/agentweaver/issues/2130).

The visual baseline is `dev` commit
`108d1a067f1b58da870ca98db1c65a0e9b37a35f`.
The pre-adaptation v1 baseline is
`9a28e8868492b41ae799234dc0c25cffd95c52ba`.
Compare mounted routes, not just retained source files: v1 previously
bypassed most incumbent pages with a separate route tree.

## Retained surfaces

| Surface or route | Retained from 0.x | Required v1 adaptation |
| --- | --- | --- |
| Sign-in | Brand, centered card, typography, buttons, errors | Broker sign-in, memory-only tokens, reload restoration. |
| Consent | Sign-in card styling and native controls | Permission explanations and existing Broker choice handling. |
| Shell | Collapsible rail, project switcher, settings menu, grouped navigation, persona footer, floating content panel | Slots supply Gateway projects and Broker session controls without mounting legacy data consumers. Only API-backed destinations are visible. |
| `/projects` | Page header, responsive gallery tiles, modal creation | Name-only creation and configuration revisions. No automatic Owner grant. Role names remain unavailable. |
| `/projects/:id` | Dashboard page and section primitives | Current project metadata and exact-run entry instead of invented aggregate metrics. |
| `/projects/:id/orchestrations` | Page, section, and form patterns | Exact-run opener; no fabricated run list or start action. |
| `/projects/:id/orchestrations/:runId` | Old bookmarks | Redirect preserves the selected view and other query parameters. |
| `/projects/:id/runs/:runId` | Page/section primitives and existing workflow topology nodes | Owner session tree, blockers, pending decisions, exact gate responses, live journal, immutable selection and measured usage. |
| `/projects/:id/team` | Roster tiles | Revisioned agent charters and current cast. |
| `/projects/:id/skills` | Dedicated destination and native controls | Marketplace source revisions, preview/import, inventory, and fenced assignment. |
| `/projects/:id/memories` | Dedicated destination and shared section/error patterns | Exact run/agent scope, trust state, revisions, supported record mutations and transfer. |
| `/projects/:id/knowledge` | Compatibility URL | Opens the same Memories view. |
| `/projects/:id/team/:agentName/memory` | Existing deep links | Seeds the agent field; the display name never grants authority. The service validates exact scope. |
| `/projects/:id/settings` | Section rail, readable form sections, advanced controls | Default and per-task models, reasoning effort, saved providers, immutable revision reads and complete-document saves. Changing the default model clears its credential until you enter a new one. JSON is an advanced fallback. |
| Settings > Connections | Settings layout and connection controls | Existing GitHub and Copilot flows plus exact-ID Remote MCP registration, status, authorization, refresh and disconnect. |
| `/settings` | Account settings entry | Broker session controls and GitHub account status. Project-specific connections remain in project settings. |

## Unavailable destinations

These routes use one shared **Not available in this version** component with
a specific reason and a return link. They do not appear in navigation.

| Old route | Missing API or construct |
| --- | --- |
| `/`, `/overview` | `/` opens Projects. The old Overview address explains the missing cross-project activity aggregate. |
| `/sessions`, `/projects/:id/sessions` | Cross-project session search. The project sessions address opens the same global explanation. |
| `/assistant`, `/console` | Public assistant-root start flow. Console retains its Assistant compatibility redirect. |
| `/platform-settings` | Browser platform administration. |
| `/projects/:id/board` | Project board data. |
| `/projects/:id/flow` | Project-wide agent activity aggregate. |
| `/projects/:id/workspace` | Pinned run-artifact file browser; see #1917. No host filesystem proxy is added. |
| `/projects/:id/team/cast` | Interactive casting proposal UI. Current charters and cast remain visible in Agents. |
| `/projects/:id/workflows` | An API to edit workflows. |
| `/observability/*`, `/projects/:id/observability/*` | Trace/agent aggregation. Existing run Activity and Usage remain available. |
| `/projects/:id/diagnostics` | Project diagnostics data. |
| `/projects/:id/heartbeat` | Project heartbeat data. |
| `/projects/:id/cluster` | Browser cluster administration. |
| Unknown addresses | A separate Page not found state, not a silent Projects redirect. |

## Current API gaps

- [Effective role names](https://github.com/sabbour/agentweaver/issues/2131):
  summaries and authorization context do not identify TenantAdmin, Owner or
  Orchestrator roles. The UI shows **Role unavailable**, not an inferred role.
- [Selectable models and providers](https://github.com/sabbour/agentweaver/issues/2132):
  saved references are editable, but the Gateway does not expose an eligible
  catalog. The service still validates all writes.
- [Remote MCP discovery and checksums](https://github.com/sabbour/agentweaver/issues/2135):
  exact-ID lifecycle routes exist, but connection discovery and the owner-held
  setup checksum do not. Setup uses explicit metadata, not assumed canonical bytes.

Exact configuration revision reads, per-task models, and reasoning effort
already exist in owner contracts. They are wired by this adaptation, not
treated as missing APIs.

## Evidence boundary

Local screenshots use fixture data and isolated source snapshots. They compare
0.x, pre-adaptation v1, and adapted v1 without live sign-in or real credentials.
They do not prove deployed identity, authorization, live providers, or complete
P1 acceptance. This source change is part of #1859, not closure of its broader
live acceptance criteria. Follow [Use the v1 web client](../guide/web-client.md)
for actual routes, settings, and current limits.
