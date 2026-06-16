# Feature Specification: Memory and Decision Inbox

**Feature Branch**: `006-memory-and-decision-inbox`

**Created**: 2026-06-15

**Status**: Draft

**Input**: User description: "Memory & Decision Inbox System — a structured, queryable, audit-friendly memory and decision system for AI agent teams, interoperable with the bradygaster/squad SDK file-based ledger."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Agent Records a Decision (Priority: P1)

A Scribe agent, after completing a work batch, needs to record a team decision so that the entire agent team and the project stakeholders can see what was decided, why, and when. The agent submits a draft decision to the inbox, and after review it is atomically promoted to a finalized decision that appears in the shared ledger.

**Why this priority**: Decision recording is the core value of the system. Without a reliable way to capture and finalize decisions, the team has no persistent shared memory.

**Independent Test**: Can be fully tested by submitting an inbox entry via the API, calling the merge endpoint, and verifying that a finalized Decision record exists and the inbox entry is marked as merged. Delivers a working decision audit trail.

**Acceptance Scenarios**:

1. **Given** a project exists and an agent has completed a task, **When** the agent posts a draft decision to `/api/projects/{id}/decisions/inbox`, **Then** the system stores the entry with status `pending` and returns the entry's ID.
2. **Given** a pending inbox entry exists, **When** an authorized caller posts to `/api/projects/{id}/decisions/inbox/{entryId}/merge`, **Then** the system atomically creates a Decision record, marks the inbox entry as `merged` with a timestamp, and returns the new Decision's ID.
3. **Given** a merge has been completed, **When** any agent or user queries `/api/projects/{id}/decisions`, **Then** the finalized decision appears in the list with the correct type, content, and agent attribution.
4. **Given** a pending inbox entry exists, **When** a caller sends `DELETE /api/projects/{id}/decisions/inbox/{entryId}`, **Then** the entry is marked `rejected` and no Decision record is created.

---

### User Story 2 - Agent Persists and Recalls Memory (Priority: P2)

An agent working on a multi-session project needs to record what it has learned (patterns, context updates, core knowledge) so that future sessions can start with accumulated context rather than from scratch. The agent writes memory entries during a session and retrieves them on the next session start.

**Why this priority**: Without persistent per-agent memory the team loses continuity between sessions, forcing repeated context rebuilding and degrading output quality.

**Independent Test**: Can be fully tested by posting a memory entry for an agent, then retrieving it in a separate request filtered by type and importance. Delivers persistent, queryable agent knowledge.

**Acceptance Scenarios**:

1. **Given** an agent completes a learning during a session, **When** the agent posts to `/api/projects/{id}/agents/{name}/memory` with type `learning` and importance `high`, **Then** the memory is stored and retrievable by agent name.
2. **Given** multiple memory entries exist across types and importance levels, **When** the caller queries `/api/projects/{id}/agents/{name}/memory?type=learning&importance=high`, **Then** only entries matching both filters are returned.
3. **Given** memory entries exist across multiple agents, **When** the caller queries `/api/projects/{id}/memory?tags=["database","schema"]`, **Then** all memory entries across agents that match any of the provided tags are returned.

---

### User Story 3 - Session Context Tracking (Priority: P3)

A project coordinator needs to track what each agent session is focused on — its current issue list, focus area, and summary — so that the team can resume interrupted sessions and the web UI can display an accurate "what is happening right now" view.

**Why this priority**: Session tracking enables continuity and visibility but is less critical than decisions and memory for the core audit trail.

**Independent Test**: Can be fully tested by starting a session, updating its focus area, then querying the current session endpoint. Delivers live session state visibility.

**Acceptance Scenarios**:

1. **Given** a new work session begins, **When** the caller posts to `/api/projects/{id}/sessions` with a focus area and session ID, **Then** a SessionContext record is created and marked active (no `EndedAt`).
2. **Given** an active session exists, **When** the caller queries `GET /api/projects/{id}/sessions/current`, **Then** the most recent session with no `EndedAt` is returned.
3. **Given** an active session exists, **When** the caller patches `/api/projects/{id}/sessions/{sessionId}` with an `EndedAt` timestamp, **Then** the session is marked as ended and no longer returned as the current session.
4. **Given** a project starts a second session with the same `SessionId`, **When** the system receives the request, **Then** it rejects it with a conflict error (session IDs are unique per project).

---

### User Story 4 - Squad File Ledger Stays in Sync (Priority: P4)

A developer or external tool reading the repository directly expects to find up-to-date `.squad/decisions.md`, `.squad/decisions/inbox/*.md`, `.squad/agents/{name}/history.md`, and `.squad/identity/now.md` files that reflect the current database state. Any file written directly to the `.squad/` directory (fallback path) must also be reflected in the database.

**Why this priority**: File-ledger interoperability is needed for compatibility with tooling that reads the repo directly, but the system is usable without it (database is the system of record).

**Independent Test**: Can be tested by writing decisions and memory via the API, then verifying the exported files are regenerated correctly; and separately by dropping a file in `.squad/decisions/inbox/` and verifying it is imported into the database.

**Acceptance Scenarios**:

1. **Given** a Decision is created or merged via the API, **When** the export runs, **Then** `.squad/decisions.md` contains the decision's content and the inbox entry's corresponding file is present in `.squad/decisions/inbox/`.
2. **Given** an agent writes a markdown file directly to `.squad/decisions/inbox/`, **When** an import is triggered, **Then** the file's content is ingested as a `DecisionInboxEntry` without duplication (idempotent by project + agent + slug).
3. **Given** a session is active, **When** the export runs, **Then** `.squad/identity/now.md` reflects the current session's focus area and active issues.

---

### User Story 5 - Project Initialization Seeds Baseline Memory (Priority: P2)

When a new agent team is cast for a project, the system automatically creates the baseline memory records so that every agent starts its first session with project context, a genesis decision is recorded, and a session context is active — without requiring any manual post-cast API calls.

**Why this priority**: Init seeding ensures agents have context from the very first interaction and the audit trail starts at team formation.

**Independent Test**: Can be tested by running `ConfirmCastAsync` on a new project and verifying the database contains one `SessionContext`, one `AgentMemory` per agent of type `core_context`, and one genesis `Decision`.

**Acceptance Scenarios**:

1. **Given** a project cast is confirmed, **When** `ConfirmCastAsync` completes, **Then** exactly one `SessionContext` record exists for the initial session.
2. **Given** a project cast is confirmed with N agents, **When** `ConfirmCastAsync` completes, **Then** exactly N `AgentMemory` records of type `core_context` exist, one per agent.
3. **Given** a project cast is confirmed, **When** `ConfirmCastAsync` completes, **Then** exactly one genesis `Decision` of type `process` exists recording the team formation event.

---

### Edge Cases

- What happens when an agent submits an inbox entry with a slug that already exists for the same project and agent? The system must treat it as an idempotent update rather than creating a duplicate.
- How does the system handle a merge request for an inbox entry that has already been merged or rejected? It must return a conflict error — double-merge is not permitted.
- What happens when a Decision is superseded? The original record must be marked `superseded` and linked to the replacement Decision via `SupersededById` — both records remain queryable.
- What happens if the export runs while a merge is in progress? Exports must be idempotent and safe to run at any time; partial state must never corrupt the file ledger.
- What happens when memory entries are retrieved for an agent that has no records? The system must return an empty list, not an error.
- How are tag filters applied in the cross-agent memory search? Tags use AND-within-entry matching (an entry must contain all requested tags), or OR (any tag matches) — the spec assumes OR (any matching tag returns the entry).

## Requirements *(mandatory)*

### Functional Requirements

**Decision Inbox**

- **FR-001**: The system MUST allow any agent to submit a draft decision to the project's inbox via an API call, providing title, content, rationale, decision type, and a kebab-case slug.
- **FR-002**: The system MUST list all pending inbox entries for a project, with optional filtering by agent name and decision type.
- **FR-003**: The system MUST support atomically merging a pending inbox entry into a finalized Decision in a single API call, setting a merge timestamp and linking the entry to the new Decision record.
- **FR-004**: The system MUST prevent merging an inbox entry that has already been merged or rejected; such attempts must return an error.
- **FR-005**: The system MUST allow rejecting (deleting) a pending inbox entry.
- **FR-006**: Inbox entry slugs MUST be unique per project and agent combination; submitting a duplicate slug for the same project and agent MUST be treated as an idempotent re-submission.

**Decisions**

- **FR-007**: The system MUST allow direct creation of a Decision record (bypassing the inbox) for authorized roles such as Scribe.
- **FR-008**: The system MUST list active Decisions for a project, with optional filtering by type and agent name.
- **FR-009**: The system MUST allow updating a Decision's status, including marking it as superseded by linking it to a replacement Decision.
- **FR-010**: A superseded Decision MUST retain its original content and remain queryable; only its status changes.

**Agent Memory**

- **FR-011**: The system MUST allow an agent to record a memory entry with a type (core context, learning, update, or pattern), importance level (low, medium, or high), content, optional tags, and optional session reference.
- **FR-012**: The system MUST allow retrieving all memory entries for a named agent on a project, with optional filtering by type and importance.
- **FR-013**: The system MUST allow searching all memory entries across all agents on a project, with optional filtering by tags and type.

**Session Context**

- **FR-014**: The system MUST allow starting a session context for a project, recording the session ID, focus area, and active issues list.
- **FR-015**: Session IDs MUST be unique per project; attempting to start a session with a duplicate session ID for the same project MUST return a conflict error.
- **FR-016**: The system MUST allow updating a session's focus area, active issues, and summary, and allow marking it as ended.
- **FR-017**: The system MUST expose a single endpoint that returns the most recently started session that has not yet ended.

**Squad File Interoperability**

- **FR-018**: After every write operation (decision merge, decision create, memory record, session update), the system MUST regenerate the Squad-compatible file ledger under `.squad/` in the project's repository directory.
- **FR-019**: File export MUST be idempotent; running it multiple times with the same database state MUST produce identical files.
- **FR-020**: The system MUST support importing decision inbox entries written directly to `.squad/decisions/inbox/` as markdown files, ingesting them into the database without creating duplicates.
- **FR-021**: Import MUST be triggered on project load and on cast confirmation.

**Initialization**

- **FR-022**: When a project's agent cast is confirmed, the system MUST automatically seed one `SessionContext` for the initial session, one `AgentMemory` of type `core_context` per agent, and one genesis `Decision` of type `process`.

**Agent Prompt Integration**

- **FR-023**: Agent spawn templates MUST include a copy-pasteable prompt snippet instructing agents to use the REST API for all memory and decision writes instead of writing directly to `.squad/` files.

**Progressive Memory Disclosure in Runs (Context Compilation Pattern)**

- **FR-024**: When a run is submitted with an `agent_name`, `RunOrchestrator` MUST compile a context block using a `MemoryContextCompiler` and prepend it to the agent's system prompt before the run starts. The compiler MUST assemble the context in strict priority order (each layer overrides the previous when they conflict): (1) active `architectural` and `scope` Decisions — these are non-negotiable boundaries; (2) the agent's `core_context` memories — permanent role/project context; (3) up to 5 `high`-importance `learning` or `pattern` memories ordered by most recent; (4) the current session's `focus_area` and `active_issues`. The task itself is always appended last as the intent.
- **FR-025**: The compiled context block MUST use a clearly delimited, hierarchically structured markdown format so the agent can distinguish each layer. Required sections: `## Boundaries and Decisions` (from active architectural/scope decisions), `## Memory` (core context + high-importance learnings), `## Current Session` (focus area and active issues). The charter section is prepended separately and is always highest precedence.
- **FR-026**: After a run completes (reaches any terminal state), the run system MUST emit a structured harvest prompt to the agent requesting it to: (a) submit any new architectural or scope decisions made during the run to the inbox, (b) record new learnings or patterns as memory entries, and (c) flag any boundary violations encountered — cases where the run's constraints conflicted with the task requirements. The harvest prompt MUST include the project API base URL, project ID, and agent name so the agent can call the REST endpoints directly.
- **FR-027**: Context compilation MUST be a non-blocking best-effort operation. If the memory query fails, the run MUST proceed with the charter-only system prompt. The failure MUST be logged but MUST NOT prevent the run from starting.
- **FR-029**: The `SquadMemoryExporter` MUST also write a `.agentweaver/context/` directory to the managed repository root containing two compiled artifact files: `boundaries.md` (compiled from all active `architectural` and `scope` Decisions, formatted as a declarative constraints document) and `patterns.md` (compiled from all `pattern`-type AgentMemory entries across agents). These files make the team's accumulated context version-controllable alongside the code.
- **FR-030**: The `MemoryContextCompiler` MUST scope context to the run's agent. `learning` and `pattern` memories from other agents MUST NOT be injected unless they are tagged with `cross-team`. Decisions are always cross-agent (they are team-wide boundaries).

**MCP Server**

- **FR-028**: The MCP server (`apps/Scaffolder.Mcp`) MUST expose memory and decision operations as MCP tools so that MCP-capable AI clients can record and retrieve memory and decisions without calling the REST API directly. The tools MUST include: `decision_inbox_submit`, `decision_inbox_list`, `decision_inbox_merge`, `decision_inbox_reject`, `decision_create`, `decision_list`, `memory_record`, `memory_get`, `memory_search`, `session_start`, `session_current`, `session_update`.

**Post-Run Scribe Pass (Loop Close)**

- **FR-031**: When a *project* run with a non-null `AgentName` reaches a terminal state (`Completed`, `Merged`, `NoChanges`), the run system MUST automatically trigger a post-run Scribe pass. This pass closes the memory flywheel: the same context that was injected at run start is updated with what the agent produced. The pass MUST be non-blocking — a failure in the Scribe pass MUST NOT affect the run's own terminal state or status code.
- **FR-032**: The post-run Scribe pass MUST perform the following operations in order:
  1. **Auto-merge low-risk inbox entries** — merge all `pending` inbox entries of type `learning`, `pattern`, or `update` that were submitted during this run (identified by `AgentName` + `CreatedAt >= run.StartedAt`). These types are unambiguous and do not require coordinator review.
  2. **Hold architectural/scope entries for review** — inbox entries of type `architectural` or `scope` MUST remain `pending` and be flagged in the run's event stream (`scribe.inbox_review_needed`) so the coordinator is notified.
  3. **Trigger memory export** — call the equivalent of `POST /api/projects/{id}/memory/export` to regenerate `.squad/decisions.md`, `.squad/agents/{name}/history.md`, and `.agentweaver/context/boundaries.md` + `patterns.md`.
  4. **Update session context** — if a current open session exists, append the run ID and a one-line outcome to `ActiveIssues` or `Summary`.
- **FR-033**: The full memory loop for a project run with an agent is therefore:
  ```
  Pre-run:   CompileContext(DB) → inject into systemPromptContext
  Run:       Agent executes → harvest prompt triggers API calls (inbox + memory)
  Terminal:  RunWatchLoopService detects terminal state
  Scribe:    Auto-merge low-risk inbox → export to .squad/ + .agentweaver/context/ → update session
  Next run:  CompileContext sees richer decisions + memories → improved context
  ```
  Each cycle deposits new knowledge into the DB, which feeds the next compilation.

### Key Entities

- **Decision**: A finalized, audit-visible team decision. Belongs to a project; attributed to an agent; has a type (architectural, process, scope, technical), a status (active, superseded, archived), and an optional supersession link to another Decision. Content is markdown.
- **DecisionInboxEntry**: A draft decision submitted by an agent awaiting promotion. Belongs to a project; identified within a project-agent pair by a kebab-case slug (ensuring idempotency); transitions through pending, merged, and rejected statuses. When merged, it links to the resulting Decision record.
- **AgentMemory**: An accumulated knowledge record for a specific agent on a project. Typed as core context, learning, update, or pattern; weighted by importance; tagged for cross-agent search. Content is markdown. Optionally linked to a session.
- **SessionContext**: A record of one agent work session on a project. Tracks focus area, active issues (list), a summary, start time, and optional end time. Session ID is unique within a project.
- **MemoryContextCompiler**: A deterministic assembler that constructs the system prompt context block for a run from structured database artifacts. Applies a strict priority hierarchy (decisions > core context > high-importance memories > session focus) so that architectural boundaries override learnings, which override session state. Scopes memory to the target agent (cross-team tag overrides scoping). Produces a markdown document with named sections; the charter is always prepended separately and takes the highest precedence.
- **Context Artifacts (`.agentweaver/context/`)**: Version-controlled files compiled from the memory database and exported to the managed repository root. `boundaries.md` contains all active architectural and scope Decisions in declarative constraint format. `patterns.md` contains all pattern-type memories across agents. These files make the team's accumulated context auditable and diffable in git history alongside the code they govern.

### SessionContext Schema

| Field | Type (C#) | DB column | Notes |
|---|---|---|---|
| Id | `int` | `id INTEGER PRIMARY KEY` | Auto-increment surrogate key |
| ProjectId | `string` | `project_id TEXT NOT NULL` | FK to projects |
| SessionId | `string` | `session_id TEXT NOT NULL` | Unique within a project |
| FocusArea | `string` | `focus_area TEXT NOT NULL` | What the session is working on |
| ActiveIssues | `string` (JSON array) | `active_issues TEXT` | JSON-encoded list of issue refs |
| Summary | `string?` | `summary TEXT` | Human-readable briefing for agents resuming this session |
| SerializedState | `string?` (JSON) | `serialized_state TEXT` | Nullable. JSON from `SerializeSessionAsync`. Stored on session end; used by `DeserializeSessionAsync` for true context resumption rather than briefing notes. |
| StartedAt | `DateTimeOffset` | `started_at TEXT NOT NULL` | Session start timestamp |
| EndedAt | `DateTimeOffset?` | `ended_at TEXT` | Null until the session is explicitly closed |

> **`summary` vs `serialized_state`**: `summary` is a human-readable markdown briefing written by agents to communicate what happened during a session — it is consumed by humans and by agent prompts when starting a new session. `serialized_state` is a machine-readable SDK session snapshot produced by `SerializeSessionAsync` and consumed exclusively by `DeserializeSessionAsync` to programmatically restore the full SDK session state (conversation history, tool context, etc.). Only `serialized_state` enables true session resumption at the SDK level; `summary` provides continuity at the human/prompt level.

### DDL

```sql
CREATE TABLE IF NOT EXISTS session_context (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    focus_area TEXT NOT NULL,
    active_issues TEXT,
    summary TEXT,
    serialized_state TEXT,
    started_at TEXT NOT NULL,
    ended_at TEXT,
    UNIQUE(project_id, session_id)
);
```

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every finalized decision is queryable within the same request cycle it was merged or created — there is no delayed-write or eventual-consistency window.
- **SC-002**: The merge of an inbox entry into a Decision is atomic — a partial state (entry merged but no Decision, or Decision created but entry still pending) must never be observable by any concurrent query.
- **SC-003**: The Squad file ledger is regenerated after every write and reflects the current database state; a developer reading the files immediately after an API write sees up-to-date content.
- **SC-004**: File import is idempotent — running import twice on the same set of files produces the same database records with no duplicates.
- **SC-005**: All 15 REST endpoints respond within acceptable latency for interactive use (sub-second round-trip on a developer machine under light load).
- **SC-006**: Project initialization via `ConfirmCastAsync` produces a complete baseline: one session context, one memory record per agent, and one genesis decision, all verifiable immediately after cast confirmation.
- **SC-007**: Cross-agent memory search correctly narrows results by tag and type filters — no false positives (records that do not match the filter) are returned.
- **SC-008**: All agent-attributed records (decisions, memory entries, inbox entries) are queryable by agent name, ensuring per-agent accountability across the full audit trail.
- **SC-009**: When a run with `agent_name` starts, the compiled context block injected into the system prompt contains the Boundaries and Decisions section, the Memory section, and the Current Session section as distinct, non-overlapping markdown sections — verifiable by inspecting the `SystemPrompt` stored on the Run record.
- **SC-010**: After every export, `.agentweaver/context/boundaries.md` in the managed repository contains a section for every active `architectural` or `scope` Decision — no decisions are silently omitted.
- **SC-011**: The harvest prompt emitted after run completion explicitly requests submissions to the decision inbox, memory recording, and boundary violation flagging in structured form — the agent has all necessary API coordinates (URL, project ID, agent name) to act on it without querying additional endpoints.
- **SC-012**: After a project run with `AgentName` reaches terminal state, `pending` inbox entries of type `learning`, `pattern`, or `update` submitted by that agent during the run are automatically merged, and the `.squad/decisions.md` file is regenerated — verifiable by querying decisions before and after run completion.
- **SC-013**: After the post-run Scribe pass, the next `MemoryContextCompiler.CompileAsync` call for the same agent sees the newly merged decisions and memory entries in its output — the flywheel is closed and the context is richer than before the run.

## Assumptions

- The existing database storage layer (ADO.NET with SQLite) is being incrementally replaced; this feature introduces EF Core with the SQLite provider as the new pattern, but the existing stores are not migrated as part of this feature.
- The web UI and CLI are consumers of the same 15 REST endpoints — no separate write paths exist for those clients for memory and decision data.
- The Squad SDK file format (`.squad/decisions.md`, `.squad/decisions/inbox/*.md`, `.squad/agents/{name}/history.md`, `.squad/identity/now.md`) is treated as a stable external contract. The exporter must produce files compatible with that format.
- `ProjectId` is an existing entity in the system; this feature adds foreign-key relationships to it but does not change the projects model.
- Authentication and authorization for the 15 REST endpoints follow the existing project-scoped access pattern already established in the API; no new auth mechanisms are introduced.
- The agent prompt snippet is injected at spawn time by the existing agent spawning infrastructure; this feature defines the content of the snippet, not the injection mechanism itself.
- Tag filtering on cross-agent memory search uses OR semantics: an entry is returned if it matches any of the requested tags.
- "Authorized caller" for direct decision creation and inbox merge is the Scribe role; role enforcement follows the existing agent-role model.
- Local file system access to the project's `.squad/` directory is available to the backend API at runtime (same machine or mounted volume).
