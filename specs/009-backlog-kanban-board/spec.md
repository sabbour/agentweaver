# Feature Specification: Project Backlog & Workflow Kanban Board

**Feature Branch**: `009-backlog-kanban-board`

**Created**: 2026-06-19

**Status**: Draft

**Input**: User description: "I want to be able to drop tasks into a backlog per project. The Backlog captures things that are captured but not yet committed and are still under consideration. I also want to have a Ready bucket which has work I need to be picked up by the coordinator, which creates Runs with it through the workflow. This view should be a Kanban board representation of the workflow, and it should be what is on the project homepage. The 'buckets' (columns) of that Kanban board should be dynamically generated from the equivalent workflow. Anything that is moved to Ready should be picked up in the next coordinator heartbeat."

## Overview

A Project (Feature 003) is the container in which a team's runs operate. Today a run begins when a user explicitly submits work; there is no place to *park* ideas that are still under consideration, and no single view of where committed work currently sits. This feature adds that missing front-of-the-pipeline surface.

It introduces two pre-run holding buckets per project — a **Backlog** for captured-but-not-yet-committed tasks, and a **Ready** bucket for work the user has committed and wants the Squad Coordinator Agent (Feature 008) to pick up — and presents them, together with the live state of work already moving through the run workflow (`agent → RAI → human review → merge → scribe → terminal`, Features 001/008), as a single **Kanban board** that is the project homepage.

The board's columns are not hardcoded. After the two fixed intake columns (Backlog and Ready), the remaining columns are **dynamically generated from the project's effective workflow**, so the board always mirrors the actual stages a run passes through. When a user moves a task into Ready, the coordinator claims it on its **next heartbeat** and creates a Run from it, after which the task is represented on the board by the live state of that run.

Consistent with the constitution, this is an API-first capability: every action (capture a task, edit it, move it between Backlog and Ready, view the board) MUST be reachable identically from the MCP server and the Web UI, with no business logic in either client. This feature adds an intake-and-visualization layer only; it MUST NOT re-specify how runs execute, how the coordinator decomposes or dispatches work, or what the workflow stages do — it builds on those existing capabilities.

## Clarifications

### Session 2026-06-19

- Q: When the coordinator claims a Ready task and creates a Run, what happens to the underlying task record? → A: Retain the task record marked `claimed`, with a permanent 1:1 reference to the Run it produced; the card renders from run state. Preserves provenance and enables at-most-one-run dedupe.
- Q: Do completed (terminal) run cards stay on the board forever? → A: Terminal-state cards remain visible, but the board defaults to showing recent/active terminal items with older ones collapsed or filterable (readable without losing history).
- Q: How many Ready items are claimed per heartbeat? → A: At most N per heartbeat, where N is a per-project configurable setting (default 3); the remainder waits for the next heartbeat.
- Q: Is ordering within Backlog/Ready persisted/shared, and does it affect pickup? → A: Ordering is persisted and shared across the team AND determines coordinator pickup priority; combined with the N cap, each heartbeat claims the top-N Ready items in priority order.
- Q: Which user drag moves are valid? → A: Only Backlog↔Ready; Ready→Backlog is allowed only while the item is unclaimed; all workflow columns are read-only / coordinator-driven (not user drop targets).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Capture tasks into a project Backlog (Priority: P1)

A user has an idea or a piece of work that is not ready to commit. From the project, they drop a task into the Backlog with at least a title (and optionally a description). It is captured and persisted against that project, visible to anyone on the team, but nothing is dispatched and no run is created — it is explicitly "under consideration."

**Why this priority**: Capturing work is the foundational intake action this feature exists to provide. Without it there is nothing to triage, prioritize, or promote. It is the smallest slice that delivers standalone value: a per-project place to park ideas.

**Independent Test**: From a single client, add a task with a title to a project's Backlog, then confirm it is persisted, appears in that project's Backlog column, and that no run was created and the coordinator did not act on it.

**Acceptance Scenarios**:

1. **Given** an open project, **When** the user captures a task with a title into the Backlog, **Then** the task is persisted against that project and appears in the Backlog column.
2. **Given** a task being captured, **When** the user submits without a title (empty or whitespace only), **Then** capture is rejected with a clear reason and no task is created.
3. **Given** a task in the Backlog, **When** the user views the board, **Then** the task is shown as captured-but-not-committed and no run exists for it.
4. **Given** a Backlog task, **When** the user edits its title or description or deletes it, **Then** the change is persisted and reflected on the board, with no run affected.

---

### User Story 2 - Promote work to Ready for the coordinator to pick up (Priority: P1)

A user decides a Backlog task is committed and should be worked. They move it into the **Ready** bucket. On its next heartbeat, the Squad Coordinator Agent claims the Ready task and creates a Run from it that flows through the workflow. The task on the board then reflects the live state of that run rather than sitting in Ready.

**Why this priority**: Promotion-to-Ready plus automatic pickup is the core value: it is what turns parked intent into actual work without the user manually launching a run. Combined with US1 it forms the minimum viable feature — capture, commit, and the system takes it from there.

**Independent Test**: Move a Backlog task to Ready in a project whose coordinator is active; verify the task is claimed on the next heartbeat, a run is created from it, and the task is no longer shown as waiting in Ready but as the live state of its run.

**Acceptance Scenarios**:

1. **Given** a task in the Backlog, **When** the user moves it to Ready, **Then** the task is marked as committed and queued for coordinator pickup.
2. **Given** a task sitting in Ready, **When** the coordinator's next heartbeat occurs, **Then** the coordinator claims that task and creates a Run from it.
3. **Given** a Ready task that has been claimed and turned into a run, **When** the user views the board, **Then** the task is represented by the live state of its run in the appropriate workflow column, not as an unclaimed Ready item.
4. **Given** multiple tasks placed in Ready before a heartbeat, **When** the heartbeat occurs, **Then** the coordinator claims up to the per-project limit (default 3) of the highest-priority Ready tasks in order, each claimed exactly once and producing at most one run, with any remainder left in Ready for the next heartbeat (no duplicate runs on subsequent heartbeats).
5. **Given** a task in Ready that has not yet been claimed, **When** the user moves it back to the Backlog, **Then** it is no longer eligible for pickup and the coordinator does not create a run from it.

---

### User Story 3 - See the whole pipeline as a workflow Kanban board on the homepage (Priority: P1)

When the user opens a project, the project homepage is a Kanban board. The first two columns are the fixed intake buckets — **Backlog** and **Ready** — and the columns after them are generated from the project's effective workflow stages, in workflow order. Each card on the board represents a unit of work, positioned in the column for its current state; work that has become a run appears under the workflow stage that run currently occupies. The user can read the board at a glance to know what is parked, what is committed, and where in-flight work stands.

**Why this priority**: The board is the primary surface the feature describes and the place the other two stories are performed. Capture (US1) and promotion (US2) both happen on it, so it sits alongside them at P1.

**Independent Test**: Open a project that has Backlog tasks, Ready tasks, and runs at different workflow stages; verify the homepage shows a Kanban board whose first two columns are Backlog and Ready and whose remaining columns match the project's workflow stages in order, with each item shown in the correct column.

**Acceptance Scenarios**:

1. **Given** an open project, **When** the homepage loads, **Then** it displays a Kanban board whose first two columns are Backlog and Ready followed by one column per workflow stage in workflow order.
2. **Given** a project workflow with a specific set of stages, **When** the board renders its columns, **Then** the workflow columns are derived from that workflow (not a hardcoded list), so a different or changed workflow yields correspondingly different columns.
3. **Given** runs in progress at different stages, **When** the user views the board, **Then** each run-backed card appears in the column matching the workflow stage its run currently occupies.
4. **Given** a run advances from one workflow stage to the next, **When** the board is observed, **Then** the corresponding card moves to the next column to reflect the run's live state.
5. **Given** both clients, **When** the same project board is opened from the MCP server and from the Web UI, **Then** both present the same columns and the same items.

---

### Edge Cases

- **Empty project**: A project with no tasks and no runs still shows the board with Backlog and Ready columns and the full set of workflow columns, all empty, plus a clear way to capture the first task.
- **Coordinator inactive or not yet configured**: If the project has no active coordinator when a task is moved to Ready, the task remains visibly waiting in Ready and is claimed once a coordinator heartbeat occurs; the move itself does not fail or silently drop the task.
- **Workflow has no defined stages / cannot be resolved**: If the effective workflow cannot be resolved into stages, the board MUST still render the Backlog and Ready intake columns and surface a clear indication that workflow columns are unavailable, rather than showing a broken or empty board.
- **Task moved to Ready then back to Backlog before a heartbeat**: The task must end up in the Backlog and must not be claimed by a later heartbeat (no race that creates an orphan run).
- **Direct drag onto a workflow column**: Workflow columns reflect run state and are not user drop targets for raw tasks; an attempt to drop a Backlog/Ready task directly into a workflow stage column MUST be rejected with a clear reason (only the coordinator advances work into the workflow).
- **Run created from a Ready task is cancelled or fails**: The board reflects the run's terminal state in the appropriate column; the originating Ready task is not silently re-queued without a clear, deliberate action.
- **Concurrent edits / moves**: Two users acting on the same task at the same time (e.g., one edits while another promotes to Ready) MUST resolve to a single consistent state without losing the task or creating duplicates.
- **Coordinator misses or overlaps heartbeats**: A delayed, skipped, or overlapping heartbeat MUST NOT cause a Ready task to be claimed twice or produce duplicate runs.

## Requirements *(mandatory)*

### Functional Requirements

**Backlog (capture & triage)**

- **FR-001**: The system MUST let a user capture a task into a specific project's Backlog with, at minimum, a title; a description MUST be optional.
- **FR-002**: The system MUST reject capture of a task whose title is empty or whitespace-only, with a clear reason, and MUST NOT create the task.
- **FR-003**: Backlog tasks MUST be persisted against their project and scoped to it, so a task captured in one project never appears in another.
- **FR-004**: The system MUST treat Backlog tasks as captured-but-not-committed: a Backlog task MUST NOT cause any run to be created and MUST NOT be acted upon by the coordinator.
- **FR-005**: Users MUST be able to edit a Backlog task's title and description and to delete a Backlog task, with changes persisted and reflected on the board.

**Ready (commitment & pickup)**

- **FR-006**: Users MUST be able to move a task from the Backlog to the Ready bucket, marking it as committed work intended for the coordinator.
- **FR-007**: Users MUST be able to move an unclaimed Ready task back to the Backlog, after which it MUST NOT be eligible for coordinator pickup.
- **FR-008**: On each coordinator heartbeat, the coordinator MUST claim Ready tasks in shared priority order (highest priority first), claiming at most N tasks per heartbeat (see FR-008a), and create a Run from each claimed task, flowing it into the existing run workflow. Tasks beyond the per-heartbeat limit MUST remain in Ready and be eligible for the next heartbeat.
- **FR-008a**: The maximum number of Ready tasks claimed per coordinator heartbeat MUST be a per-project configurable setting with a default of 3; changing it MUST take effect on subsequent heartbeats without affecting already-claimed tasks.
- **FR-009**: The system MUST guarantee that a Ready task is claimed at most once and produces at most one run, even across delayed, skipped, or overlapping heartbeats (idempotent, exactly-once pickup). Once claimed, the task record MUST retain a permanent 1:1 reference to the Run it produced, which is the basis for this deduplication.
- **FR-010**: A task moved to Ready but not yet claimed MUST become eligible for pickup starting on the *next* heartbeat after it entered Ready, with no further user action required to trigger pickup. Actual claim timing depends on its priority position relative to the per-heartbeat limit (FR-008/FR-008a): a task within the top-N is claimed on the next heartbeat, while lower-priority tasks are claimed on subsequent heartbeats as higher-priority items clear.
- **FR-011**: If no coordinator is active for the project at the time of the move, the task MUST remain visibly waiting in Ready and be claimed on the first subsequent heartbeat; the move MUST NOT fail or drop the task.
- **FR-012**: Once a Ready task has been claimed and turned into a run, its task record MUST be retained and marked `claimed` with a permanent 1:1 reference to the created Run; on the board it MUST be represented by that run's live state and MUST NOT also remain shown as an unclaimed Ready item.

**Kanban board (homepage & dynamic columns)**

- **FR-013**: The project homepage MUST be a Kanban board representation of the project's pipeline.
- **FR-014**: The board's first two columns MUST be the fixed intake buckets Backlog and Ready, in that order.
- **FR-015**: The columns after Backlog and Ready MUST be dynamically generated from the project's effective workflow stages, presented in workflow order; the workflow columns MUST NOT be a hardcoded list, so a different or changed workflow yields correspondingly different columns.
- **FR-016**: Each item on the board MUST appear in exactly one column reflecting its current state: Backlog tasks in Backlog, committed-but-unclaimed tasks in Ready, and claimed/run-backed items in the column for the workflow stage their run currently occupies. Within Backlog and Ready, items MUST be presented in their persisted, team-shared priority order.
- **FR-016a**: Run-backed cards that have reached a terminal workflow stage MUST remain visible on the board, but the board MUST default to surfacing recent/active terminal items while allowing older terminal items to be collapsed or filtered, so the terminal column stays readable without losing history.
- **FR-017**: As a run advances through workflow stages, its card MUST move to the corresponding column to reflect the run's live state, observable without a manual refresh.
- **FR-018**: User-initiated moves MUST be limited to between the Backlog and Ready columns; a Ready→Backlog move MUST be permitted only while the item is still unclaimed. Workflow-stage columns MUST reflect run state and MUST NOT be user drop targets for any task or card; an attempt to drop a task directly into a workflow column MUST be rejected with a clear reason. Only the coordinator advances work into and through the workflow.
- **FR-018a**: Users MUST be able to reorder tasks within the Backlog and within Ready; this ordering MUST be persisted and shared across the team, and the Ready ordering MUST determine coordinator pickup priority (top-N by priority per heartbeat, per FR-008/FR-008a).
- **FR-019**: If the effective workflow cannot be resolved into stages, the board MUST still render the Backlog and Ready columns and clearly indicate that workflow columns are unavailable, rather than rendering a broken board.

**Cross-cutting (parity, observability, scope)**

- **FR-020**: Every capability in this feature — capture, edit, delete, move between Backlog and Ready, and view the board with its columns and items — MUST be reachable identically from the MCP server and the Web UI, with no business logic residing in either client.
- **FR-021**: This feature MUST reuse the existing run workflow, coordinator, and run-step streaming; it MUST NOT re-specify how runs execute, how the coordinator decomposes/dispatches work, or what each workflow stage does.
- **FR-022**: The board MUST reflect changes to Backlog/Ready contents and to run state for the open project, so that captures, moves, pickups, and stage transitions become visible to viewers of that project without a manual refresh.

### Key Entities *(include if feature involves data)*

- **Backlog Task**: A unit of intended work captured against a project. Key attributes: title (required), description (optional), the owning project, its bucket/state (Backlog vs Ready), a persisted team-shared ordering/priority position within its bucket, capture/commit timestamps, and the accountable human who captured it. Not yet a run while in Backlog or unclaimed in Ready.
- **Ready Item**: A Backlog Task that has been committed (moved to Ready) and is awaiting or has undergone coordinator pickup. Carries the claim state (unclaimed / claimed) and, once claimed, is retained with a permanent 1:1 reference to the Run created from it, ensuring the one-task-to-at-most-one-run guarantee and preserving provenance. Its priority position within Ready determines pickup order.
- **Run**: The existing unit of executing work (Features 001/008) created by the coordinator from a Ready Item; its current workflow stage determines which board column its card occupies. Defined elsewhere; referenced here, not redefined.
- **Workflow Stage**: A stage of the project's effective run workflow (e.g., agent, RAI, human review, merge, scribe, terminal). The ordered set of stages is the source for the board's dynamically generated columns. Defined by the workflow; referenced here, not redefined.
- **Kanban Board (Project Homepage View)**: The composed, per-project view whose columns are [Backlog, Ready, …workflow stages in order] and whose cards are Backlog Tasks, Ready Items, and Runs placed in their current-state column. Backlog and Ready cards are ordered by their persisted shared priority; terminal run cards remain present but may be collapsed/filtered by default.
- **Per-Project Pickup Configuration**: A project-scoped setting controlling the maximum number of Ready items the coordinator claims per heartbeat (default 3).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can capture a task into a project's Backlog in under 30 seconds from the project homepage, and the task appears on the board immediately.
- **SC-002**: 100% of tasks moved to Ready are claimed and turned into exactly one run by a subsequent coordinator heartbeat, with zero duplicate runs across repeated, delayed, or overlapping heartbeats.
- **SC-003**: A Ready task within the per-project per-heartbeat limit (top-N by priority, default N=3) while a coordinator is active is picked up by the next heartbeat (no additional user action required), measurable as: time-to-pickup for an in-limit task never exceeds one heartbeat interval after the move; lower-priority tasks are claimed on subsequent heartbeats as the queue clears.
- **SC-004**: For any project, the board's columns after Backlog and Ready exactly match the project's effective workflow stages in order — verified by changing the workflow and observing the columns change correspondingly, with no code change.
- **SC-005**: As runs progress, run-backed cards appear in the column matching their current workflow stage at least 95% of the time within a few seconds of the stage transition, without a manual refresh.
- **SC-006**: The identical board (same columns, same items, same placements) is observable from both the MCP server and the Web UI for the same project state.
- **SC-007**: A task captured in one project never appears on another project's board (0 cross-project leakage).

## Assumptions

- "Workflow" refers to the project's effective run workflow whose stages are `agent → RAI → human review → merge → scribe → terminal` (Features 001/008); this feature consumes that stage list rather than defining it, and adapts automatically if it changes.
- "Coordinator heartbeat" is the Squad Coordinator Agent's existing periodic activation cycle (Feature 008); this feature hooks Ready-bucket pickup into that cycle and does not introduce a new scheduler.
- A "task" at the intake stage is lightweight (title plus optional description); richer decomposition, casting, model selection, and dispatch remain the coordinator's responsibility (Feature 008) and are out of scope here.
- Backlog and Ready are the only two fixed/manual columns; all other columns are derived from the workflow. Backlog and Ready are not themselves workflow stages and are not user-reorderable relative to the workflow columns.
- Tasks and the board are scoped to a single project (Feature 003) and shared across that project's team, consistent with the team/project model.
- The accountable human for a captured task is the signed-in user (consistent with Feature 003's accountability model), preserving Responsible-AI accountability (Constitution Principle IX).
- Backlog and Ready ordering is persisted and shared across the team and is meaningful: the Ready ordering determines coordinator pickup priority. Each heartbeat the coordinator claims the top-N Ready items in priority order, where N is a per-project configurable limit (default 3); remaining Ready items wait for subsequent heartbeats.
- Mobile/responsive layout specifics of the board are out of scope; the requirement is functional parity across the two API clients, not a particular visual layout.
