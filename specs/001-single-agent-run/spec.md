# Feature Specification: Single-Agent File-Editing Run

**Feature Branch**: `001-single-agent-run`

**Created**: 2026-06-07

**Status**: Active

**Input**: User description: "Build the first feature: a single AI agent that runs an agent loop to complete a file-editing task. One agent runs from an originating branch inside its own session, with a dedicated artifact directory backed by a git worktree. The agent has read-file and write-file tools sandboxed to that directory. A user submits a natural-language task and selects a model source (GitHub Copilot SDK or Microsoft Foundry). Submitting starts a run that streams each step live. The user watches from the CLI or Web UI. On completion the worktree diff is the output; a human reviews and, on approval, the worktree merges back to the originating branch."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Submit a task and get a file-editing result (Priority: P1)

A user picks an originating branch, types a natural-language task (for example, "add a license header to every source file"), and submits it. A run starts: the agent reasons about the task, reads and writes files inside its own isolated working area, and stops when the task is done. The user ends up with a set of file changes that address the task, isolated from the originating branch until reviewed.

**Why this priority**: This is the core promise of the product - a single agent loop that turns a prompt into file changes inside a sandboxed session. Without it nothing else has value. It is the minimum slice that proves the basics are wired.

**Independent Test**: Submit a task against a known branch and confirm a run completes and produces file changes contained entirely within the run's own working area, with no modification to the originating branch.

**Acceptance Scenarios**:

1. **Given** an originating branch and a natural-language task, **When** the user submits the task, **Then** a run is created with its own isolated working area derived from that branch.
2. **Given** a running task, **When** the agent decides it needs file contents, **Then** it reads a file from inside the working area and receives the contents.
3. **Given** a running task, **When** the agent decides to change a file, **Then** it writes the file inside the working area and the change is persisted there.
4. **Given** the agent determines the task is complete, **When** the loop ends, **Then** the run is marked complete and the set of changes (the difference from the originating branch) is available as the run's output.

---

### User Story 2 - Watch a run's steps live (Priority: P2)

While a run is in progress, the user watches the run unfold step by step: each message the agent produces, each tool call it makes (read or write, with the target path), and the result of each tool call. The same live view is available whether the user is in the terminal client or the web client.

**Why this priority**: Observability is what makes the agent trustworthy and demonstrable. It is required to confirm the loop is actually working, but it builds on the run from Story 1.

**Independent Test**: Start a run and confirm that, from either client, the user sees an ordered, live stream of agent messages, tool calls, and tool results as they occur, ending when the run completes.

**Acceptance Scenarios**:

1. **Given** a run is in progress, **When** the agent emits a message, **Then** an `agent.message` event appears in the watching client without the user refreshing or re-requesting.
2. **Given** a run is in progress, **When** the agent calls a read or write tool, **Then** a `tool.call` event and its paired `tool.result` or `tool.error` event (carrying the same `callId`) appear in `sequence` order in the stream.
3. **Given** two users watching the same run from different clients, **When** an event occurs, **Then** both clients show the same event at the same `sequence`.
4. **Given** a run completes, **When** the final lifecycle event is delivered, **Then** the stream indicates the run has finished with a `run.completed` or `run.failed` event.
5. **Given** a watching client disconnects and reconnects with its `lastSeenSequence` (SSE `Last-Event-ID`), **When** it reconnects, **Then** the backend replays only events after `lastSeenSequence` and then continues live, and the client deduplicates any re-delivered event by `sequence`.

---

### User Story 3 - Choose the model source for a run (Priority: P3)

When submitting a task, the user selects which model source powers the run from the two supported providers. The selection applies to that run.

**Why this priority**: Selectable model source is a required product capability, but a single default provider is enough to demonstrate the loop, so this layers on after the core run and streaming.

**Independent Test**: Submit two runs choosing a different provider for each and confirm each run records and uses the provider the user selected.

**Acceptance Scenarios**:

1. **Given** the task submission flow, **When** the user is asked for a model source, **Then** exactly the two supported providers are offered.
2. **Given** the user selects a provider, **When** the run starts, **Then** the run uses that provider and records which provider was used.
3. **Given** no other provider exists, **When** the user attempts to use an unsupported source, **Then** the submission is rejected.

---

### User Story 4 - Review and approve the merge back (Priority: P4)

After a run completes, a human reviews the run's output (the set of changes the agent made). If the human approves, the changes are merged back into the originating branch. If the human does not approve, the originating branch is left unchanged.

**Why this priority**: Human-in-the-loop approval is what closes the loop and makes the output usable, but it depends on having a completed run with output to review.

**Independent Test**: Complete a run, review its changes, approve, and confirm the originating branch now contains exactly those changes; in a separate run, decline and confirm the originating branch is unchanged.

**Acceptance Scenarios**:

1. **Given** a completed run, **When** the human opens the result, **Then** the human sees the set of changes the run made relative to the originating branch.
2. **Given** a reviewed run, **When** the human approves, **Then** the run's changes are merged into the originating branch.
3. **Given** a reviewed run, **When** the human declines, **Then** the originating branch remains unchanged and the run's working area is left intact for reference.

---

### Edge Cases

- **Path escape attempts**: The agent attempts to read or write using an absolute path, a `..` traversal, or a symlink that points outside the working area. The operation is rejected and the rejection is reported as the tool result; nothing outside the working area is read or modified.
- **Non-existent or unreadable file**: The agent reads a path that does not exist inside the working area. The tool returns a clear failure result rather than ending the run abnormally.
- **Runaway loop**: The agent never declares the task done. The run is bounded so it cannot continue indefinitely; reaching a step-count or time limit emits a `run.bounded` event and ends the run in a terminal state the user can see.
- **Provider failure mid-run**: The selected model source becomes unavailable during a run. The run ends in a visible terminal failure state rather than hanging.
- **Divergent originating branch at merge time**: The originating branch has advanced since the run started and the changes conflict. The conflict is surfaced to the human; the originating branch is not modified without human resolution.
- **Client disconnect during streaming**: A watching client disconnects and reconnects. On reconnect the client presents its `lastSeenSequence` (SSE `Last-Event-ID`); the backend replays only events after that cursor from the durable event log - working across process restarts while the run is still within the retention window - and then continues live. Delivery is at-least-once, so the client deduplicates re-delivered events by per-run `sequence`.
- **Content-safety failure**: The model provider returns content that fails a content-safety check. The content is withheld from the client; the run ends in a visible terminal failure state; and the failure is recorded in the event log so the human accountable for the run can see what occurred and why the run ended.
- **Task or files containing secrets or personal data**: The user's task prompt or the files in the working area contain credentials, tokens, or personal data. The system relays to the model provider only what that provider requires for the active run and does not forward sensitive data to any other party; raw secrets and personal data are not written into event log payloads or client-facing outputs.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST provide exactly one agent that operates as an agent loop: evaluate the task, call tools, receive results, and repeat until the task is complete.
- **FR-002**: A run MUST be created from a user-chosen local git repository — an existing folder on disk that is already a valid git repository (initialized with `git init` or cloned from another repository) — and a user-chosen originating branch within that repository.
- **FR-003**: Each run MUST execute inside its own session, and each session MUST have a dedicated artifact directory backed by a git worktree checked out from the originating branch.
- **FR-004**: The session's artifact directory MUST be the agent's entire visible file system for the run.
- **FR-005**: The agent MUST have exactly two core tools available during a run: read a file and write a file.
- **FR-006**: Both file tools MUST operate only on paths inside the session's artifact directory.
- **FR-007**: The system MUST reject any file access whose path attempts to escape the artifact directory, including absolute paths, `..` traversal, and symlinks that resolve outside the directory.
- **FR-008**: A user MUST be able to submit a task as a natural-language prompt to start a run.
- **FR-009**: When submitting a task, the user MUST be able to select the model source for that run from exactly two providers: the GitHub Copilot SDK or Microsoft Foundry. No other source is permitted.
- **FR-010**: Submitting a task MUST start a run in which the agent reasons, calls the read and write tools, receives their results, and repeats until the task is done.
- **FR-011**: A run MUST emit its activity as an ordered stream of typed events drawn from a defined taxonomy: lifecycle events (`run.started`, `run.completed`, `run.failed`, `run.bounded`), content events (`agent.message`, `tool.call`, `tool.result`, `tool.rejected`, `tool.error`), and review/merge events (`review.requested`, `review.approved`, `review.declined`, `merge.completed`, `merge.failed`). `tool.rejected` and `tool.error` MUST be distinct event types from `tool.result`. The stream MUST NOT emit token-level deltas.
- **FR-012**: The system MUST allow a user to submit a task and watch a run's steps live from a terminal client (CLI/TUI) and from a web client, and both clients MUST be able to do everything the submission-and-watch flow allows.
- **FR-013**: A run MUST finish in a visible terminal state when the task is complete (or when it ends due to a bound or failure).
- **FR-014**: When a run completes, the changes made in the session's worktree - the difference against the originating branch - MUST be available as the run's output artifacts.
- **FR-015**: A human MUST be able to review a completed run's output before any change reaches the originating branch.
- **FR-016**: On human approval, the system MUST attempt to merge the session's worktree back into the originating branch; without approval, the originating branch MUST remain unchanged. If the merge attempt fails after approval (for example, due to conflicts from a diverged originating branch), the originating branch MUST remain unchanged, a `merge.failed` event MUST be emitted with a human-readable failure reason, and the run's working area MUST be preserved intact for human inspection.
- **FR-017**: The backend interface MUST be the single source of truth for the agent loop, tasks, and the stream of run steps, and each client MUST be a thin client over it with no independent task or run logic.
- **FR-018**: Every event MUST share a common envelope with the fields `runId`, `sequence`, `type`, `timestamp`, and `payload`; tool events (`tool.call`, `tool.result`, `tool.rejected`, `tool.error`) MUST additionally carry a `callId`. `timestamp` is informational only and MUST NOT be used to order events.
- **FR-019**: `sequence` MUST be a per-run monotonic value that establishes a total ordering of all events within a run.
- **FR-020**: Each `tool.result`, `tool.rejected`, and `tool.error` event MUST echo the `callId` of its originating `tool.call`, so a consumer can pair a tool outcome with its call without relying on stream adjacency.
- **FR-021**: A client MUST be able to reconnect to a run's stream using a resumable cursor (`lastSeenSequence`, surfaced as the SSE `Last-Event-ID`); on reconnect the system MUST replay only events after `lastSeenSequence` and then continue live. Delivery is at-least-once, and clients MUST deduplicate re-delivered events by per-run `sequence`.
- **FR-022**: The system MUST persist every run event in a durable, append-only, per-run event log, with the per-run `sequence` as the persisted cursor. The log MUST be retained for the full run plus a bounded post-completion retention window, and replay MUST work across process restarts for any `lastSeenSequence` still within the window.
- **FR-023**: Human review decisions and merge outcomes MUST be recorded as first-class events on the same per-run append-only event log (`review.requested`, `review.approved`, `review.declined`, `merge.completed`, `merge.failed`), sharing the same monotonic `sequence` and resumable cursor; the event log MUST span the full run lifecycle through merge.
- **FR-024**: Every run MUST record the identity of the user who submitted it. That user is the named human accountable for the run, and their identity MUST be preserved in the run's event log for the full retention window.
- **FR-025**: Before relaying any model-generated content to a client, the system MUST apply content-safety checks appropriate to the active model provider. Content that fails a safety check MUST be withheld from clients; the run MUST be ended in a visible terminal failure state; and the content-safety failure MUST be recorded as an event in the run's event log.
- **FR-026**: Secrets, credentials, and personal data MUST NOT appear in event log payloads, client-facing outputs, or operational records. Data forwarded to the model provider MUST be limited to what that provider requires for the active run; no other third party may receive it.
- **FR-027**: The rules governing which tools an agent may use, which model sources are permitted, the sandbox boundary, and the requirement for human approval before irreversible actions MUST be enforced uniformly for every run regardless of which client or interface initiates it. No client may grant itself permissions beyond what the governance rules allow.
- **FR-028**: The system MUST produce an operational record for every run, independent of the per-run event log, sufficient for debugging, compliance review, and capacity analysis. This operational record MUST capture at minimum: the submitting user's identity, selected model source, run start time, step count, outcome, and end time.
- **FR-029**: Every run MUST be subject to explicit, enforceable limits: at minimum a maximum step count and a maximum wall-clock duration. Reaching either limit MUST end the run in a visible terminal state by emitting a `run.bounded` event. These limits MUST be enforced by policy and MUST NOT be bypassable by any client or tool.

### Key Entities *(include if feature involves data)*

- **Run**: One execution of the agent against a submitted task. Has a local git repository, an originating branch, a selected model source, a current status (for example: in progress, complete, failed), a durable append-only event log, and an output (the diff against the originating branch).
- **Session**: The isolated execution context for a single run. Owns one artifact directory.
- **Artifact Directory (Worktree)**: The per-session directory, checked out from the originating branch, that is the agent's entire visible file system and that scopes all file access.
- **Task**: The natural-language prompt a user submits to start a run.
- **Event**: One observable, immutable record in a run's stream. Shares a common envelope (`runId`, `sequence`, `type`, `timestamp`, `payload`, plus `callId` on tool events). `type` is one of the defined lifecycle, content, or review/merge event types (formerly modeled as a generic "Step"). `sequence` is per-run monotonic and provides total ordering; `timestamp` is informational only.
- **Event Log**: The durable, append-only, per-run sequence of Events spanning the full run lifecycle through merge. Retained for the full run plus a bounded post-completion retention window. The per-run `sequence` is the persisted cursor enabling reconnect and replay across process restarts.
- **Tool Call / Tool Result**: A read-file or write-file invocation against a path inside the artifact directory, and its outcome. A `tool.call` carries a `callId`; the outcome is reported as a `tool.result` (success), `tool.rejected` (denied, e.g. path escape), or `tool.error` (failure such as not-found), each echoing the originating `callId`.
- **Model Source**: The provider selected for a run; one of the two supported providers.
- **Local Git Repository**: The folder on disk chosen by the user at run submission time; it must already be a valid git repository (initialized with `git init` or cloned from another repository). All branches and worktrees for a run are resolved within this folder.
- **Originating Branch**: The branch within the chosen local git repository that a run starts from and that an approved run merges back into.

### Non-Functional Requirements

- **NFR-001**: The system MUST operate correctly in both a local developer environment and a hosted cloud environment using the same deployable artifact. No behavior specific to either environment may be required or require a dedicated code path.
- **NFR-002**: All text produced by the system (event log payloads, operational records, client-facing output, and any content the agent writes into the working area) MUST be free of emoji characters.
- **NFR-003**: Before the feature is released, all system-controlled defaults that can influence AI behavior (default prompts, tool defaults, and model-source defaults) MUST be reviewed for potential bias or fairness concerns. Any identified concern MUST be documented and a mitigation plan established before release.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user can go from submitting a task to seeing the first streamed step in under 10 seconds under normal conditions.
- **SC-002**: 100% of file operations targeting a path outside the session's artifact directory are rejected, with zero reads or writes occurring outside that directory.
- **SC-003**: A user can complete the full flow - submit a task, watch it run, review the output, and approve the merge - entirely from the CLI, and separately entirely from the Web UI, with identical outcomes.
- **SC-004**: For an approved run, the changes merged into the originating branch match the run's reviewed output exactly, with no additional or missing changes.
- **SC-005**: For a declined run, the originating branch is byte-for-byte unchanged from before the run.
- **SC-006**: During a run, every emitted event (agent message, tool call, tool outcome, lifecycle, and review/merge event) appears in the live stream in `sequence` order, with no gaps from a watching client, including after a disconnect-and-reconnect from the client's `lastSeenSequence`.
- **SC-007**: Two runs started from the same originating branch do not read or write each other's files at any point.
- **SC-008**: 100% of model-generated outputs that fail a content-safety check are withheld from clients and recorded in the event log, with zero safety-failing content reaching any client.
- **SC-009**: No secrets, credentials, or personal data appear in any event log payload, client-visible output, or operational record across any run.
- **SC-010**: Every governance policy decision (tool permission, model-source validation, sandbox boundary enforcement, and human-approval gate) produces a traceable entry in the operational record, enabling a compliance reviewer to reconstruct all policy outcomes for any run within the retention window.

## Assumptions

- The specific values for the run bounds required by FR-029 (maximum step count and maximum wall-clock duration) are tuning details to be established during planning; the existence and enforcement of those bounds are mandatory.
- The agent declares completion itself (by ending the loop with a final message and no further tool calls); "task done" means the loop terminated normally.
- "File system" for a run means regular files and directories within the artifact directory; reads and writes operate on text content for this slice.
- Concurrent runs are permitted, each in its own worktree and session; isolation between runs is provided by separate artifact directories.
- At merge time, if the originating branch has diverged and the changes conflict, the conflict is surfaced to the human and the originating branch is not changed without human resolution; automatic conflict resolution is out of scope for this slice.
- Authentication and authorization for users and for the two model-source providers are handled by standard mechanisms and are not specified in detail here.
- This is the first testing slice; breadth (multiple agents, boards, additional tools) is intentionally excluded and will be addressed in later features.
- The post-completion retention window for a run's event log is bounded but its exact duration is a tuning detail for planning; reconnect/replay is guaranteed only for a `lastSeenSequence` still within that window.
- The format and storage mechanism for the operational record required by FR-028 are implementation details for planning; the operational record is distinct from the per-run event log and is intended for operational and compliance consumers, not end users.
- The content-safety mechanism applied under FR-025 depends on the active model provider's safety capabilities; both supported providers are expected to expose content-safety capabilities that the system can invoke before relaying model output to clients.
- The transport protocol and API endpoint shape for the live event stream (SSE, WebSocket, URL conventions) are implementation details to be established during planning; the spec requires that the mechanism supports reconnect with a resumable cursor (`lastSeenSequence`) and replay from that cursor.
