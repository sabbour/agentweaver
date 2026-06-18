# Feature Specification: Squad Coordinator Agent

**Feature Branch**: `008-coordinator-agent`

**Created**: 2026-06-17

**Status**: Draft

**Input**: User description: "Each team comes with a built-in Squad Coordinator Agent. It uses memories and decisions to identify work as a short outcome-based spec, asks the user to confirm the spec, then selects the best agents from the cast/roster, chooses a model per task by complexity, decomposes the spec into subtasks, decides whether to serialize or fan out in parallel, and launches those agents as subagents with a read-only timeline on their runs. It collects results, reports progress, and steers the subagents. It plugs into the existing workflow: each subagent stream passes RAI, but the second human review, merge, and scribe happen on the collective output of all agents; RAI and review outcomes flow back to the coordinator, which dispatches follow-up work. It bubbles up questions and permission requests from subagents, may split a unit of work into planning/execution/validation phases each with its own model, and knows when to create a branch vs a worktree, identifying dependencies and potential merge conflicts. The coordinator is launched as an agent in the MAF workflow, and its charter must not duplicate functionality the platform already provides."

## Background & Non-Redundancy Constraint *(mandatory context)*

The platform already provides, as first-class capabilities, the building blocks the coordinator orchestrates. The coordinator MUST build on these rather than reimplement them:

- **Single-agent runs and the run workflow** (Feature 001): the agent loop and the existing run pipeline `agent → RAI → human review → merge → scribe → terminal`.
- **Responsible-AI gate** (within 001's workflow): the RAI pass that flags or requests revision before ship.
- **Human review, merge, and scribe** stages (within 001's workflow): the approval gate, branch/worktree merge, and post-run memory/summarization pass.
- **Sandboxed execution** (Feature 002): isolated workspaces and host-capability probing.
- **Projects** (Feature 003): the container that bounds runs, teams, and memory.
- **Agent team casting** (Feature 005): the cast/roster, per-role charters, and per-role default model (overridable at runtime).
- **Memory and decisions** (Feature 006): team memories, finalized decisions, and the decision inbox.

This feature adds exactly one new thing: an **orchestration layer** — a built-in coordinator that turns a goal into confirmed, decomposed work and drives a team of these existing single-agent runs to a single combined outcome. The coordinator's charter MUST defer to the capabilities above and MUST NOT re-specify RAI checks, casting, memory governance, sandboxing, review, merge, or scribe as its own behavior.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Turn a goal into a confirmed outcome spec (Priority: P1)

A user gives the coordinator a goal in plain language. The coordinator consults the team's existing memories and decisions, then produces a short, outcome-based specification — what success looks like, the scope, and the assumptions it is making. It presents this to the user for review and confirmation, asking only the clarifying questions that materially affect scope. No work is dispatched until the user confirms.

**Why this priority**: Confirmation before dispatch is the safety and alignment foundation for everything else. On its own it delivers value: the user gets a structured, memory-informed restatement of their goal they can correct before any agent runs.

**Independent Test**: Give the coordinator a goal in a project that has existing memories/decisions; verify it produces an outcome spec that reflects that prior context, asks at most a few scoped clarifying questions, and does not start any subagent work until the user confirms.

**Acceptance Scenarios**:

1. **Given** a project with existing memories and decisions, **When** a user states a goal, **Then** the coordinator produces a short outcome-based spec that reflects relevant prior decisions and presents it for confirmation.
2. **Given** an ambiguous goal, **When** the coordinator drafts the spec, **Then** it asks scoped clarifying questions and incorporates the user's answers into the spec before requesting final confirmation.
3. **Given** an outcome spec awaiting confirmation, **When** the user has not yet confirmed, **Then** no subagent work has been dispatched.
4. **Given** a presented outcome spec, **When** the user requests changes, **Then** the coordinator revises and re-presents it without dispatching work.

---

### User Story 2 - Decompose, cast, and orchestrate subagents to a result (Priority: P1)

Once the spec is confirmed, the coordinator selects the most suitable agents from the team's roster, chooses a model for each unit of work based on its complexity, decomposes the spec into subtasks, and decides which subtasks can run in parallel versus which must be serialized because of dependencies. It launches the selected agents as subagents, observes each one through a read-only timeline of its run, collects their results, reports progress back to the user, and can steer subagents (redirect, amend, or stop) while they work — relaying direction the user gives the coordinator to the running subagents, where a stop takes effect immediately and redirect/amend take effect at the subagent's next turn boundary without restarting the run. Pause is not supported in Phase 2.

**Why this priority**: This is the core orchestration value — the coordinator actually getting a team to do the work. Combined with US1 it forms the minimum viable coordinator: confirm, then deliver.

**Independent Test**: Confirm a spec that naturally splits into independent and dependent subtasks; verify the coordinator assigns appropriate roster agents and per-task models, runs independent subtasks in parallel and dependent ones in order, streams progress, and lets the user steer a running subagent.

**Acceptance Scenarios**:

1. **Given** a confirmed spec, **When** the coordinator plans the work, **Then** it assigns each subtask to a roster agent whose role fits and selects a model appropriate to that subtask's complexity.
2. **Given** subtasks with no dependencies between them, **When** the coordinator dispatches, **Then** those subtasks run in parallel.
3. **Given** subtasks where one depends on another's output, **When** the coordinator dispatches, **Then** the dependent subtask does not start until its prerequisite completes.
4. **Given** running subagents, **When** the user asks for status, **Then** the coordinator reports per-subagent progress derived from each run's read-only timeline.
5. **Given** a running subagent going off track, **When** the user (or the coordinator) intervenes, **Then** the coordinator can redirect, amend, or stop that subagent without disrupting unrelated subagents.
6. **Given** subagents are running, **When** the user gives the coordinator new direction, **Then** the coordinator relays it to the affected subagent(s) — a stop applying immediately and a redirect/amend applying at the subagent's next turn boundary, without restarting the run.
7. **Given** all subagents have produced results, **When** they complete, **Then** the coordinator collects and combines their outputs into a single coherent result.

---

### User Story 3 - Per-stream RAI, then one collective review/merge/scribe (Priority: P2)

Each subagent's individual output passes the Responsible-AI check as part of its own run. RAI findings flow back to the coordinator, which dispatches the appropriate fix rather than the original author silently retrying. Once all subagent work is combined, the **collective** output goes through a single second human review, a single merge, and a single scribe pass — not once per subagent. The human review outcome (approve / request changes / decline) flows back to the coordinator, which dispatches any follow-up work.

**Why this priority**: This is what makes multi-agent work safe and reviewable without overwhelming the human. It depends on US2 producing combined output, so it follows P1.

**Independent Test**: Run a fan-out that produces a combined result; verify each subagent stream was RAI-checked individually, that exactly one human review / merge / scribe ran over the combined output, and that an RAI flag or a "request changes" verdict routes back through the coordinator as new dispatched work.

**Acceptance Scenarios**:

1. **Given** a subagent produces output, **When** its run reaches the RAI stage, **Then** RAI evaluates that subagent's output individually.
2. **Given** RAI flags or requests revision on a subagent's output, **When** the finding is raised, **Then** it flows back to the coordinator, which dispatches a fix (subject to reviewer-rejection rules) rather than the result shipping.
3. **Given** all subagent outputs are combined, **When** the work is ready, **Then** exactly one human review, one merge, and one scribe pass run over the collective output.
4. **Given** the human review returns "request changes," **When** the verdict is delivered, **Then** it flows back to the coordinator, which decomposes and dispatches the follow-up work.
5. **Given** the human review returns "approve," **When** the verdict is delivered, **Then** the combined output is merged and the scribe pass records the session once.

---

### User Story 4 - Bubble up questions and permission requests (Priority: P2)

While subagents work, any of them may need a clarification answered or permission to take a gated/irreversible action. The coordinator surfaces these to the accountable human in one place, attributes each request to its originating subagent, and relays the human's answer or decision back to the right subagent so it can proceed (or stop). Work that does not depend on the pending answer continues meanwhile.

**Why this priority**: Required for the human-in-the-loop guarantee across a team, but the coordinator can deliver core value (US1–US2) without it in a happy-path scenario, so it is P2.

**Independent Test**: Have a subagent request both a clarification and a permission for a gated action; verify both surface to the human attributed to that subagent, the answers route back to it, and independent subagents keep working while the request is pending.

**Acceptance Scenarios**:

1. **Given** a subagent needs a clarification, **When** it asks, **Then** the coordinator surfaces the question to the human attributed to that subagent.
2. **Given** a subagent requests permission for a gated or irreversible action, **When** it asks, **Then** the coordinator surfaces the permission request and the action does not proceed until the human decides.
3. **Given** the human answers a bubbled-up request, **When** the answer is given, **Then** the coordinator relays it to the originating subagent.
4. **Given** one subagent is blocked on a pending request, **When** other subagents have independent work, **Then** those other subagents continue without waiting.

---

### User Story 5 - Split a unit of work into phased subagents with per-phase models (Priority: P3)

For a complex unit of work, the coordinator may split it into distinct planning, execution, and validation phases and run each phase with a model appropriate to that phase (for example, a stronger model for planning and validation, a faster model for mechanical execution). Each phase hands its output to the next.

**Why this priority**: A quality and cost optimization that improves outcomes on hard tasks, but the coordinator is fully functional treating each unit as a single phase, so it is P3.

**Independent Test**: Give the coordinator a complex unit of work; verify it can run separate planning, execution, and validation phases, each with its own selected model, with output flowing from one phase to the next.

**Acceptance Scenarios**:

1. **Given** a complex unit of work, **When** the coordinator chooses to phase it, **Then** it creates distinct planning, execution, and validation phases.
2. **Given** phased work, **When** each phase runs, **Then** the coordinator selects a model appropriate to that phase.
3. **Given** a completed planning phase, **When** execution begins, **Then** execution consumes the planning output, and validation subsequently checks the execution output.

---

### User Story 6 - Branch/worktree, dependency, and merge-conflict awareness (Priority: P3)

When deciding how subagents do their work, the coordinator chooses the appropriate isolation strategy — a shared branch when work is tightly coupled and must be serialized, or separate worktrees when independent work can proceed in parallel. It identifies dependencies between subtasks and anticipates potential merge conflicts when assembling the collective output, sequencing or isolating work to avoid them.

**Why this priority**: Optimizes parallel throughput and reduces failed merges, but the coordinator can fall back to a conservative serialized strategy and still deliver, so it is P3.

**Independent Test**: Give the coordinator a spec where some subtasks touch overlapping areas and others are independent; verify it isolates the independent work for parallel execution, serializes or sequences the overlapping work, and assembles the combined output without unresolved conflicts.

**Acceptance Scenarios**:

1. **Given** independent subtasks, **When** the coordinator plans isolation, **Then** it allows them to proceed in parallel isolated workspaces.
2. **Given** subtasks likely to conflict, **When** the coordinator plans isolation, **Then** it serializes or sequences them to avoid conflicting concurrent edits.
3. **Given** subtask outputs ready to combine, **When** the coordinator assembles the collective output, **Then** it identifies and resolves (or routes for resolution) any merge conflicts before the single human review.

---

### Edge Cases

- **No suitable agent in the roster**: the coordinator surfaces the gap to the user (and may suggest adding a role via casting) rather than forcing an ill-suited agent onto the work.
- **User declines the outcome spec entirely**: no work is dispatched; the session ends or returns to drafting.
- **A subagent fails or its run terminates abnormally**: the coordinator reports the failure, preserves completed sibling work, and offers to retry or re-route that subtask without losing the rest.
- **RAI repeatedly flags the same subtask**: reviewer-rejection handling applies — the original author is locked out and a different agent owns the revision; if all eligible agents are exhausted, the coordinator escalates to the human.
- **Conflicting decisions in memory**: the coordinator surfaces the conflict in the outcome spec for the user to resolve rather than silently choosing one.
- **Human review requests changes after merge-readiness**: the combined output does not ship; the verdict routes back to the coordinator as new dispatched work.
- **A bubbled-up permission request is denied**: the dependent action and any subtasks that require it stop; independent work still completes.
- **Parallel subagents produce unmergeable overlapping output despite isolation**: the coordinator detects the conflict at assembly time and serializes a reconciliation pass before review.

## Requirements *(mandatory)*

### Functional Requirements

**Built-in coordinator and scope**

- **FR-001**: Every team MUST include a built-in coordinator agent, provisioned automatically when the team is created, that orchestrates the team's other agents.
- **FR-002**: The coordinator MUST be launched as an agent within the existing MAF run workflow and MUST itself be an observable run (its messages, tool calls, and results visible through the standard run step stream).
- **FR-003**: The coordinator's charter MUST defer to existing platform capabilities (RAI gate, casting/roster, memory and decisions, sandboxed execution, branch/worktree isolation, human review, merge, scribe) and MUST NOT reimplement or re-specify any of them. The single exception is that the coordinator MAY read and write the team's memories and decisions (Feature 006) on the team's behalf — including persisting the work plan that dispatched subagents work from — using the existing memory/decision store rather than a parallel one.
- **FR-004**: The coordinator MUST NOT perform domain work directly (code, designs, analyses); every unit of domain work MUST be delegated to a roster agent. Producing and persisting the outcome spec and the work plan is orchestration, not domain work.
- **FR-004a**: The coordinator MUST persist the confirmed outcome spec and the per-subtask plan (assignments, models, dependencies, isolation strategy) to the team's shared memory/decision store so that each dispatched subagent reads its scope and context from that stored plan.

**Outcome spec and confirmation**

- **FR-005**: The coordinator MUST read the team's existing memories and decisions and use them as context when interpreting a user goal.
- **FR-006**: The coordinator MUST produce a short, outcome-based specification (desired outcome, scope, and stated assumptions) from a user goal, using spec-kit-style prompting techniques.
- **FR-007**: The coordinator MUST present the outcome spec to the user for review and confirmation, and MUST ask clarifying questions limited to those that materially affect scope.
- **FR-008**: The coordinator MUST NOT dispatch any subagent work until the user confirms the outcome spec.
- **FR-009**: When the user requests changes to the outcome spec, the coordinator MUST revise and re-present it without dispatching work.

**Decomposition, casting, and model selection**

- **FR-010**: After confirmation, the coordinator MUST decompose the spec into subtasks.
- **FR-011**: The coordinator MUST select, for each subtask, the most suitable agent from the team's roster based on role fit.
- **FR-012**: The coordinator MUST select a model for each unit of work based on its task type and complexity, honoring each role's default model while applying a runtime override when complexity warrants. The model provider remains fixed to GitHub Copilot.
- **FR-013**: The coordinator MUST determine subtask dependencies and decide, per subtask, whether it can run in parallel with others or must be serialized.

**Dispatch, observation, steering**

- **FR-014**: The coordinator MUST launch selected agents as subagents and MUST run independent subtasks in parallel and dependent subtasks in dependency order.
- **FR-015**: Each subagent MUST be launched as a first-class child run — with its own step stream, its own RAI pass, and its own sandbox — parented by the coordinator's run. The coordinator MUST observe each subagent through a read-only timeline of that child run.
- **FR-016**: The coordinator MUST collect subagent results and combine them into a single coherent collective output.
- **FR-017**: The coordinator MUST report per-subagent progress to the user on request.
- **FR-018**: The coordinator MUST be able to steer a running subagent — redirect, amend, or stop it — without disrupting unrelated subagents.
- **FR-018a**: A user MUST be able to steer the coordinator while subagents are running — issuing direction (redirect, amend scope, or stop) that the coordinator relays to one or more running subagents. A stop MUST take effect immediately (cancellation); a redirect or amend MUST take effect at the targeted subagent's next turn boundary, without restarting the run. Pause is not supported in Phase 2 and is deferred to a later phase.

**Workflow integration (RAI, review, merge, scribe)**

- **FR-019**: Each subagent's individual output MUST pass the Responsible-AI check as part of its own run.
- **FR-020**: RAI findings on a subagent's output MUST flow back to the coordinator, which dispatches the corresponding fix; flagged output MUST NOT ship.
- **FR-021**: The second human review, the merge, and the scribe pass MUST each run exactly once over the **collective** output of all subagents, not once per subagent.
- **FR-022**: The human review outcome (approve / request changes / decline) MUST flow back to the coordinator, which dispatches any follow-up work derived from a "request changes" verdict.
- **FR-023**: When a fix is dispatched following an RAI flag or a review rejection, reviewer-rejection rules MUST apply (the original author is locked out and a different agent owns the revision).

**Bubble-up of questions and permissions**

- **FR-024**: The coordinator MUST surface subagent clarification questions and permission requests to the accountable human, attributed to the originating subagent.
- **FR-025**: A subagent's gated or irreversible action MUST NOT proceed until the human grants permission via the coordinator.
- **FR-026**: The coordinator MUST relay the human's answer or decision back to the originating subagent.
- **FR-027**: Subtasks that do not depend on a pending bubbled-up request MUST continue while the request is outstanding.

**Phasing**

- **FR-028**: The coordinator MUST be able to split a unit of work into planning, execution, and validation phases, with each phase's output feeding the next.
- **FR-029**: The coordinator MUST be able to select a different model per phase.

**Isolation, dependencies, and conflicts**

- **FR-030**: The coordinator MUST decide the isolation strategy per unit of work based on dependency analysis — separate worktrees for independent work that can run in parallel, and a shared/serialized workspace for tightly coupled work — using the existing isolation mechanisms rather than new ones. It MUST then assemble the parallel outputs into one collective output for a single human review/merge.
- **FR-031**: The coordinator MUST identify potential merge conflicts before assembling the collective output and MUST sequence, isolate, or route for resolution to avoid unresolved conflicts at the single human-review gate.

### Key Entities *(include if feature involves data)*

- **Coordinator agent**: the built-in, per-team orchestration agent; a roster member with a charter scoped to orchestration only. Provisioned with the team.
- **Outcome spec**: a short, confirmable statement of desired outcome, scope, and assumptions derived from a user goal and the team's memories/decisions; gates dispatch until confirmed.
- **Subtask**: a unit of work decomposed from the outcome spec, with an assigned roster agent, a selected model, a dependency relationship to other subtasks, an isolation strategy, and an optional phase (planning/execution/validation).
- **Subagent run**: a first-class child run — own step stream, own RAI pass, own sandbox — parented by the coordinator's run; the coordinator watches its read-only timeline.
- **Work plan**: the persisted decomposition (subtasks with assigned agent, selected model, dependencies, isolation strategy, optional phase) stored in the team's memory/decision store; dispatched subagents read their scope and context from it.
- **Collective output**: the combined result of all subagent runs that goes through a single human review, merge, and scribe pass.
- **Bubble-up request**: a clarification or permission request raised by a subagent, attributed to it, routed to the human and back through the coordinator.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For a goal a team can address, the coordinator produces a confirmable outcome spec, and in 100% of cases no subagent work begins before the user confirms.
- **SC-002**: For a spec whose subtasks include both independent and dependent work, the coordinator runs all independent subtasks concurrently and never starts a dependent subtask before its prerequisite completes.
- **SC-003**: A user can obtain accurate per-subagent progress at any time during a multi-agent run, and can issue direction that reaches a running subagent — taking effect immediately for a stop, or at the subagent's next turn boundary for a redirect/amend — without restarting the run. Pause is not supported in Phase 2.
- **SC-004**: Across a multi-agent run, the second human review, the merge, and the scribe pass each occur exactly once over the combined output (never per subagent).
- **SC-005**: 100% of RAI flags and review "request changes" verdicts route back through the coordinator and result in dispatched follow-up work rather than silently shipping or silently dropping.
- **SC-006**: 100% of subagent permission requests for gated/irreversible actions block the action until a human decides.
- **SC-007**: For a parallelizable goal of comparable size, a fan-out run reaches the single human-review gate faster than running the same subtasks one at a time.
- **SC-008**: When subtasks touch overlapping areas, the coordinator assembles the collective output with zero unresolved merge conflicts reaching the human-review gate.

## Assumptions

- The coordinator is a roster member shipped with every team (per Feature 005 casting), provisioned at team-creation time; it has a charter like any other agent but its charter is orchestration-only.
- The coordinator's prompt/charter is adapted from the reference Squad coordinator governance (the project's template and the upstream reference), with all platform-provided behaviors (RAI, memory, casting, sandbox, review, merge, scribe) removed so the charter does not duplicate platform functionality.
- "Memories and decisions" the coordinator consumes are exactly those defined by Feature 006; the coordinator reads them and also writes to them on the team's behalf (notably to persist the confirmed outcome spec and work plan that subagents read from), without introducing a parallel memory store.
- Each subagent is a first-class child run parented by the coordinator's run, so per-subagent RAI, sandboxing, and step streaming reuse the existing single-agent run machinery (Features 001/002); the coordinator adds the parent/child relationship and the read-only observation, not new run primitives.
- The user can steer the coordinator during a multi-agent run; the coordinator relays that direction to running subagents rather than only acting between batches. Because an in-flight agent turn cannot be interrupted mid-turn under the run model, a stop cancels immediately while redirect/amend are queued and applied at the subagent's next turn boundary. Pause is not supported in Phase 2.
- "Cast/roster," per-role charters, and per-role default models are exactly those defined by Feature 005; model selection stays within the GitHub-Copilot-only provider constraint (Constitution Principle II).
- RAI, human review, merge, and scribe stages are the existing workflow stages from Feature 001's run pipeline; this feature reuses them and changes only *where* the collective gates apply.
- Branch/worktree isolation and sandboxing reuse Features 001/002; the coordinator decides *which* strategy to apply, not new isolation primitives.
- The outcome spec is a lightweight, in-run artifact reviewed inline before dispatch and persisted (with the work plan) to the team's memory/decision store; it is not assumed to create a committed spec-kit `specs/NNN` directory unless clarified otherwise.
- A human remains accountable for every run (Constitution Principles IX/X); the coordinator routes accountability and approvals to that human and never bypasses approval gates.
- The coordinator never performs domain work itself; it always delegates to a roster subagent (orchestration-only).
