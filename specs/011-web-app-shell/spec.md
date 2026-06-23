# Feature Specification: Web App Shell & Project Navigation

**Feature Branch**: `011-web-app-shell`

**Created**: 2026-06-22

**Status**: Draft

**Input**: User description: "Restructure the Agentweaver web app's information architecture / navigation shell to match the Squadboard app pattern: a persistent left sidebar with grouped nav sections (WORK / SQUAD / OPERATIONS / SYSTEM), a top bar with a project switcher, and the existing pages wired into the new nav. First iteration: the app shell + persistent left nav with sections; the top bar with a project switcher; wiring the existing pages into the new nav. Include Flow (the YAML workflows from feature 010), Diagnostics, and Heartbeat. Defer Docs, Inbox, Consult, the Settings sub-nav redesign, the Projects-landing redesign, and the Skills / Tools / MCP Servers / Ceremonies / Templates / Costs / Now / Apps destinations."

## Overview

Today the Agentweaver web app has no information architecture to speak of. Its shell (`apps/web/src/App.tsx`) is a single thin top header — a brand logo plus the GitHub sign-in control — above a flat set of routes (`apps/web/src/App.tsx:70-98`). A user who opens a project (`/projects/:projectId`, the board homepage) has no persistent way to reach that same project's Team, Settings, or Memories; those destinations exist only as routes (`/projects/:projectId/settings`, `/projects/:projectId/team`, `/projects/:projectId/memories`) with no navigation surface that exposes them. There is also no way to switch between projects without returning to the gallery at `/`.

This feature introduces a **navigation shell**: a persistent **left sidebar** whose items are organized into labelled sections, and a **top bar** that carries a **project switcher** so the current project is always visible and changeable in place. It wires the project's **already-existing pages** into that shell so they become reachable, discoverable destinations rather than orphaned routes. The sibling app **Squadboard** (`C:\Users\asabbour\Git\squadboard\packages\client\src\components\Layout.tsx`) is used purely as a layout / IA reference for the section grouping (WORK / SQUAD / OPERATIONS / SYSTEM), the bottom-anchored SYSTEM group, the project-switcher combobox in the top bar, and the health status dot. No Squadboard product code, route, or feature is copied; only destinations that map to **real, existing Agentweaver features** (or the explicitly-included new SYSTEM items) appear in the nav.

This first iteration deliberately **adapts** the Squadboard pattern to Agentweaver's actual domain rather than reproducing it pixel-for-pixel. Destinations Agentweaver does not have (Skills, Tools, MCP Servers, Ceremonies, Templates, Costs, Now, Apps) are **not** invented as placeholder pages. The top bar gains the project switcher but **not** the Docs / Inbox / Consult buttons. The Settings sub-nav redesign and the Projects-landing redesign are out of scope here and called out as future work.

Consistent with the constitution, navigation and IA are a **Web-presentation** concern: the shell itself holds no business logic (Principle III). Any data the shell newly surfaces — the project list for the switcher, the health/status dot, and the SYSTEM section's Diagnostics and Heartbeat views — MUST be served by **real API endpoints**, never mocked or stubbed (Principles III, VII). Where a nav destination exposes data or actions, that capability MUST remain reachable from the MCP server at parity with the Web UI (Principle IV). No emojis appear in any shipped shell surface (Principle VIII).

## Clarifications

### Session 2026-06-22

- Q (C1): Does Memories sit under the SQUAD section (alongside Agents) or under OPERATIONS? → A: Under **SQUAD**, next to Agents. (FR-002, FR-009)
- Q (C2): Does this iteration build the Diagnostics and Heartbeat API endpoints and pages, or show the SYSTEM nav items as disabled placeholders until later? → A: **Build them this iteration** — a read-only Diagnostics endpoint and a Heartbeat status endpoint (surfacing the existing `CoordinatorHeartbeatService`: last tick, schedule/interval, active/idle, and pickup config/counts as appropriate) plus their pages. All logic stays server-side (API-first); the pages are thin clients; the same diagnostics and heartbeat status MUST be reachable via the MCP server at parity (Principles III, IV); no mocks/fakes (Principle VII). (FR-016, FR-016a, FR-017, FR-017a, FR-018)
- Q (C3): What does the top-bar health/status dot represent — API reachable, heartbeat liveness, or both? → A: **API reachable only** (green when the API responds, red/grey when not). It does NOT represent heartbeat liveness. It is backed by a lightweight health check; the existing `GET /` ("Agentweaver API") response is acceptable, or a minimal `/api/health` endpoint. (FR-013)
- Q (C4): Do the deep run/execution pages (`WatchPage`, `WorkflowRunPage`, `CoordinatorRunPage`, `CastingWizardPage`) render inside the shell with the left nav, or as full-screen flows? → A: **Inside the shell** — the left nav stays visible; they are not full-screen flows. (FR-006, FR-018 context)
- Q (C5): Does the top-bar project switcher only switch among existing projects, or also offer inline "Add Project" / "Create from template"? → A: **Switch-only** — it lists and selects existing projects to navigate between them; it does NOT offer inline Add Project / Create from template (those remain on the Projects landing page). (FR-011)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Navigate a project's destinations from a persistent left sidebar (Priority: P1)

A signed-in user opens a project. Alongside the project content they see a persistent **left sidebar** whose items are grouped into labelled sections. The sections collect the project's real destinations: the **Board** (the project homepage) and **Flow** (the project's workflows) under a work group; the **Agents** roster (today's Team page) and **Memories** under a squad group; **Settings** under an operations group; and **Diagnostics** and **Heartbeat** under a system group anchored to the bottom of the sidebar. From any project page, the user can reach any other project destination in one click, and the sidebar makes clear which destination is currently active.

**Why this priority**: This is the core value of the feature — turning orphaned routes into discoverable, always-available destinations. Without the persistent sidebar there is no information architecture at all; every other story builds on it. It is the smallest slice that delivers standalone value: existing pages become reachable from a consistent shell.

**Independent Test**: Open a project, confirm a persistent left sidebar renders with the grouped sections, and verify each item navigates to its corresponding existing page (Board, Flow, Agents, Memories, Settings) and that the active item is visually indicated. Diagnostics and Heartbeat appear in a bottom-anchored SYSTEM group.

**Acceptance Scenarios**:

1. **Given** an open project, **When** the app shell renders, **Then** a persistent left sidebar is shown with labelled sections grouping the project's destinations, and it remains visible as the user moves between those destinations.
2. **Given** the sidebar, **When** the user clicks Board, **Then** the project board homepage (`ProjectPage`) is shown and Board is marked active.
3. **Given** the sidebar, **When** the user clicks the Agents item, **Then** the existing Team page (`TeamPage`) is shown, regardless of the item being labelled "Agents" rather than "Team".
4. **Given** the sidebar, **When** the user clicks Settings, **Then** the existing project Settings page (`ProjectSettingsPage`) is shown.
5. **Given** the sidebar, **When** the user clicks Memories, **Then** the existing Memories page (`MemoriesPage`) is shown.
6. **Given** any project destination is open, **When** the user reads the sidebar, **Then** exactly one item is indicated as active and it corresponds to the current destination.

---

### User Story 2 - Switch the active project from the top bar (Priority: P1)

While working inside one project, a user wants to move to a different project without going back to a gallery. The **top bar** shows the **current project's name** as a switcher control. The user opens it, sees the list of available projects (with recently-used ones surfaced first), picks another, and the app navigates to the corresponding destination in that project, keeping the shell in place.

**Why this priority**: Project switching is the second half of a usable shell. With the sidebar (US1) a user can move *within* a project; the switcher lets them move *between* projects without losing the shell. Together US1 and US2 are the minimum viable navigation experience.

**Independent Test**: From inside a project, open the top-bar switcher, confirm it lists available projects with the current one shown, select a different project, and verify the app navigates into that project with the shell intact and the sidebar now scoped to the newly-selected project.

**Acceptance Scenarios**:

1. **Given** an open project, **When** the top bar renders, **Then** it displays the current project's name as the switcher control.
2. **Given** the switcher is opened, **When** the project list loads, **Then** it shows the available projects, served by a real API endpoint, with recently-used projects surfaced first.
3. **Given** the open switcher, **When** the user selects a different project, **Then** the app navigates into that project and the sidebar and shell update to reflect the new project scope.
4. **Given** the switcher list, **When** the user types to filter, **Then** the list narrows to matching project names.
5. **Given** no project is currently selected (e.g., at the app root), **When** the top bar renders, **Then** the switcher indicates that no project is selected and still allows choosing one.

---

### User Story 3 - Reach the project's workflows via "Flow" (Priority: P2)

A user wants to see and manage the project's run workflows. From the WORK group in the sidebar they click **Flow** and land on the project's workflows management surface (defined by Feature 010), where workflows discovered from `.scaffolders/workflows/` are listed with their validation status and a Sync action is available. This story wires the navigation entry; the workflows page content and behavior are owned by Feature 010 (FR-039) and are not redefined here.

**Why this priority**: Flow is an explicitly-included destination, but unlike Board/Agents/Settings/Memories its target page is delivered by Feature 010 rather than already shipping. It is P2 because the shell (US1/US2) must exist first, and because the linkage degrades gracefully if the workflows page is not yet present.

**Independent Test**: Click Flow in the WORK group and verify it routes to the project's workflows management page (Feature 010). If that page is not yet available in the build, the item is present and clearly indicated as not-yet-available rather than navigating to a broken route.

**Acceptance Scenarios**:

1. **Given** a project whose workflows page (Feature 010) is available, **When** the user clicks Flow, **Then** the project's workflows management page is shown, scoped to that project.
2. **Given** the workflows page is not present in the current build, **When** the user views the Flow item, **Then** it is shown as a known-but-unavailable destination rather than a link to a broken or empty route.
3. **Given** the Flow destination, **When** it renders, **Then** it does not redefine workflow internals; it only surfaces the Feature 010 workflows page.

---

### Edge Cases

- **No projects exist**: The top-bar switcher renders with an empty list and a clear indication that there are no projects, without erroring; the shell still loads.
- **Project list fails to load**: If the API call backing the switcher fails, the top bar surfaces a clear, non-blocking error state and the rest of the shell remains usable.
- **Direct deep link to a project destination**: Opening a deep URL (e.g., `/projects/:projectId/settings`) directly MUST render the full shell with the correct sidebar item active, not a bare page without navigation.
- **Unknown or non-project route within the shell**: When the current route does not map to a known project destination, the sidebar MUST fall back to a sensible active state (e.g., the Board home) rather than showing no active item or a broken one.
- **SYSTEM destinations**: The Diagnostics and Heartbeat destinations are built this iteration with real read-only API endpoints (FR-016, FR-017), so both are live links. If any future build lacks a backing endpoint, the corresponding item MUST be shown as unavailable/disabled rather than navigating to a page that fabricates data (Principle VII).
- **Narrow viewport**: On a narrow viewport the sidebar MUST remain usable (e.g., collapsible to icons) without losing access to any destination; precise responsive behavior is a presentation detail, not a parity requirement.
- **Deferred top-bar items**: Docs, Inbox, and Consult MUST NOT appear in this iteration's top bar; their absence MUST NOT leave dead controls or broken layout.

## Requirements *(mandatory)*

### Functional Requirements

**App shell & left navigation**

- **FR-001**: The web app MUST present a persistent navigation shell consisting of a left sidebar and a top bar that frame the main content area, replacing the current flat header-only shell (`apps/web/src/App.tsx`).
- **FR-002**: The left sidebar MUST organize project destinations into labelled sections. The adapted Agentweaver grouping is: **WORK** (Board, Flow), **SQUAD** (Agents, Memories), **OPERATIONS** (Settings), and **SYSTEM** (Diagnostics, Heartbeat).
- **FR-003**: The SYSTEM section MUST be visually anchored to the bottom of the sidebar, separated from the project-scoped sections above it (modeled on the Squadboard layout reference).
- **FR-004**: The sidebar MUST indicate which single destination is currently active, derived from the current route, and MUST update that indication as the user navigates.
- **FR-005**: Each sidebar item MUST navigate to a **real, existing Agentweaver page** or an explicitly-included new SYSTEM destination; the iteration MUST NOT introduce placeholder pages for destinations Agentweaver does not have (Principle VII).
- **FR-006**: The shell MUST render for all project-scoped destinations, including when reached by direct deep link, so navigation is never lost on a project page. The deep run/execution pages (`WatchPage`, `WorkflowRunPage`, `CoordinatorRunPage`, `CastingWizardPage`) MUST render **inside** the shell with the project left-nav visible; they MUST NOT be full-screen flows that hide the navigation.

**Wiring existing pages into the nav**

- **FR-007**: The **Board** item (WORK) MUST route to the existing project board homepage (`ProjectPage`, `/projects/:projectId`).
- **FR-008**: The **Agents** item (SQUAD) MUST route to the existing Team page (`TeamPage`, `/projects/:projectId/team`); the nav label MUST be adapted to "Agents" while reusing the existing page unchanged (label adaptation only, no new page).
- **FR-009**: The **Memories** item MUST be placed in the **SQUAD** section, next to Agents, and MUST route to the existing Memories page (`MemoriesPage`, `/projects/:projectId/memories`).
- **FR-010**: The **Settings** item (OPERATIONS) MUST route to the existing project Settings page (`ProjectSettingsPage`, `/projects/:projectId/settings`); the Settings sub-nav redesign is out of scope for this iteration (see Out of Scope).

**Top bar & project switcher**

- **FR-011**: The top bar MUST include a **switch-only project switcher** that displays the current project's name and lets the user change the active project in place, without first returning to the project gallery. The switcher MUST only list and select existing projects to navigate between them; it MUST NOT offer inline "Add Project" or "Create from template" affordances (project creation remains on the Projects landing page).
- **FR-012**: The project switcher's list MUST be populated from a **real API endpoint** that returns the available projects, with no business logic in the client (Principles III, IV); recently-used projects SHOULD be surfaced first and the list SHOULD be filterable by typing.
- **FR-013**: The top bar MUST display a health/status indicator (status dot) that represents **API reachability only**: green when the API responds, red/grey when it does not. The indicator MUST NOT represent coordinator heartbeat liveness. It MUST be backed by a lightweight health check — either the existing root endpoint (`GET /` returning "Agentweaver API", `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:30`) or a minimal `/api/health` endpoint — and MUST NOT be a hardcoded value.
- **FR-014**: Selecting a different project in the switcher MUST navigate into that project and re-scope the sidebar and shell to it, preserving the shell.
- **FR-015**: The top bar MUST retain the existing brand mark and GitHub sign-in control, and MUST NOT add the Docs, Inbox, or Consult controls in this iteration.

**SYSTEM section (Diagnostics & Heartbeat)**

- **FR-016**: The sidebar MUST include a **Diagnostics** destination in the SYSTEM section, and this iteration MUST build it: a read-only Diagnostics **page** backed by a real, read-only Diagnostics **API endpoint**. All diagnostics logic MUST live server-side (API-first); the page is a thin client over the endpoint (Principle III). The page MUST NOT fabricate, mock, or stub any data (Principle VII).
- **FR-016a**: The Diagnostics data exposed by the read-only Diagnostics API endpoint MUST be reachable from the **MCP server at parity** with the Web UI, so an MCP-capable client can read the same diagnostics (Principle IV).
- **FR-017**: The sidebar MUST include a **Heartbeat** destination in the SYSTEM section, and this iteration MUST build it: a read-only Heartbeat status **page** backed by a real, read-only Heartbeat status **API endpoint** that surfaces the existing coordinator heartbeat (`CoordinatorHeartbeatService`, `apps/Agentweaver.Api/Coordinator/CoordinatorHeartbeatService.cs`). The endpoint MUST expose the heartbeat's observable state — at minimum the last tick time, the schedule/interval, whether it is active or idle, and pickup configuration/counts as appropriate (e.g., `MaxReadyPerHeartbeat`). All logic MUST stay server-side; the page is a thin client (Principle III); no mocks/fakes (Principle VII).
- **FR-017a**: The Heartbeat status exposed by the read-only Heartbeat API endpoint MUST be reachable from the **MCP server at parity** with the Web UI, so an MCP-capable client can read the same heartbeat status (Principle IV).
- **FR-018**: Because the Diagnostics and Heartbeat endpoints and pages are built in this iteration (FR-016, FR-017), both SYSTEM destinations MUST be live, functional links rather than disabled placeholders. Should any nav destination ever lack a backing endpoint in a given build, its sidebar item MUST be shown as unavailable/disabled rather than navigating to a page that fabricates data (Principle VII).

**Flow (Feature 010 linkage)**

- **FR-019**: The **Flow** item (WORK) MUST route to the project's workflows management page defined by Feature 010 (`specs/010-yaml-workflows-review-policies/spec.md`, FR-039); this feature MUST NOT redefine workflow loading, validation, sync, or execution.
- **FR-020**: If the Feature 010 workflows page is not present in the current build, the Flow item MUST be shown as a known-but-unavailable destination rather than linking to a broken route.

**Cross-cutting (parity, scope, presentation)**

- **FR-021**: The navigation shell MUST contain no business logic; it is a thin presentation layer over the API (Principle III). All data it surfaces (project list, health/status, diagnostics, heartbeat) MUST be served by the API.
- **FR-022**: For any nav destination that exposes data or actions newly surfaced by this feature (project list for switching, health/status, diagnostics, heartbeat), the underlying API capability MUST be reachable from the MCP server at parity with the Web UI (Principle IV). Pure web-presentation concerns (sidebar layout, active-item highlighting, collapse behavior) are Web-only and not subject to MCP parity.
- **FR-023**: No shipped shell surface (labels, tooltips, status text) MUST contain emojis (Principle VIII).
- **FR-024**: This feature MUST NOT alter the behavior of the existing pages it wires in (Board, Agents/Team, Settings, Memories); it only changes how they are reached and framed.

## Key Entities *(include if feature involves data)*

- **Navigation Shell**: The persistent web-app frame composed of a left sidebar and a top bar around a main content area. Web-presentation only; holds no business logic. Replaces the current header-only shell.
- **Nav Section**: A labelled grouping of sidebar items (WORK, SQUAD, OPERATIONS, SYSTEM). SYSTEM is bottom-anchored. Adapted from the Squadboard reference grouping.
- **Nav Item / Destination**: A single sidebar entry mapping a label and icon to a real Agentweaver route/page (or an explicitly-included new SYSTEM destination). Carries an availability state so unavailable destinations render as disabled rather than broken links.
- **Project Switcher**: The top-bar control showing the current project and offering the list of available projects (recent-first, filterable) to change the active project in place. Backed by a real projects API endpoint.
- **Active Project Scope**: The currently-selected project that scopes the sidebar's project destinations and the content area. Derived from the route and changeable via the switcher.
- **System Status Indicator**: The top-bar health/status dot representing **API reachability only** (green when the API responds, red/grey when not); it does not represent heartbeat liveness. Backed by a lightweight health check (`GET /` or a minimal `/api/health`).
- **Heartbeat Status**: The observable state of the existing `CoordinatorHeartbeatService` (last tick, schedule/interval, active/idle, pickup config/counts such as `MaxReadyPerHeartbeat`) surfaced by the Heartbeat destination via a new read-only API endpoint built this iteration and reachable from the MCP server at parity. The heartbeat mechanism itself is defined in Features 008/009 and not redefined here.
- **Diagnostics**: A read-only system diagnostics view backed by a new read-only API endpoint built this iteration, served identically to the Web UI and the MCP server (parity). Server-side logic only; the page is a thin client.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: From any project page, a user can reach any other destination in that same project (Board, Flow, Agents, Memories, Settings) in a single click via the sidebar, with the active destination clearly indicated.
- **SC-002**: A user can change the active project from the top bar without first navigating to the project gallery, and lands in the newly-selected project with the shell intact, in under 5 seconds.
- **SC-003**: 100% of sidebar destinations either navigate to a real, existing page or are clearly marked unavailable; zero destinations lead to placeholder pages that fabricate data (Principle VII).
- **SC-004**: Every project destination renders with the full shell (sidebar + top bar) when reached by direct deep link, with the correct sidebar item active — verified for each wired destination.
- **SC-005**: The project list, system status indicator, and any SYSTEM destination data are each served by a real API endpoint; none is hardcoded or mocked in the client.
- **SC-006**: For each nav destination that newly surfaces data or actions (project switching, health/status, diagnostics, heartbeat), the underlying capability is reachable from the MCP server as well as the Web UI (parity verified per capability).
- **SC-007**: No emoji appears anywhere in the shell's shipped surfaces.
- **SC-008**: The pre-existing pages (Board, Team/Agents, Settings, Memories) behave identically before and after being wired into the shell (no behavior regression).

## Assumptions

- The Squadboard app (`C:\Users\asabbour\Git\squadboard\packages\client\src\components\Layout.tsx`) is referenced **only** as a layout / IA pattern: its section grouping (WORK / SQUAD / OPERATIONS / SYSTEM), bottom-anchored SYSTEM group, top-bar project-switcher combobox (recent-first, filterable), and health status dot (`/api/health` returning `{ status, version, recoveryWarning }`). No Squadboard product code, routes, or features (Skills, Tools, MCP Servers, Ceremonies, Templates, Costs, Now, Apps, Docs, Inbox, Consult) are copied.
- The existing destinations to wire in already exist as pages and routes: `ProjectPage` (Board, `/projects/:projectId`), `TeamPage` (Agents, `/projects/:projectId/team`), `ProjectSettingsPage` (Settings, `/projects/:projectId/settings`), `MemoriesPage` (Memories, `/projects/:projectId/memories`) — see `apps/web/src/App.tsx:85-90`.
- "Flow" targets the workflows management page defined by Feature 010 (FR-039), which is specified but may not be implemented when this shell ships; the shell links to it and degrades gracefully if absent.
- Agentweaver has a running coordinator heartbeat (`CoordinatorHeartbeatService`) but **no** structured health/diagnostics/heartbeat API endpoint today (only `GET /` returning the plain string "Agentweaver API", `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:30`). This iteration therefore **builds** new read-only Diagnostics and Heartbeat API endpoints (and their pages), reachable from the MCP server at parity (resolved in Clarifications C2). The top-bar status dot represents API reachability only (C3), distinct from the Heartbeat destination which reports coordinator heartbeat state.
- The project gallery (`ProjectGalleryPage`, `/`) remains the app-root Projects landing for this iteration; its Squadboard-style redesign (search/filter/sort, project-template cards, `.squad` badges, select checkboxes) is deferred.
- The accountable-human / sign-in model is unchanged: the existing GitHub sign-in control is retained in the top bar and gates access (`AuthGate`, `apps/web/src/App.tsx:110-146`).
- Responsive / mobile layout specifics (e.g., sidebar collapse) are presentation details; the requirement is that every destination stays reachable, not a particular visual layout.

## Dependencies & Relationships

- **Feature 003 (Projects)**: Supplies the project model and the projects list API that backs the switcher.
- **Feature 006 (Memory & Decision Inbox)**: Supplies the Memories destination (`MemoriesPage`). The Squadboard "Inbox" concept (decision inbox) is deferred from the top bar this iteration.
- **Feature 005 (Agent Team Casting)**: Supplies the Team page (relabeled "Agents") and the Casting Wizard deep flow.
- **Feature 008 (Coordinator Agent) / Feature 009 (Backlog Kanban Board)**: Supply the board homepage and the coordinator heartbeat whose status the Heartbeat destination surfaces.
- **Feature 010 (YAML Workflows & Review Policies)**: Owns the Flow destination's target page (FR-039). This feature provides only the navigation entry.

## Out of Scope (Deferred to Future Iterations)

The following are explicitly **not** specified here and are noted as future work:

- **Top-bar Docs / Inbox / Consult** controls (project switcher only this iteration).
- **Settings sub-nav redesign** (General, Display, MCP Config, Squad Sync, Budget, Review policy, Portability, Backup & Restore, GitHub, Danger Zone) — the existing `ProjectSettingsPage` is wired in as-is.
- **Projects-landing redesign** (Create-from-template / Add Project, search, filter, sort, `.squad` badge cards, select checkboxes) — the existing gallery is retained.
- **Squadboard destinations Agentweaver lacks**: Skills, Tools, MCP Servers, Ceremonies, Templates, Costs, Now, Apps — not invented as placeholder pages.
